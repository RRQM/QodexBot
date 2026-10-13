using System.Text.Json;
using System.Collections.Concurrent;

internal sealed class BotSettingsStore(string filePath)
{
    internal static readonly IReadOnlyDictionary<string, string> DefaultCommandBindings =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["help"] = "\\",
            ["status"] = "\\状态",
            ["sessions"] = "\\会话",
            ["switch-session"] = "\\切换",
            ["new-session"] = "\\新建",
            ["recent"] = "\\最近",
            ["current"] = "\\当前",
            ["rename"] = "\\标题",
            ["workdir"] = "\\工作目录",
            ["screenshot"] = "\\截图",
            ["retry"] = "\\重试",
            ["stop"] = "\\停止",
            ["models"] = "\\模型列表",
            ["switch-model"] = "\\模型",
            ["update-codex"] = "\\更新Codex",
        };

    private readonly object m_sync = new();
    private readonly SemaphoreSlim m_saveLock = new(1, 1);
    private readonly SemaphoreSlim m_changeSignal = new(0, 1);
    private BotSettingsDocument m_settings = new();
    private string? m_configurationError;
    private string m_runtimeStatus = "等待机器人配置";
    private readonly ConcurrentDictionary<string, string> m_botStatuses = new(StringComparer.Ordinal);

    public string FilePath { get; } = Path.GetFullPath(filePath);
    public string RuntimeStatus => Volatile.Read(ref m_runtimeStatus);

    public int GetAdminPort()
    {
        lock (this.m_sync)
        {
            return this.m_settings.AdminPort is > 0 and <= 65535 ? this.m_settings.AdminPort : 17890;
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(this.FilePath)) return;

        try
        {
            var json = await File.ReadAllTextAsync(this.FilePath, cancellationToken).ConfigureAwait(false);
            var settings = JsonSerializer.Deserialize(json, WebJsonContext.Default.BotSettingsDocument);
            if (settings is not null)
            {
                lock (this.m_sync)
                {
                    if (settings.Bots.Count == 0 && !string.IsNullOrWhiteSpace(settings.AppId))
                    {
                        settings.Bots.Add(new BotProfileDocument { Id = "default", Name = "QQ 机器人 1", AppId = settings.AppId,
                            AppSecret = settings.AppSecret, Sandbox = settings.Sandbox,
                            SubscribeC2cMessages = settings.SubscribeC2cMessages, CommandBindings = settings.CommandBindings });
                        settings.AppId = string.Empty;
                        settings.AppSecret = string.Empty;
                    }
                    this.m_settings = settings;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            this.m_configurationError = $"无法读取 JSON 配置：{ex.Message}";
            Console.Error.WriteLine(this.m_configurationError);
        }

        _ = this.GetBots();
    }

    public BotConfig? GetBotConfig()
    {
        return this.GetBotConfigs().FirstOrDefault();
    }

    public BotSettingsView GetView()
    {
        BotSettingsDocument settings;
        lock (this.m_sync)
        {
            settings = this.m_settings;
        }

        var bot = settings.Bots.FirstOrDefault();
        if (bot is null)
            return new BotSettingsView("", "QQ 机器人", "", false, true, settings.AdminPort, false, false,
                this.m_configurationError ?? "", NormalizeCommandBindings(null), "等待扫码绑定");
        return ToView(bot, settings.AdminPort);
    }

    public IReadOnlyList<BotSettingsView> GetBots()
    {
        BotSettingsDocument settings;
        lock (this.m_sync) settings = this.m_settings;
        return settings.Bots.Select(bot => ToView(bot, settings.AdminPort)).ToArray();
    }

    public BotConfig[] GetBotConfigs()
    {
        BotSettingsDocument settings;
        lock (this.m_sync) settings = this.m_settings;
        var configs = new List<BotConfig>();
        foreach (var bot in settings.Bots)
        {
            if (string.IsNullOrWhiteSpace(bot.AppId) || string.IsNullOrWhiteSpace(bot.AppSecret)) continue;
            try
            {
                Validate(bot);
                configs.Add(new BotConfig(bot.Id, bot.Name, bot.AppId, bot.AppSecret, bot.Sandbox,
                    bot.SubscribeC2cMessages ? 1UL << 25 : 0));
            }
            catch (ArgumentException ex)
            {
                this.m_configurationError = ex.Message;
            }
        }
        return configs.ToArray();
    }

    public BotSettingsView? GetBot(string botId)
    {
        BotSettingsDocument settings;
        lock (this.m_sync) settings = this.m_settings;
        var bot = settings.Bots.FirstOrDefault(item => item.Id == botId);
        return bot is null ? null : ToView(bot, settings.AdminPort);
    }

    public void SetBotRuntimeStatus(string botId, string status) => m_botStatuses[botId] = status;
    public string GetBotRuntimeStatus(string botId) => m_botStatuses.GetValueOrDefault(botId, "等待机器人配置");

    public IReadOnlyDictionary<string, string> GetCommandBindings()
    {
        lock (this.m_sync) return NormalizeCommandBindings(this.m_settings.CommandBindings);
    }

    public async Task<BotSettingsView> SaveAsync(BotSettingsInput input, CancellationToken cancellationToken)
    {
        await this.m_saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BotSettingsDocument current;
            lock (this.m_sync)
            {
                current = this.m_settings;
            }

            var botId = string.IsNullOrWhiteSpace(input.BotId) ? current.Bots.FirstOrDefault()?.Id : input.BotId.Trim();
            var bot = current.Bots.FirstOrDefault(item => item.Id == botId)
                ?? throw new ArgumentException("找不到要修改的机器人。");
            var updatedBot = bot with
            {
                Name = string.IsNullOrWhiteSpace(input.Name) ? bot.Name : input.Name.Trim(),
                AppId = input.AppId?.Trim() ?? string.Empty,
                AppSecret = string.IsNullOrWhiteSpace(input.AppSecret) ? bot.AppSecret : input.AppSecret,
                Sandbox = input.Sandbox,
                SubscribeC2cMessages = input.SubscribeC2cMessages,
                CommandBindings = NormalizeCommandBindings(input.CommandBindings ?? bot.CommandBindings),
            };
            Validate(updatedBot, credentialsRequired: false);
            if (input.AdminPort is <= 0 or > 65535) throw new ArgumentException("管理端口必须在 1 到 65535 之间。");
            if (updatedBot.Name.Length > 64 || updatedBot.Name.Any(char.IsControl)) throw new ArgumentException("机器人名称最多 64 个字符，且不能包含控制字符。");
            var settings = current with { AdminPort = input.AdminPort, Bots = current.Bots.Select(item => item.Id == botId ? updatedBot : item).ToList() };

            var directory = Path.GetDirectoryName(this.FilePath)
                ?? throw new InvalidOperationException("配置文件路径无效。");
            Directory.CreateDirectory(directory);
            var temporaryPath = this.FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var json = JsonSerializer.Serialize(settings, WebJsonContext.Default.BotSettingsDocument);
                await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
                File.Move(temporaryPath, this.FilePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }

            lock (this.m_sync)
            {
                this.m_settings = settings;
            }
            this.m_configurationError = null;
            SignalChanges();
            return ToView(updatedBot, settings.AdminPort);
        }
        finally
        {
            this.m_saveLock.Release();
        }
    }

    public async Task<BotSettingsView> AddBotAsync(string name, CancellationToken cancellationToken)
    {
        await this.m_saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BotSettingsDocument current;
            lock (this.m_sync) current = this.m_settings;
            var profile = new BotProfileDocument { Id = Guid.NewGuid().ToString("N"), Name = string.IsNullOrWhiteSpace(name) ? $"QQ 机器人 {current.Bots.Count + 1}" : name.Trim() };
            var settings = current with { Bots = [.. current.Bots, profile] };
            await SaveDocumentAsync(settings, cancellationToken).ConfigureAwait(false);
            lock (this.m_sync) this.m_settings = settings;
            SignalChanges();
            return ToView(profile, settings.AdminPort);
        }
        finally { this.m_saveLock.Release(); }
    }

    public async Task DeleteBotAsync(string botId, CancellationToken cancellationToken)
    {
        await this.m_saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BotSettingsDocument current;
            lock (this.m_sync) current = this.m_settings;
            if (!current.Bots.Any(item => item.Id == botId)) throw new ArgumentException("找不到要删除的机器人。");
            var settings = current with { Bots = current.Bots.Where(item => item.Id != botId).ToList() };
            await SaveDocumentAsync(settings, cancellationToken).ConfigureAwait(false);
            lock (this.m_sync) this.m_settings = settings;
            m_botStatuses.TryRemove(botId, out _);
            SignalChanges();
        }
        finally { this.m_saveLock.Release(); }
    }

    public async Task<BotSettingsView> SaveQrCredentialsAsync(string botId, string appId, string appSecret, CancellationToken cancellationToken)
    {
        await this.m_saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BotSettingsDocument current;
            lock (this.m_sync)
            {
                current = this.m_settings;
            }

            var profile = current.Bots.FirstOrDefault(item => item.Id == botId) ?? throw new ArgumentException("机器人不存在。");
            var updatedBot = profile with { AppId = appId.Trim(), AppSecret = appSecret };
            Validate(updatedBot);
            var settings = current with { Bots = current.Bots.Select(item => item.Id == botId ? updatedBot : item).ToList() };
            await this.SaveDocumentAsync(settings, cancellationToken).ConfigureAwait(false);

            lock (this.m_sync)
            {
                this.m_settings = settings;
            }
            this.m_configurationError = null;
            SignalChanges();
            return ToView(updatedBot, settings.AdminPort);
        }
        finally
        {
            this.m_saveLock.Release();
        }
    }

    private async Task SaveDocumentAsync(BotSettingsDocument settings, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(this.FilePath)
            ?? throw new InvalidOperationException("配置文件路径无效。");
        Directory.CreateDirectory(directory);
        var temporaryPath = this.FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(settings, WebJsonContext.Default.BotSettingsDocument);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, this.FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public async IAsyncEnumerable<bool> ReadChangesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await m_changeSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
            yield return true;
        }
    }

    private void SignalChanges()
    {
        if (m_changeSignal.CurrentCount == 0) m_changeSignal.Release();
    }

    public void SetRuntimeStatus(string status)
    {
        Volatile.Write(ref this.m_runtimeStatus, status);
    }

    private static void Validate(BotSettingsDocument settings, bool credentialsRequired = true)
    {
        if (credentialsRequired && string.IsNullOrWhiteSpace(settings.AppId)) throw new ArgumentException("AppID 不能为空。");
        if (credentialsRequired && string.IsNullOrWhiteSpace(settings.AppSecret)) throw new ArgumentException("AppSecret 不能为空。");
        if (settings.AdminPort is <= 0 or > 65535) throw new ArgumentException("管理端口必须在 1 到 65535 之间。");
        var commands = settings.CommandBindings.Values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()).ToArray();
        if (commands.Any(value => value.Length > 32 || value.Any(char.IsControl)))
            throw new ArgumentException("指令命令最多 32 个字符，且不能包含换行或控制字符。");
        if (commands.Distinct(StringComparer.OrdinalIgnoreCase).Count() != commands.Length)
            throw new ArgumentException("不同动作不能绑定相同的命令。");
    }

    private static void Validate(BotProfileDocument settings, bool credentialsRequired = true)
    {
        if (credentialsRequired && string.IsNullOrWhiteSpace(settings.AppId)) throw new ArgumentException("AppID 不能为空。");
        if (credentialsRequired && string.IsNullOrWhiteSpace(settings.AppSecret)) throw new ArgumentException("AppSecret 不能为空。");
        var commands = settings.CommandBindings.Values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToArray();
        if (commands.Any(value => value.Length > 32 || value.Any(char.IsControl))) throw new ArgumentException("指令命令最多 32 个字符，且不能包含换行或控制字符。");
        if (commands.Distinct(StringComparer.OrdinalIgnoreCase).Count() != commands.Length) throw new ArgumentException("不同动作不能绑定相同的命令。");
    }

    private BotSettingsView ToView(BotProfileDocument bot, int adminPort) => new(bot.Id, bot.Name, bot.AppId, bot.Sandbox,
        bot.SubscribeC2cMessages, adminPort, !string.IsNullOrEmpty(bot.AppSecret), !string.IsNullOrEmpty(bot.AppId) && !string.IsNullOrEmpty(bot.AppSecret),
        string.Empty, NormalizeCommandBindings(bot.CommandBindings), GetBotRuntimeStatus(bot.Id));

    private static Dictionary<string, string> NormalizeCommandBindings(Dictionary<string, string>? supplied)
    {
        var result = DefaultCommandBindings.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (supplied is null) return result;
        foreach (var (key, value) in supplied)
            if (DefaultCommandBindings.ContainsKey(key)) result[key] = value?.Trim() ?? string.Empty;
        return result;
    }

    internal sealed record BotSettingsDocument
    {
        public BotSettingsDocument()
        {
        }

        public string AppId { get; set; } = string.Empty;
        public string AppSecret { get; set; } = string.Empty;
        public bool Sandbox { get; set; }
        public bool SubscribeC2cMessages { get; set; } = true;
        public int AdminPort { get; set; } = 17890;
        public Dictionary<string, string> CommandBindings { get; set; } = DefaultCommandBindings
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        public List<BotProfileDocument> Bots { get; set; } = [];
    }

    internal sealed record BotProfileDocument
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        public string Name { get; init; } = "QQ 机器人";
        public string AppId { get; init; } = string.Empty;
        public string AppSecret { get; init; } = string.Empty;
        public bool Sandbox { get; init; }
        public bool SubscribeC2cMessages { get; init; } = true;
        public Dictionary<string, string> CommandBindings { get; init; } = DefaultCommandBindings.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }
}

internal sealed record BotSettingsInput(
    string? AppId,
    string? AppSecret,
    bool Sandbox,
    bool SubscribeC2cMessages,
    int AdminPort,
    Dictionary<string, string>? CommandBindings = null,
    string? BotId = null,
    string? Name = null);

internal sealed record BotSettingsView(
    string BotId,
    string Name,
    string AppId,
    bool Sandbox,
    bool SubscribeC2cMessages,
    int AdminPort,
    bool SecretConfigured,
    bool IsConfigured,
    string ConfigurationError,
    Dictionary<string, string> CommandBindings,
    string RuntimeStatus);
