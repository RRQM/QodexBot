internal sealed class QqBotSupervisor(
    HttpClient http,
    BotSettingsStore settingsStore,
    ConversationStore conversationStore,
    BotSessionStore sessionStore)
{
    private readonly Dictionary<string, ActiveBot> m_activeBots = new(StringComparer.Ordinal);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await this.ReconcileAsync(cancellationToken).ConfigureAwait(false);
            await foreach (var _ in settingsStore.ReadChangesAsync(cancellationToken).ConfigureAwait(false))
                await this.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            foreach (var botId in m_activeBots.Keys.ToArray()) await StopBotAsync(botId).ConfigureAwait(false);
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var configs = settingsStore.GetBotConfigs().ToDictionary(item => item.BotId, StringComparer.Ordinal);
        foreach (var (botId, active) in m_activeBots.ToArray())
        {
            if (configs.TryGetValue(botId, out var current) && current == active.Config) continue;
            await StopBotAsync(botId).ConfigureAwait(false);
        }

        if (configs.Count == 0)
            settingsStore.SetRuntimeStatus("请在管理页添加机器人并扫码绑定 QQ。");
        foreach (var (botId, config) in configs)
        {
            if (m_activeBots.ContainsKey(botId)) continue;
            var active = new ActiveBot(config);
            m_activeBots.Add(botId, active);
            try
            {
                active.Codex = new CodexAppServer(botId);
                active.Mcp = new LocalToolIpcServer();
                await active.Mcp.StartAsync().ConfigureAwait(false);
                var bot = new QqBotClient(http, config, active.Codex, active.Mcp, conversationStore, sessionStore,
                    () => settingsStore.GetBot(config.BotId)?.CommandBindings ?? BotSettingsStore.DefaultCommandBindings,
                    () => settingsStore.GetBotRuntimeStatus(botId), status => settingsStore.SetBotRuntimeStatus(botId, status));
                active.RunTask = bot.RunAsync(active.Cancellation.Token);
                settingsStore.SetBotRuntimeStatus(botId, "正在使用已保存的设置启动 QQ 机器人。");
                Console.WriteLine($"启动机器人：{config.Name} ({botId})");
            }
            catch (Exception ex)
            {
                settingsStore.SetBotRuntimeStatus(botId, $"启动机器人失败：{ex.Message}");
                Console.Error.WriteLine($"启动机器人失败 ({botId})：{ex.Message}");
                await StopBotAsync(botId).ConfigureAwait(false);
            }
        }
    }

    private async Task StopBotAsync(string botId)
    {
        if (!m_activeBots.Remove(botId, out var active)) return;
        active.Cancellation.Cancel();
        try { await active.RunTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.Error.WriteLine($"停止机器人 {botId} 失败：{ex.Message}"); }
        finally
        {
            active.Cancellation.Dispose();
            if (active.Mcp is not null) await active.Mcp.DisposeAsync().ConfigureAwait(false);
            if (active.Codex is not null) await active.Codex.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class ActiveBot(BotConfig config)
    {
        public BotConfig Config { get; } = config;
        public CancellationTokenSource Cancellation { get; } = new();
        public CodexAppServer? Codex { get; set; }
        public LocalToolIpcServer? Mcp { get; set; }
        public Task RunTask { get; set; } = Task.CompletedTask;
    }
}
