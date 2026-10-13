if (args.Length == 2 && args[0] == "--mcp-server")
{
    await McpStdioWorker.RunAsync(args[1]);
    return 0;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

var runtimeDirectory = AppContext.BaseDirectory;
var settingsPath = Path.Combine(runtimeDirectory, "settings.json");
var conversationsPath = Path.Combine(runtimeDirectory, "conversations.jsonl");

var settingsStore = new BotSettingsStore(settingsPath);
await settingsStore.LoadAsync(shutdown.Token);
var conversationStore = new ConversationStore(conversationsPath);
await conversationStore.LoadAsync(shutdown.Token);

Console.WriteLine("QodexBot 管理服务启动中…");
Console.WriteLine($"JSON 配置文件：{settingsStore.FilePath}");
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
await using var codex = new CodexAppServer();
await using var mcp = new LocalToolIpcServer();
await mcp.StartAsync();
await using var admin = new AdminWebServer(settingsStore, conversationStore);
await admin.StartAsync(shutdown.Token);
Console.WriteLine($"管理页面已启动：{admin.DisplayUrl}");
Console.WriteLine("管理服务仅监听本机地址；按 Ctrl+C 退出。\n");
AdminWebServer.OpenBrowser(admin.DisplayUrl);

var supervisor = new QqBotSupervisor(http, settingsStore, codex, mcp, conversationStore);
await supervisor.RunAsync(shutdown.Token);
return 0;
