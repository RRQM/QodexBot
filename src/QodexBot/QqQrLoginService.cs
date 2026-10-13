using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QRCoder;

/// <summary>
/// 管理 QQ 机器人扫码绑定会话，并在绑定成功后保存平台凭据。
/// </summary>
internal sealed class QqQrLoginService(BotSettingsStore settingsStore, HttpClient httpClient) : IAsyncDisposable
{
    private const string ApiHost = "https://q.qq.com";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private readonly ConcurrentDictionary<string, LoginSession> m_sessions = new(StringComparer.Ordinal);
    private readonly BotSettingsStore m_settingsStore = settingsStore;
    private readonly HttpClient m_httpClient = httpClient;

    public async Task<QqQrLoginView> StartAsync(CancellationToken cancellationToken)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var bindTask = await this.CreateBindTaskAsync(cancellationToken).ConfigureAwait(false);
        var session = new LoginSession(sessionId, bindTask.TaskId, bindTask.Key, CreateQrSvg(BuildConnectUrl(bindTask.TaskId)));
        if (!this.m_sessions.TryAdd(sessionId, session))
            throw new InvalidOperationException("无法创建扫码会话。");

        session.WorkerTask = this.PollAsync(session);
        return session.View;
    }

    public QqQrLoginView Get(string sessionId)
    {
        if (!this.m_sessions.TryGetValue(sessionId, out var session))
            return new QqQrLoginView(sessionId, "expired", "扫码会话已失效，请重新开始。", null);

        lock (session.Sync)
            return session.View;
    }

    public async Task<QqQrLoginView> CancelAsync(string sessionId)
    {
        if (!this.m_sessions.TryGetValue(sessionId, out var session))
            return new QqQrLoginView(sessionId, "expired", "扫码会话已失效。", null);

        lock (session.Sync)
        {
            if (!session.IsTerminal)
                session.SetTerminal("cancelled", "已取消扫码绑定。", null);
        }

        session.Cancellation.Cancel();
        await session.WorkerTask.ConfigureAwait(false);
        this.m_sessions.TryRemove(sessionId, out _);
        session.Dispose();
        return session.View;
    }

    private async Task PollAsync(LoginSession session)
    {
        try
        {
            while (!session.Cancellation.IsCancellationRequested)
            {
                if (DateTimeOffset.UtcNow - session.CreatedAt >= SessionLifetime)
                {
                    lock (session.Sync)
                        if (!session.IsTerminal)
                            session.SetTerminal("expired", "二维码已过期，请重新开始。", null);
                    this.m_sessions.TryRemove(session.Id, out _);
                    return;
                }

                PollResult result;
                try
                {
                    result = await this.PollBindResultAsync(session.TaskId, session.Cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"QQ 扫码轮询失败：{ex.Message}");
                    await Task.Delay(PollInterval, session.Cancellation.Token).ConfigureAwait(false);
                    continue;
                }

                if (result.Status == BindStatus.Completed)
                {
                    var appSecret = DecryptSecret(result.EncryptedSecret, session.Key);
                    if (string.IsNullOrWhiteSpace(result.AppId) || string.IsNullOrWhiteSpace(appSecret))
                        throw new InvalidOperationException("扫码未返回有效的机器人凭据。");

                    await this.m_settingsStore.SaveQrCredentialsAsync(result.AppId, appSecret, session.Cancellation.Token)
                        .ConfigureAwait(false);
                    lock (session.Sync)
                        if (!session.IsTerminal)
                            session.SetTerminal("connected", "扫码绑定成功，机器人正在连接。", null);
                    return;
                }

                if (result.Status == BindStatus.Expired)
                {
                    var replacement = await this.CreateBindTaskAsync(session.Cancellation.Token).ConfigureAwait(false);
                    var svg = CreateQrSvg(BuildConnectUrl(replacement.TaskId));
                    lock (session.Sync)
                    {
                        if (session.IsTerminal) return;
                        session.TaskId = replacement.TaskId;
                        session.Key = replacement.Key;
                        session.View = new QqQrLoginView(session.Id, "waiting", "二维码已刷新，请使用手机 QQ 扫描。", svg);
                    }
                }

                await Task.Delay(PollInterval, session.Cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            lock (session.Sync)
            {
                if (!session.IsTerminal)
                    session.SetTerminal("failed", $"扫码绑定失败：{ex.Message}", null);
            }
        }
    }

    private async Task<BindTask> CreateBindTaskAsync(CancellationToken cancellationToken)
    {
        var keyBytes = RandomNumberGenerator.GetBytes(32);
        var key = Convert.ToBase64String(keyBytes);
        using var request = CreateJsonRequest($"{ApiHost}/lite/create_bind_task", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("key", key);
            writer.WriteEndObject();
        });
        using var response = await this.m_httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        using var document = await ReadSuccessResponseAsync(response, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        EnsureSuccess(root, "create_bind_task");
        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("task_id", out var taskIdElement))
            throw new InvalidOperationException("QQ 接口未返回绑定任务 ID。");
        var taskId = taskIdElement.GetString();
        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("QQ 接口返回的绑定任务 ID 为空。");
        return new BindTask(taskId, key);
    }

    private async Task<PollResult> PollBindResultAsync(string taskId, CancellationToken cancellationToken)
    {
        using var request = CreateJsonRequest($"{ApiHost}/lite/poll_bind_result", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("task_id", taskId);
            writer.WriteEndObject();
        });
        using var response = await this.m_httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        using var document = await ReadSuccessResponseAsync(response, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        EnsureSuccess(root, "poll_bind_result");
        if (!root.TryGetProperty("data", out var data))
            return new PollResult(BindStatus.None, string.Empty, string.Empty);

        var status = data.TryGetProperty("status", out var statusElement) && statusElement.TryGetInt32(out var statusValue)
            ? (BindStatus)statusValue
            : BindStatus.None;
        var appId = data.TryGetProperty("bot_appid", out var appIdElement) ? appIdElement.ToString() : string.Empty;
        var encryptedSecret = data.TryGetProperty("bot_encrypt_secret", out var secretElement)
            ? secretElement.GetString() ?? string.Empty
            : string.Empty;
        return new PollResult(status, appId, encryptedSecret);
    }

    private static HttpRequestMessage CreateJsonRequest(string url, Action<Utf8JsonWriter> writeBody)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            writeBody(writer);
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(stream.ToArray()),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task<JsonDocument> ReadSuccessResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureSuccess(JsonElement root, string operation)
    {
        if (!root.TryGetProperty("retcode", out var code) || !code.TryGetInt32(out var value) || value != 0)
        {
            var message = root.TryGetProperty("msg", out var messageElement) ? messageElement.GetString() : null;
            throw new InvalidOperationException(message ?? $"QQ 接口 {operation} 返回错误。");
        }
    }

    private static string BuildConnectUrl(string taskId) =>
        $"{ApiHost}/qqbot/openclaw/connect.html?task_id={Uri.EscapeDataString(taskId)}&source=&_wv=2";

    private static string CreateQrSvg(string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        using var qrCode = new SvgQRCode(data);
        return qrCode.GetGraphic(6, "#14213d", "#ffffff", drawQuietZones: true);
    }

    private static string DecryptSecret(string encryptedSecret, string base64Key)
    {
        var encrypted = Convert.FromBase64String(encryptedSecret);
        var key = Convert.FromBase64String(base64Key);
        if (encrypted.Length < 12 + 16)
            throw new InvalidOperationException("QQ 接口返回的加密密钥格式无效。");

        var nonce = encrypted.AsSpan(0, 12);
        var tag = encrypted.AsSpan(encrypted.Length - 16, 16);
        var ciphertext = encrypted.AsSpan(12, encrypted.Length - 12 - 16);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in this.m_sessions.Values)
        {
            session.Cancellation.Cancel();
            try
            {
                await session.WorkerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            session.Dispose();
        }
        this.m_sessions.Clear();
    }

    private enum BindStatus
    {
        None = 0,
        Pending = 1,
        Completed = 2,
        Expired = 3,
    }

    private sealed record BindTask(string TaskId, string Key);
    private sealed record PollResult(BindStatus Status, string AppId, string EncryptedSecret);

    private sealed class LoginSession(string id, string taskId, string key, string qrSvg)
    {
        public object Sync { get; } = new();
        public string Id { get; } = id;
        public string TaskId { get; set; } = taskId;
        public string Key { get; set; } = key;
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task WorkerTask { get; set; } = Task.CompletedTask;
        public QqQrLoginView View { get; set; } = new(id, "waiting", "请使用手机 QQ 扫描二维码。", qrSvg);
        public bool IsTerminal => this.View.Status is "connected" or "failed" or "cancelled" or "expired";

        public void SetTerminal(string status, string message, string? svg) =>
            this.View = new QqQrLoginView(this.Id, status, message, svg);

        public void Dispose() => this.Cancellation.Dispose();
    }
}

internal sealed record QqQrLoginView(string SessionId, string Status, string Message, string? QrSvg);

internal sealed record QqQrLoginCancelInput(string SessionId);
