using System.Diagnostics;
using System.Net;
using System.Text;
using TouchSocket.Core;
using TouchSocket.Http;
using TouchSocket.Rpc;
using TouchSocket.Sockets;

/// <summary>
/// 提供本机管理页面和机器人设置接口。
/// </summary>
internal sealed class AdminWebServer : IAsyncDisposable
{
    private readonly BotSettingsStore m_settingsStore;
    private readonly ConversationStore m_conversationStore;
    private readonly HttpService m_service = new();
    private string? m_pageContent;
    private readonly int m_port;

    public AdminWebServer(BotSettingsStore settingsStore, ConversationStore conversationStore)
    {
        this.m_settingsStore = settingsStore;
        this.m_conversationStore = conversationStore;
        this.m_port = settingsStore.GetAdminPort();
    }

    public string DisplayUrl => $"http://127.0.0.1:{this.m_port}/";

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await using (var stream = typeof(AdminWebServer).Assembly.GetManifestResourceStream("QodexBot.wwwroot.admin.html")
            ?? throw new InvalidOperationException("程序集资源中找不到管理页面 QodexBot.wwwroot.admin.html。"))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            this.m_pageContent = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var config = new TouchSocketConfig()
            .SetListenIPHosts(new IPHost($"127.0.0.1:{this.m_port}"))
            .ConfigureContainer(container => container.AddRpcStore(store =>
            {
                store.RegisterServer(new AdminApiController(this.m_settingsStore, this.m_conversationStore, this.m_port));
            }))
            .ConfigurePlugins(plugins =>
            {
                plugins.AddHttpPlugin(this.OnHttpRequestAsync);
                plugins.UseWebApi(options => options.ConfigureConverter(converter =>
                {
                    converter.Clear();
                    converter.AddSystemTextJsonSerializerFormatter(jsonOptions =>
                        jsonOptions.TypeInfoResolverChain.Insert(0, WebJsonContext.Default));
                }));
                plugins.UseDefaultHttpServicePlugin();
            });
        cancellationToken.ThrowIfCancellationRequested();
        await this.m_service.SetupAsync(config).ConfigureAwait(false);
        await this.m_service.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"无法自动打开浏览器，请手动访问管理地址。原因：{ex.Message}");
        }
    }

    private async Task OnHttpRequestAsync(IHttpSessionClient client, HttpContextEventArgs e)
    {
        _ = client;
        var request = e.Context.Request;
        var path = request.RelativeURL;

        try
        {
            if (path is not ("/" or "/index.html"))
            {
                await e.InvokeNext().ConfigureAwait(false);
                return;
            }

            if (!request.Method.ToString().Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                await SendTextAsync(e, 405, "管理页面只支持 GET 请求。").ConfigureAwait(false);
                return;
            }

            await SendHtmlAsync(e, 200, this.m_pageContent ?? string.Empty).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"管理页面请求失败：{ex.Message}");
            if (!e.Context.Response.Responsed)
                await SendTextAsync(e, 500, "服务器处理请求失败。").ConfigureAwait(false);
        }
    }

    private static async Task SendTextAsync(HttpContextEventArgs e, int statusCode, string body)
    {
        var response = e.Context.Response;
        response.SetStatus(statusCode, statusCode is >= 200 and < 300 ? "OK" : "Request Failed").FromText(body);
        await response.AnswerAsync().ConfigureAwait(false);
    }

    private static async Task SendHtmlAsync(HttpContextEventArgs e, int statusCode, string body)
    {
        var response = e.Context.Response;
        response.SetStatus(statusCode, statusCode is >= 200 and < 300 ? "OK" : "Request Failed").FromHtml(body);
        await response.AnswerAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await this.m_service.StopAsync(CancellationToken.None).ConfigureAwait(false);
        this.m_service.Dispose();
    }
}
