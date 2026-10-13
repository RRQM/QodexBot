using System.Threading.Channels;
using System.Text.Json;

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
    private readonly Channel<BotConfig?> m_changes = Channel.CreateBounded<BotConfig?>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });
    private BotSettingsDocument m_settings = new();
    private string? m_configurationError;
    private string m_runtimeStatus = "等待机器人配置";

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
                    this.m_settings = settings;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            this.m_configurationError = $"无法读取 JSON 配置：{ex.Message}";
            Console.Error.WriteLine(this.m_configurationError);
        }

        _ = this.GetBotConfig();
    }

    public BotConfig? GetBotConfig()
    {
        BotSettingsDocument settings;
        lock (this.m_sync)
        {
            settings = this.m_settings;
        }

        if (string.IsNullOrWhiteSpace(settings.AppId) || string.IsNullOrWhiteSpace(settings.AppSecret))
        {
            return null;
        }

        try
        {
            Validate(settings);
            this.m_configurationError = null;
            var intents = settings.SubscribeC2cMessages ? 1UL << 25 : 0;
            return new BotConfig(settings.AppId, settings.AppSecret, settings.Sandbox, intents);
        }
        catch (ArgumentException ex)
        {
            this.m_configurationError = ex.Message;
            return null;
        }
    }

    public BotSettingsView GetView()
    {
        BotSettingsDocument settings;
        lock (this.m_sync)
        {
            settings = this.m_settings;
        }

        var configured = this.GetBotConfig() is not null;
        return new BotSettingsView(settings.AppId, settings.Sandbox, settings.SubscribeC2cMessages,
            settings.AdminPort, !string.IsNullOrEmpty(settings.AppSecret), configured,
            this.m_configurationError ?? string.Empty, NormalizeCommandBindings(settings.CommandBindings));
    }

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

            var settings = new BotSettingsDocument
            {
                AppId = input.AppId?.Trim() ?? string.Empty,
                AppSecret = string.IsNullOrWhiteSpace(input.AppSecret) ? current.AppSecret : input.AppSecret,
                Sandbox = input.Sandbox,
                SubscribeC2cMessages = input.SubscribeC2cMessages,
                AdminPort = input.AdminPort,
                CommandBindings = NormalizeCommandBindings(input.CommandBindings ?? current.CommandBindings),
            };
            Validate(settings, credentialsRequired: false);

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
            var config = this.GetBotConfig();
            this.m_changes.Writer.TryWrite(config);
            return this.GetView();
        }
        finally
        {
            this.m_saveLock.Release();
        }
    }

    public async Task<BotSettingsView> SaveQrCredentialsAsync(string appId, string appSecret, CancellationToken cancellationToken)
    {
        await this.m_saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BotSettingsDocument current;
            lock (this.m_sync)
            {
                current = this.m_settings;
            }

            var settings = new BotSettingsDocument
            {
                AppId = appId.Trim(),
                AppSecret = appSecret,
                Sandbox = current.Sandbox,
                SubscribeC2cMessages = current.SubscribeC2cMessages,
                AdminPort = current.AdminPort,
                CommandBindings = NormalizeCommandBindings(current.CommandBindings),
            };
            Validate(settings);
            await this.SaveDocumentAsync(settings, cancellationToken).ConfigureAwait(false);

            lock (this.m_sync)
            {
                this.m_settings = settings;
            }
            this.m_configurationError = null;
            this.m_changes.Writer.TryWrite(this.GetBotConfig());
            return this.GetView();
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

    public IAsyncEnumerable<BotConfig?> ReadChangesAsync(CancellationToken cancellationToken) =>
        this.m_changes.Reader.ReadAllAsync(cancellationToken);

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

    private static Dictionary<string, string> NormalizeCommandBindings(Dictionary<string, string>? supplied)
    {
        var result = DefaultCommandBindings.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (supplied is null) return result;
        foreach (var (key, value) in supplied)
            if (DefaultCommandBindings.ContainsKey(key)) result[key] = value?.Trim() ?? string.Empty;
        return result;
    }

    internal sealed class BotSettingsDocument
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
    }
}

internal sealed record BotSettingsInput(
    string? AppId,
    string? AppSecret,
    bool Sandbox,
    bool SubscribeC2cMessages,
    int AdminPort,
    Dictionary<string, string>? CommandBindings = null);

internal sealed record BotSettingsView(
    string AppId,
    bool Sandbox,
    bool SubscribeC2cMessages,
    int AdminPort,
    bool SecretConfigured,
    bool IsConfigured,
    string ConfigurationError,
    Dictionary<string, string> CommandBindings);
