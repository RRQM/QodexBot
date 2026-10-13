using System.Threading.Channels;
using System.Text.Json;

internal sealed class BotSettingsStore(string filePath)
{
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
            this.m_configurationError ?? string.Empty);
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
            };
            Validate(settings);

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

    public IAsyncEnumerable<BotConfig?> ReadChangesAsync(CancellationToken cancellationToken) =>
        this.m_changes.Reader.ReadAllAsync(cancellationToken);

    public void SetRuntimeStatus(string status)
    {
        Volatile.Write(ref this.m_runtimeStatus, status);
    }

    private static void Validate(BotSettingsDocument settings)
    {
        if (string.IsNullOrWhiteSpace(settings.AppId)) throw new ArgumentException("AppID 不能为空。");
        if (string.IsNullOrWhiteSpace(settings.AppSecret)) throw new ArgumentException("AppSecret 不能为空。");
        if (settings.AdminPort is <= 0 or > 65535) throw new ArgumentException("管理端口必须在 1 到 65535 之间。");

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
    }
}

internal sealed record BotSettingsInput(
    string? AppId,
    string? AppSecret,
    bool Sandbox,
    bool SubscribeC2cMessages,
    int AdminPort);

internal sealed record BotSettingsView(
    string AppId,
    bool Sandbox,
    bool SubscribeC2cMessages,
    int AdminPort,
    bool SecretConfigured,
    bool IsConfigured,
    string ConfigurationError);
