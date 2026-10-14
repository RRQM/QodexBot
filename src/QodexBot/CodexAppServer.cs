using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal sealed class CodexAppServer : IAsyncDisposable
{
    public const string DefaultModel = "gpt-6-luna";
    private readonly string _cliScript = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "npm", "node_modules", "@openai", "codex", "bin", "codex.js");
    private readonly string _workDirectory;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, byte> _liveThreads = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private Process? _process;
    private Task? _readerTask;
    private string? _threadId;
    private int _nextRequestId;
    private StringBuilder? _activeReply;
    private StringBuilder? _activeReasoningSummary;
    private ConcurrentQueue<CodexImage>? _activeImages;
    private TaskCompletionSource<string>? _activeTurn;
    private string? _activeTurnId;
    private string? _mcpPipeName;
    private ReplyContext? _activeToolContext;
    public CodexAppServer(string botId = "default")
    {
        var safeBotId = string.Concat(botId.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
        _workDirectory = Path.Combine(Path.GetTempPath(), "QodexBot", "bots", safeBotId);
    }
    public ReplyContext? ActiveToolContext => Volatile.Read(ref _activeToolContext);
    public string? DefaultThreadId => _threadId;
    public string WorkDirectory => _workDirectory;

    public void SetMcpPipeName(string pipeName) => _mcpPipeName = pipeName;
    public void SetActiveToolContext(ReplyContext? context) => Volatile.Write(ref _activeToolContext, context);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false } && _threadId is not null) return;
        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (_process is { HasExited: false } && _threadId is not null) return;
            if (!File.Exists(_cliScript)) throw new FileNotFoundException("没有找到本机 Codex CLI", _cliScript);
            Directory.CreateDirectory(_workDirectory);
            Directory.CreateDirectory(Path.Combine(_workDirectory, "qq-outbox"));

            var startInfo = new ProcessStartInfo
            {
                FileName = "node.exe",
                WorkingDirectory = _workDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // JSON-RPC over stdio must start with '{' on byte 0. Encoding.UTF8 can
                // emit a BOM in StreamWriter, which app-server's JSON parser rejects.
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            };
            startInfo.ArgumentList.Add(_cliScript);
            startInfo.ArgumentList.Add("app-server");
            startInfo.ArgumentList.Add("--listen");
            startInfo.ArgumentList.Add("stdio://");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("model_reasoning_summary=\"detailed\"");
            if (string.IsNullOrWhiteSpace(_mcpPipeName)) throw new InvalidOperationException("本机 MCP 通道尚未启动。");
            var workerCommand = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位当前程序路径。");
            var workerArgs = new List<string>();
            if (Path.GetFileNameWithoutExtension(workerCommand).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                workerArgs.Add(Path.Combine(AppContext.BaseDirectory,
                    $"{typeof(CodexAppServer).Assembly.GetName().Name}.dll"));
            workerArgs.Add("--mcp-server");
            workerArgs.Add(_mcpPipeName);
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"mcp_servers.qodexbot.command={JsonSerializer.Serialize(workerCommand, AppJsonContext.Default.String)}");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"mcp_servers.qodexbot.args={JsonSerializer.Serialize(workerArgs, AppJsonContext.Default.ListString)}");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"mcp_servers.qodexbot.enabled=true");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("mcp_servers.qodexbot.tool_timeout_sec=360");
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!_process.Start()) throw new InvalidOperationException("无法启动 Codex app-server。");
            _readerTask = ReadServerOutputAsync(_process);
            _ = DrainStandardErrorAsync(_process);

            await SendRequestAsync("initialize", new JsonObject
            {
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "qodex_bot",
                    ["title"] = "QodexBot",
                    ["version"] = "1.0.0"
                },
                ["capabilities"] = new JsonObject()
            }, cancellationToken);
            await SendNotificationAsync("initialized", new JsonObject(), cancellationToken);
            var threadResult = await SendRequestAsync("thread/start", BuildThreadStartParams(), cancellationToken);
            _threadId = threadResult.GetProperty("thread").GetProperty("id").GetString()
                ?? throw new InvalidOperationException("Codex app-server 没有返回 thread id。");
            _liveThreads.TryAdd(_threadId, 0);
            Console.WriteLine($"常驻 Codex CLI 已启动，会话：{_threadId}");
            Console.WriteLine("已启用 Codex MCP 工具：qq_capture_screen、qq_send_image、qq_ask_user、qq_input。");
        }
        catch
        {
            await StopProcessAsync();
            throw;
        }
        finally { _startLock.Release(); }
    }

    public async Task<string> CreateThreadAsync(CancellationToken cancellationToken)
    {
        await StartAsync(cancellationToken);
        var result = await SendRequestAsync("thread/start", BuildThreadStartParams(), cancellationToken);
        var threadId = result.GetProperty("thread").GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Codex app-server 没有返回新会话 ID。");
        _liveThreads.TryAdd(threadId, 0);
        return threadId;
    }

    public async Task<IReadOnlyList<CodexModel>> ListModelsAsync(CancellationToken cancellationToken)
    {
        await StartAsync(cancellationToken);
        var result = await SendRequestAsync("model/list", new JsonObject { ["includeHidden"] = false }, cancellationToken);
        if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Codex CLI 返回的模型列表格式不正确。");

        return data.EnumerateArray()
            .Where(item => !item.TryGetProperty("hidden", out var hidden) || hidden.ValueKind != JsonValueKind.True)
            .Select(item =>
            {
                var model = item.TryGetProperty("model", out var modelElement) ? modelElement.GetString() : null;
                var id = item.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                var displayName = item.TryGetProperty("displayName", out var nameElement) ? nameElement.GetString() : null;
                var isDefault = item.TryGetProperty("isDefault", out var defaultElement) && defaultElement.ValueKind == JsonValueKind.True;
                var value = string.IsNullOrWhiteSpace(model) ? id : model;
                return string.IsNullOrWhiteSpace(value) ? null : new CodexModel(value, displayName ?? value, isDefault);
            })
            .Where(model => model is not null)
            .Cast<CodexModel>()
            .ToArray();
    }

    public async Task<string> UpdateCliAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/s");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("npm install --global --no-audit --no-fund @openai/codex@latest");

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 npm 更新进程。");
        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;
        if (process.ExitCode != 0)
        {
            var details = string.Join("\n", new[] { standardError.Trim(), standardOutput.Trim() }.Where(value => value.Length > 0));
            throw new InvalidOperationException($"npm 更新 Codex CLI 失败（退出码 {process.ExitCode}）：{TruncateOutput(details)}");
        }

        await StopProcessAsync();
        await StartAsync(cancellationToken);
        var packagePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_cliScript)!, "..", "package.json"));
        using var package = JsonDocument.Parse(await File.ReadAllTextAsync(packagePath, cancellationToken));
        var version = package.RootElement.TryGetProperty("version", out var versionElement) ? versionElement.GetString() : null;
        return string.IsNullOrWhiteSpace(version) ? "Codex CLI 已更新并重启。" : $"Codex CLI 已更新到 {version}，app-server 已重启。";
    }

    private static string TruncateOutput(string output) => output.Length <= 700 ? output : output[..700] + "…";

    public async Task ResumeThreadAsync(string threadId, CancellationToken cancellationToken)
    {
        await StartAsync(cancellationToken);
        // 新 thread 在第一轮 turn 写出 rollout 前无法 resume；它已由当前 app-server 持有。
        if (_liveThreads.ContainsKey(threadId)) return;
        await SendRequestAsync("thread/resume", new JsonObject { ["threadId"] = threadId }, cancellationToken);
    }

    public async Task InterruptTurnAsync(string threadId, CancellationToken cancellationToken)
    {
        var turnId = Volatile.Read(ref _activeTurnId);
        if (turnId is null) return;
        await SendRequestAsync("turn/interrupt", new JsonObject
        {
            ["threadId"] = threadId,
            ["turnId"] = turnId
        }, cancellationToken);
    }

    private JsonObject BuildThreadStartParams() => new()
    {
        ["cwd"] = _workDirectory,
        ["model"] = DefaultModel,
        ["approvalPolicy"] = "never",
        ["sandbox"] = "danger-full-access",
        ["ephemeral"] = false,
        ["developerInstructions"] = $"你是 QQ 助手，直接处理用户的问题和任务，优先使用中文。\n\n" +
            $"用户要求发送截图时直接调用 qq_capture_screen()：如果指定了应用或进程名，把进程可执行文件名作为 processName 参数传入以截取该进程窗口；未指定时不传参数并捕获整个桌面。不要依赖电脑操作插件截图后再复制文件。需要模拟桌面交互时调用 qq_input(action, ...)：action 可为 type_text、press_key、click、move_mouse、scroll；键盘输入发送到当前焦点窗口，鼠标坐标使用虚拟屏幕绝对坐标。发送其他已有图片时，把图片复制到 {Path.Combine(_workDirectory, "qq-outbox")} 目录，再调用 qq_send_image(path)。工具成功后再告知用户已完成。遇到缺少信息、选项无法判断或需要用户决策时，调用 qq_ask_user(question) 把问题发给用户，并等待用户在 QQ 回复后继续当前任务。\n\n" +
            "面向 QQ 的自然语言回复使用 QQ 支持的 Markdown 子集：需要时用简短标题、**粗体**、_斜体_、~~删除线~~、有序或无序列表、引用和 Markdown 链接；简单问答保持简洁，不要为了格式而堆叠标题。不要使用表格、HTML、代码围栏、嵌套列表等未确认支持的语法；代码示例请用纯文本分行表达。只提供确有必要且平台已允许的链接，不要臆造 URL。"
    };

    public async Task<CodexTurnReply> AskAsync(string threadId, string userMessage, string? model, CancellationToken cancellationToken)
    {
        await _turnLock.WaitAsync(cancellationToken);
        try
        {
            await StartAsync(cancellationToken);
            _activeReply = new StringBuilder();
            _activeReasoningSummary = new StringBuilder();
            _activeImages = new ConcurrentQueue<CodexImage>();
            _activeTurn = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var parameters = new JsonObject
            {
                ["threadId"] = threadId,
                ["input"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = userMessage
                })
            };
            if (!string.IsNullOrWhiteSpace(model)) parameters["model"] = model;
            var started = await SendRequestAsync("turn/start", parameters, cancellationToken);
            _activeTurnId = started.TryGetProperty("turn", out var turn) && turn.TryGetProperty("id", out var turnId)
                ? turnId.GetString() : null;
            var reply = await _activeTurn.Task.WaitAsync(TimeSpan.FromMinutes(10), cancellationToken);
            return new CodexTurnReply(string.IsNullOrWhiteSpace(reply) ? "Codex 没有返回文本。" : reply,
                _activeImages.ToArray(), _activeReasoningSummary.ToString());
        }
        catch (TimeoutException) { return new CodexTurnReply("Codex 处理超时，请稍后继续提问。", [], _activeReasoningSummary?.ToString() ?? ""); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Codex 会话错误：{ex.Message}");
            return new CodexTurnReply($"Codex 暂时无法回复：{ex.Message}", [], _activeReasoningSummary?.ToString() ?? "");
        }
        finally
        {
            _activeTurn = null;
            _activeTurnId = null;
            _activeReply = null;
            _activeReasoningSummary = null;
            _activeImages = null;
            _turnLock.Release();
        }
    }

    private async Task<JsonElement> SendRequestAsync(string method, JsonObject parameters,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = completion;
        var parameterElement = JsonSerializer.SerializeToElement(parameters, AppJsonContext.Default.JsonObject);
        await WriteMessageAsync(new JsonRpcMessage("2.0", id, method, parameterElement), cancellationToken);
        return await completion.Task.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken);
    }

    private Task SendNotificationAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        var parameterElement = JsonSerializer.SerializeToElement(parameters, AppJsonContext.Default.JsonObject);
        return WriteMessageAsync(new JsonRpcMessage("2.0", null, method, parameterElement), cancellationToken);
    }

    private async Task WriteMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(message, AppJsonContext.Default.JsonRpcMessage);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (_process is not { HasExited: false }) throw new InvalidOperationException("Codex app-server 已退出。");
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
        }
        finally { _writeLock.Release(); }
    }

    private async Task ReadServerOutputAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var idElement))
                {
                    var key = idElement.ToString();
                    if (_pending.TryRemove(key, out var pending))
                    {
                        if (root.TryGetProperty("error", out var error))
                            pending.TrySetException(new InvalidOperationException(error.TryGetProperty("message", out var msg)
                                ? msg.GetString() : error.ToString()));
                        else if (root.TryGetProperty("result", out var result)) pending.TrySetResult(result.Clone());
                        else pending.TrySetException(new InvalidOperationException("Codex app-server 返回了空响应。"));
                    }
                    continue;
                }

                if (!root.TryGetProperty("method", out var methodElement) ||
                    !root.TryGetProperty("params", out var parameters)) continue;
                var method = methodElement.GetString();
                if (method == "item/completed" && parameters.TryGetProperty("item", out var completedItem))
                {
                    CaptureImage(completedItem);
                    LogCodexItem("完成", completedItem);
                }
                else if (method == "item/started" && parameters.TryGetProperty("item", out var startedItem))
                {
                    LogCodexItem("开始", startedItem);
                }
                else if (method == "imageGeneration/end")
                {
                    CaptureImage(parameters);
                }
                else if (method == "item/agentMessage/delta" && parameters.TryGetProperty("delta", out var delta) &&
                    delta.ValueKind == JsonValueKind.String)
                {
                    _activeReply?.Append(delta.GetString());
                }
                else if (method == "item/reasoning/summaryTextDelta" && parameters.TryGetProperty("delta", out var summaryDelta) &&
                    summaryDelta.ValueKind == JsonValueKind.String)
                {
                    _activeReasoningSummary?.Append(summaryDelta.GetString());
                }
                else if (method == "item/reasoning/summaryPartAdded")
                {
                    if (_activeReasoningSummary is { Length: > 0 } summary && summary[^1] != '\n') summary.AppendLine();
                }
                else if (method == "item/plan/delta" && parameters.TryGetProperty("delta", out var planDelta) &&
                    planDelta.ValueKind == JsonValueKind.String)
                {
                    Console.WriteLine($"[Codex 计划] {planDelta.GetString()}");
                }
                else if (method == "turn/plan/updated")
                {
                    Console.WriteLine($"[Codex 计划更新] {parameters.GetRawText()}");
                }
                else if (method == "item/mcpToolCall/progress")
                {
                    Console.WriteLine($"[Codex MCP 工具进度] {parameters.GetRawText()}");
                }
                else if (method == "turn/completed" && parameters.TryGetProperty("turn", out var turn))
                {
                    var status = turn.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;
                    var answer = _activeReply?.ToString() ?? string.Empty;
                    if (status == "completed") _activeTurn?.TrySetResult(answer);
                    else
                    {
                        var reason = "";
                        if (turn.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                        {
                            var parts = new List<string>();
                            if (error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                                parts.Add(message.GetString()!);
                            if (error.TryGetProperty("additionalDetails", out var details) && details.ValueKind == JsonValueKind.String &&
                                !string.IsNullOrWhiteSpace(details.GetString()))
                                parts.Add(details.GetString()!);
                            if (error.TryGetProperty("codexErrorInfo", out var errorInfo) && errorInfo.ValueKind != JsonValueKind.Null)
                                parts.Add($"错误类型：{errorInfo}");
                            if (parts.Count > 0) reason = "；" + string.Join("；", parts);
                        }
                        _activeTurn?.TrySetException(new InvalidOperationException($"Codex turn 状态：{status ?? "unknown"}{reason}"));
                    }
                }
            }
            if (process.ExitCode != 0) throw new InvalidOperationException($"Codex app-server 退出，代码 {process.ExitCode}。");
        }
        catch (Exception ex)
        {
            foreach (var pending in _pending.Values) pending.TrySetException(ex);
            _activeTurn?.TrySetException(ex);
            Console.Error.WriteLine($"Codex app-server 输出读取失败：{ex.Message}");
        }
    }

    private static void LogCodexItem(string phase, JsonElement item)
    {
        var type = item.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        if (type is null || type.Equals("reasoning", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("agentMessage", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("imageGeneration", StringComparison.OrdinalIgnoreCase)) return;
        Console.WriteLine($"[Codex 执行{phase}] {item.GetRawText()}");
    }

    private void CaptureImage(JsonElement item)
    {
        var type = item.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        var status = item.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;
        if (!string.Equals(type, "imageGeneration", StringComparison.OrdinalIgnoreCase) ||
            (status is not null && !string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))) return;

        byte[]? bytes = null;
        if (item.TryGetProperty("result", out var resultElement) && resultElement.ValueKind == JsonValueKind.String)
        {
            var encoded = resultElement.GetString();
            if (!string.IsNullOrWhiteSpace(encoded))
            {
                var comma = encoded.IndexOf(',');
                if (encoded.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
                    encoded = encoded[(comma + 1)..];

                if (encoded.Length <= 14 * 1024 * 1024)
                {
                    try { bytes = Convert.FromBase64String(encoded); }
                    catch (FormatException) { Console.Error.WriteLine("Codex 图片结果不是有效的 Base64 数据。"); }
                }
            }
        }

        if ((bytes is null || bytes.Length == 0 || bytes.Length > 10 * 1024 * 1024) &&
            item.TryGetProperty("savedPath", out var pathElement) && pathElement.ValueKind == JsonValueKind.String)
        {
            var path = pathElement.GetString();
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path) && new FileInfo(path).Length <= 10 * 1024 * 1024)
                    bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"读取 Codex 生成图片失败：{ex.Message}");
            }
        }

        if (bytes is null || bytes.Length == 0 || bytes.Length > 10 * 1024 * 1024)
        {
            Console.Error.WriteLine("跳过 Codex 图片：图片为空或超过 10 MiB 限制。");
            return;
        }

        var image = GetSupportedImage(bytes);
        if (image is null)
        {
            Console.Error.WriteLine("跳过 Codex 图片：仅支持 PNG 和 JPEG 格式。");
            return;
        }
        _activeImages?.Enqueue(image);
        Console.WriteLine($"已收集 Codex 图片（{bytes.Length:N0} 字节，{image.Extension}）。");
    }

    internal static CodexImage? GetSupportedImage(byte[] bytes)
    {
        ReadOnlySpan<byte> data = bytes;
        if (data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return new CodexImage(bytes, "image/png", "png");
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return new CodexImage(bytes, "image/jpeg", "jpg");
        return null;
    }

    private static async Task DrainStandardErrorAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
                if (!string.IsNullOrWhiteSpace(line)) Console.Error.WriteLine($"[Codex] {line}");
        }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync() => await StopProcessAsync();

    private async Task StopProcessAsync()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await _process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { _process.Kill(entireProcessTree: true); }
            }
        }
        catch (InvalidOperationException) { }
        finally
        {
            _process.Dispose();
            _process = null;
            _readerTask = null;
            _threadId = null;
            _liveThreads.Clear();
        }
    }
}

internal sealed record CodexModel(string Model, string DisplayName, bool IsDefault);
