internal sealed class QqBotSupervisor(
    HttpClient http,
    BotSettingsStore settingsStore,
    CodexAppServer codex,
    LocalToolIpcServer mcp,
    ConversationStore conversationStore)
{
    private readonly BotSettingsStore m_settingsStore = settingsStore;
    private ActiveBot? m_activeBot;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await this.ApplySettingsAsync(this.m_settingsStore.GetBotConfig(), cancellationToken).ConfigureAwait(false);
            await foreach (var config in this.m_settingsStore.ReadChangesAsync(cancellationToken).ConfigureAwait(false))
            {
                await this.ApplySettingsAsync(config, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await this.StopBotAsync().ConfigureAwait(false);
        }
    }

    private async Task ApplySettingsAsync(BotConfig? config, CancellationToken cancellationToken)
    {
        await this.StopBotAsync().ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested) return;

        if (config is null)
        {
            var view = this.m_settingsStore.GetView();
            this.m_settingsStore.SetRuntimeStatus(string.IsNullOrWhiteSpace(view.ConfigurationError)
                ? "请在管理页填写 QQ AppID 和 AppSecret。"
                : $"配置有误：{view.ConfigurationError}");
            return;
        }

        try
        {
            var botCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var bot = new QqBotClient(http, config, codex, mcp, conversationStore, this.m_settingsStore.SetRuntimeStatus);
            var runTask = bot.RunAsync(botCancellation.Token);
            this.m_activeBot = new ActiveBot(botCancellation, runTask);
            this.m_settingsStore.SetRuntimeStatus("正在使用已保存的设置启动 QQ 机器人。");
        }
        catch (Exception ex)
        {
            this.m_settingsStore.SetRuntimeStatus($"启动机器人失败：{ex.Message}");
            Console.Error.WriteLine($"启动机器人失败：{ex.Message}");
        }
    }

    private async Task StopBotAsync()
    {
        var activeBot = this.m_activeBot;
        this.m_activeBot = null;
        if (activeBot is null) return;

        activeBot.Cancellation.Cancel();
        try
        {
            await activeBot.RunTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"停止旧机器人实例失败：{ex.Message}");
        }
        finally
        {
            activeBot.Cancellation.Dispose();
        }
    }

    private sealed record ActiveBot(
        CancellationTokenSource Cancellation,
        Task RunTask);
}
