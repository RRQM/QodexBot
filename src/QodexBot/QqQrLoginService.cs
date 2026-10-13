using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

/// <summary>
/// 管理 QQ 机器人扫码绑定会话，并在绑定成功后保存平台凭据。
/// </summary>
internal sealed class QqQrLoginService(BotSettingsStore settingsStore, string helperPath) : IAsyncDisposable
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, LoginSession> m_sessions = new(StringComparer.Ordinal);
    private readonly BotSettingsStore m_settingsStore = settingsStore;
    private readonly string m_helperPath = Path.GetFullPath(helperPath);

    public async Task<QqQrLoginView> StartAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(this.m_helperPath))
            throw new InvalidOperationException("扫码组件文件缺失，请重新安装或解压程序包。");
        var connectorDirectory = Path.GetDirectoryName(this.m_helperPath)!;
        if (!Directory.Exists(Path.Combine(connectorDirectory, "node_modules")))
            throw new InvalidOperationException("扫码依赖尚未安装。请在 qq-connector 目录运行 npm ci --omit=dev 后重试。");

        var sessionId = Guid.NewGuid().ToString("N");
        var startInfo = new ProcessStartInfo("node")
        {
            WorkingDirectory = connectorDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(this.m_helperPath);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动 QQ 扫码组件。");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException("找不到 Node.js。请安装 Node.js 18 或更高版本后重试。", ex);
        }

        var session = new LoginSession(sessionId, process);
        if (!this.m_sessions.TryAdd(sessionId, session))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("无法创建扫码会话。");
        }

        session.ReaderTask = this.ReadOutputAsync(session);
        _ = ReadErrorAsync(session);
        _ = this.EnforceLifetimeAsync(session);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            return await session.QrReady.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await this.CancelAsync(sessionId).ConfigureAwait(false);
            throw new InvalidOperationException("获取 QQ 登录二维码超时，请稍后重试。");
        }
        catch
        {
            await this.CancelAsync(sessionId).ConfigureAwait(false);
            throw;
        }
    }

    public QqQrLoginView Get(string sessionId)
    {
        if (!this.m_sessions.TryGetValue(sessionId, out var session))
            return new QqQrLoginView(sessionId, "expired", "扫码会话已失效，请重新开始。", null);
        lock (session.Sync)
        {
            if (!session.IsTerminal && DateTimeOffset.UtcNow - session.CreatedAt >= SessionLifetime)
                _ = this.CancelAsync(sessionId);
            return session.View;
        }
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
        try
        {
            if (!session.Process.HasExited) session.Process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        await session.ReaderTask.ConfigureAwait(false);
        if (session.View.Status is "cancelled" or "expired")
        {
            this.m_sessions.TryRemove(sessionId, out _);
            session.Process.Dispose();
        }
        return session.View;
    }

    private async Task ReadOutputAsync(LoginSession session)
    {
        try
        {
            while (await session.Process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var type = root.GetProperty("type").GetString();
                if (type == "qr")
                {
                    var svg = root.GetProperty("svg").GetString();
                    var view = new QqQrLoginView(session.Id, "waiting", "请使用手机 QQ 扫描二维码。", svg);
                    lock (session.Sync) session.View = view;
                    session.QrReady.TrySetResult(view);
                }
                else if (type == "success")
                {
                    var credentials = root.GetProperty("credentials");
                    if (credentials.GetArrayLength() == 0) throw new InvalidOperationException("扫码未返回机器人凭据。");
                    var credential = credentials[0];
                    await this.m_settingsStore.SaveQrCredentialsAsync(
                        credential.GetProperty("appId").GetString() ?? string.Empty,
                        credential.GetProperty("appSecret").GetString() ?? string.Empty,
                        CancellationToken.None).ConfigureAwait(false);
                    lock (session.Sync) session.SetTerminal("connected", "扫码绑定成功，机器人正在连接。", null);
                    return;
                }
                else if (type == "failure")
                {
                    var error = root.TryGetProperty("message", out var message) ? message.GetString() : null;
                    lock (session.Sync) session.SetTerminal("failed", $"扫码绑定失败：{error ?? "未知错误"}", null);
                    session.QrReady.TrySetException(new InvalidOperationException(session.View.Message));
                    return;
                }
            }

            lock (session.Sync)
            {
                if (!session.IsTerminal)
                    session.SetTerminal("failed", "扫码组件已退出，请重试。", null);
            }
            session.QrReady.TrySetException(new InvalidOperationException(session.View.Message));
        }
        catch (Exception ex)
        {
            lock (session.Sync)
            {
                if (!session.IsTerminal)
                    session.SetTerminal("failed", $"扫码绑定失败：{ex.Message}", null);
            }
            session.QrReady.TrySetException(ex);
        }
    }

    private static async Task ReadErrorAsync(LoginSession session)
    {
        var error = await session.Process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(error)) Console.Error.WriteLine($"QQ 扫码组件：{error.Trim()}");
    }

    private async Task EnforceLifetimeAsync(LoginSession session)
    {
        await Task.Delay(SessionLifetime).ConfigureAwait(false);
        await this.CancelAsync(session.Id).ConfigureAwait(false);
        lock (session.Sync)
        {
            if (session.View.Status == "cancelled")
                session.View = session.View with { Status = "expired", Message = "二维码已过期，请重新开始。" };
        }
        this.m_sessions.TryRemove(session.Id, out _);
        session.Process.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in this.m_sessions.Values)
        {
            if (!session.IsTerminal) await this.CancelAsync(session.Id).ConfigureAwait(false);
            session.Process.Dispose();
        }
    }

    private sealed class LoginSession(string id, Process process)
    {
        public object Sync { get; } = new();
        public string Id { get; } = id;
        public Process Process { get; } = process;
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        public TaskCompletionSource<QqQrLoginView> QrReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ReaderTask { get; set; } = Task.CompletedTask;
        public QqQrLoginView View { get; set; } = new(id, "starting", "正在获取二维码…", null);
        public bool IsTerminal => this.View.Status is "connected" or "failed" or "cancelled" or "expired";

        public void SetTerminal(string status, string message, string? svg) =>
            this.View = new QqQrLoginView(this.Id, status, message, svg);
    }
}

internal sealed record QqQrLoginView(string SessionId, string Status, string Message, string? QrSvg);

internal sealed record QqQrLoginCancelInput(string SessionId);
