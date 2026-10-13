using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Web;
using TouchSocket.Http;
using TouchSocket.Rpc;
using TouchSocket.WebApi;

/// <summary>
/// 通过 TouchSocket.WebApi 提供管理页面所需的 JSON 接口。
/// </summary>
internal sealed partial class AdminApiController : SingletonRpcServer
{
    private readonly BotSettingsStore m_settingsStore;
    private readonly ConversationStore m_conversationStore;
    private readonly QqQrLoginService m_qrLoginService;
    private readonly int m_activePort;
    public AdminApiController(BotSettingsStore settingsStore, ConversationStore conversationStore,
        QqQrLoginService qrLoginService, int activePort)
    {
        this.m_settingsStore = settingsStore;
        this.m_conversationStore = conversationStore;
        this.m_qrLoginService = qrLoginService;
        this.m_activePort = activePort;
    }

    /// <summary>
    /// 获取已保存的机器人设置。
    /// </summary>
    [Router("/api/settings")]
    [WebApi(Method = HttpMethodType.Get)]
    public Task GetSettings(IWebApiCallContext callContext) =>
        this.HandleAsync(callContext, async () =>
        {
            await this.WriteJsonAsync(callContext, 200, this.m_settingsStore.GetView(),
                WebJsonContext.Default.BotSettingsView).ConfigureAwait(false);
        });

    /// <summary>
    /// 保存机器人设置并通知运行中的机器人应用变更。
    /// </summary>
    [Router("/api/settings")]
    [WebApi(Method = HttpMethodType.Post)]
    public Task SaveSettings(IWebApiCallContext callContext) =>
        this.HandleAsync(callContext, async () =>
        {
            var body = await callContext.HttpContext.Request.GetContentAsync(CancellationToken.None).ConfigureAwait(false);
            if (body.Length > 64 * 1024)
            {
                await this.WriteJsonAsync(callContext, 413, new ApiErrorResponse("配置请求超过 64 KiB。"),
                    WebJsonContext.Default.ApiErrorResponse).ConfigureAwait(false);
                return;
            }

            var input = JsonSerializer.Deserialize(Encoding.UTF8.GetString(body.Span), WebJsonContext.Default.BotSettingsInput);
            if (input is null)
            {
                await this.WriteJsonAsync(callContext, 400, new ApiErrorResponse("配置内容为空或格式无效。"),
                    WebJsonContext.Default.ApiErrorResponse).ConfigureAwait(false);
                return;
            }

            var saved = await this.m_settingsStore.SaveAsync(input, CancellationToken.None).ConfigureAwait(false);
            var message = saved.AdminPort == this.m_activePort
                ? "设置已保存，机器人正在应用新配置。"
                : "设置已保存，机器人正在应用新配置；管理端口将在重启程序后生效。";
            await this.WriteJsonAsync(callContext, 200, new ApiSaveSettingsResponse(saved, message),
                WebJsonContext.Default.ApiSaveSettingsResponse).ConfigureAwait(false);
        });

    /// <summary>
    /// 获取机器人连接状态和本地配置文件路径。
    /// </summary>
    [Router("/api/status")]
    [WebApi(Method = HttpMethodType.Get)]
    public Task GetStatus(IWebApiCallContext callContext) =>
        this.HandleAsync(callContext, async () =>
        {
            var response = new ApiStatusResponse(this.m_settingsStore.RuntimeStatus, this.m_settingsStore.GetView(),
                this.m_settingsStore.FilePath);
            await this.WriteJsonAsync(callContext, 200, response, WebJsonContext.Default.ApiStatusResponse)
                .ConfigureAwait(false);
        });

    /// <summary>
    /// 启动 QQ 扫码绑定流程。
    /// </summary>
    [Router("/api/qq-login")]
    [WebApi(Method = HttpMethodType.Post)]
    public Task StartQqLogin(IWebApiCallContext callContext) =>
        this.HandleAsync(callContext, async () =>
        {
            var session = await this.m_qrLoginService.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await this.WriteJsonAsync(callContext, 200, session, WebJsonContext.Default.QqQrLoginView).ConfigureAwait(false);
        });

    /// <summary>
    /// 查询 QQ 扫码绑定状态。
    /// </summary>
    [Router("/api/qq-login")]
    [WebApi(Method = HttpMethodType.Get)]
    public Task GetQqLogin(IWebApiCallContext callContext) =>
        this.HandleAsync(callContext, async () =>
        {
            var requestUrl = callContext.HttpContext.Request.URL;
            if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri))
                uri = new Uri(new Uri($"http://127.0.0.1:{this.m_activePort}/"), requestUrl);
            var query = HttpUtility.ParseQueryString(uri.Query);
            var sessionId = query.Get("sessionId") ?? string.Empty;
            var session = this.m_qrLoginService.Get(sessionId);
            await this.WriteJsonAsync(callContext, 200, session, WebJsonContext.Default.QqQrLoginView).ConfigureAwait(false);
        });

    /// <summary>
    /// 取消正在进行的 QQ 扫码绑定。
    /// </summary>
    [Router("/api/qq-login/cancel")]
    [WebApi(Method = HttpMethodType.Post)]
    public Task CancelQqLogin(IWebApiCallContext callContext) =>
        this.HandleAsync(callContext, async () =>
        {
            var body = await callContext.HttpContext.Request.GetContentAsync(CancellationToken.None).ConfigureAwait(false);
            var input = JsonSerializer.Deserialize(Encoding.UTF8.GetString(body.Span), WebJsonContext.Default.QqQrLoginCancelInput);
            if (input is null)
                throw new ArgumentException("扫码会话 ID 缺失。");
            var session = await this.m_qrLoginService.CancelAsync(input.SessionId).ConfigureAwait(false);
            await this.WriteJsonAsync(callContext, 200, session, WebJsonContext.Default.QqQrLoginView).ConfigureAwait(false);
        });

    /// <summary>
    /// 获取本机保存的会话记录，支持向较早记录翻页和按用户 OpenID 筛选。
    /// </summary>
    [Router("/api/conversations")]
    [WebApi(Method = HttpMethodType.Get)]
    public Task GetConversations(IWebApiCallContext callContext) =>
        this.HandleAsync(callContext, async () =>
        {
            var requestUrl = callContext.HttpContext.Request.URL;
            if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri))
                uri = new Uri(new Uri($"http://127.0.0.1:{this.m_activePort}/"), requestUrl);
            var query = HttpUtility.ParseQueryString(uri.Query);
            long? beforeId = long.TryParse(query.Get("beforeId"), out var parsedBeforeId) ? parsedBeforeId : null;
            var limit = int.TryParse(query.Get("limit"), out var parsedLimit) ? parsedLimit : 50;
            var userOpenId = query.Get("userOpenId");
            var page = this.m_conversationStore.GetPage(beforeId, limit, userOpenId);
            await this.WriteJsonAsync(callContext, 200, page, WebJsonContext.Default.ConversationPage)
                .ConfigureAwait(false);
        });

    private async Task HandleAsync(IWebApiCallContext callContext, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            await this.WriteJsonAsync(callContext, 400, new ApiErrorResponse(ex.Message),
                WebJsonContext.Default.ApiErrorResponse).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            await this.WriteJsonAsync(callContext, 400, new ApiErrorResponse(ex.Message),
                WebJsonContext.Default.ApiErrorResponse).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            await this.WriteJsonAsync(callContext, 400, new ApiErrorResponse($"JSON 格式无效：{ex.Message}"),
                WebJsonContext.Default.ApiErrorResponse).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"管理 API 请求失败：{ex.Message}");
            if (!callContext.HttpContext.Response.Responsed)
                await this.WriteJsonAsync(callContext, 500, new ApiErrorResponse("服务器处理请求失败。"),
                    WebJsonContext.Default.ApiErrorResponse).ConfigureAwait(false);
        }
    }

    private async Task WriteJsonAsync<T>(IWebApiCallContext callContext, int statusCode, T body,
        JsonTypeInfo<T> jsonTypeInfo)
    {
        var response = callContext.HttpContext.Response;
        response.SetStatus(statusCode, statusCode is >= 200 and < 300 ? "OK" : "Request Failed")
            .FromJson(JsonSerializer.Serialize(body, jsonTypeInfo));
        await response.AnswerAsync().ConfigureAwait(false);
    }
}
