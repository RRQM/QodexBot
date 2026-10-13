using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Hosts the MCP stdio endpoint used by the Codex CLI child process.
/// </summary>
internal static class McpStdioWorker
{
    private const string ProtocolVersion = "2025-03-26";

    public static async Task RunAsync(string pipeName)
    {
        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            JsonNode? requestId = null;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var method = root.GetProperty("method").GetString()
                    ?? throw new JsonException("MCP 请求缺少 method 字段。");
                var hasId = root.TryGetProperty("id", out var idElement);
                if (hasId) requestId = JsonNode.Parse(idElement.GetRawText());

                if (method.StartsWith("notifications/", StringComparison.Ordinal))
                {
                    continue;
                }

                var result = method switch
                {
                    "initialize" => CreateInitializeResult(root),
                    "ping" => new JsonObject(),
                    "tools/list" => CreateToolsListResult(),
                    "tools/call" => await CallToolAsync(root, pipeName).ConfigureAwait(false),
                    _ => throw new McpMethodNotFoundException(method)
                };

                if (hasId)
                {
                    await WriteResponseAsync(requestId, result).ConfigureAwait(false);
                    requestId = null;
                }
            }
            catch (McpMethodNotFoundException ex)
            {
                await WriteErrorAsync(requestId, -32601, ex.Message).ConfigureAwait(false);
                requestId = null;
            }
            catch (JsonException ex)
            {
                await WriteErrorAsync(requestId, -32700, $"JSON-RPC 请求无效：{ex.Message}").ConfigureAwait(false);
                requestId = null;
            }
            catch (Exception ex)
            {
                if (requestId is not null)
                {
                    await WriteErrorAsync(requestId, -32603, ex.Message).ConfigureAwait(false);
                    requestId = null;
                }
                else
                {
                    await Console.Error.WriteLineAsync($"MCP stdio 请求失败：{ex.Message}").ConfigureAwait(false);
                }
            }
        }
    }

    private static JsonObject CreateInitializeResult(JsonElement request)
    {
        var protocolVersion = request.TryGetProperty("params", out var parameters) &&
            parameters.TryGetProperty("protocolVersion", out var versionElement)
            ? versionElement.GetString() ?? ProtocolVersion
            : ProtocolVersion;

        return new JsonObject
        {
            ["protocolVersion"] = protocolVersion,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "qodexbot",
                ["version"] = "1.0.0",
                ["title"] = "QodexBot"
            },
            ["instructions"] = "通过 QQ 机器人截图、发送图片、模拟桌面键鼠输入或向 QQ 用户提问。"
        };
    }

    private static JsonObject CreateToolsListResult() => new()
    {
        ["tools"] = new JsonArray(
            CreateTool("qq_capture_screen",
                "Capture the Windows desktop, or capture a specified process window, and send it directly to the current QQ conversation.",
                new JsonObject
                {
                    ["processName"] = StringProperty("Optional process name, with or without .exe, such as Code or notepad.")
                }),
            CreateTool("qq_send_image",
                "Send a PNG or JPEG image from the QodexBot qq-outbox directory to the current QQ conversation immediately.",
                new JsonObject
                {
                    ["path"] = StringProperty("Absolute path to the image inside the qq-outbox directory.")
                }, "path"),
            CreateTool("qq_ask_user",
                "Send a question to the current QQ user and wait for their next message, then return that answer so the current task can continue.",
                new JsonObject
                {
                    ["question"] = StringProperty("A concise question to send to the user.")
                }, "question"),
            CreateTool("qq_input",
                "Simulate keyboard and mouse input on the current Windows desktop. The input goes to the currently focused window.",
                new JsonObject
                {
                    ["action"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray(
                            JsonValue.Create("type_text"),
                            JsonValue.Create("press_key"),
                            JsonValue.Create("click"),
                            JsonValue.Create("move_mouse"),
                            JsonValue.Create("scroll")),
                        ["description"] = "Action to perform on the desktop."
                    },
                    ["text"] = StringProperty("Text to type when action is type_text."),
                    ["key"] = StringProperty("Key or key chord when action is press_key, such as Enter, Ctrl+S, or Alt+F4."),
                    ["x"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Absolute virtual-screen X coordinate."
                    },
                    ["y"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Absolute virtual-screen Y coordinate."
                    },
                    ["button"] = StringProperty("Mouse button for click: left, right, or middle."),
                    ["scrollDelta"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Mouse wheel delta. Positive scrolls up, negative scrolls down."
                    }
                }, "action"))
    };

    private static JsonObject CreateTool(string name, string description, JsonObject properties,
        params string[] required)
    {
        var requiredArray = new JsonArray();
        foreach (var property in required)
        {
            requiredArray.Add((JsonNode?)JsonValue.Create(property));
        }

        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = requiredArray,
                ["additionalProperties"] = false
            }
        };
    }

    private static JsonObject StringProperty(string description) => new()
    {
        ["type"] = "string",
        ["description"] = description
    };

    private static async Task<JsonObject> CallToolAsync(JsonElement request, string pipeName)
    {
        var parameters = request.TryGetProperty("params", out var paramsElement)
            ? paramsElement
            : throw new JsonException("tools/call 请求缺少 params 字段。");
        var name = parameters.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString()
            : null;
        var arguments = parameters.TryGetProperty("arguments", out var argsElement) &&
            argsElement.ValueKind == JsonValueKind.Object
            ? argsElement
            : default;

        try
        {
            var result = name switch
            {
                "qq_capture_screen" => await InvokeAsync(pipeName, McpToolKind.CaptureScreen,
                    GetOptionalString(arguments, "processName")).ConfigureAwait(false),
                "qq_send_image" => await InvokeAsync(pipeName, McpToolKind.SendImage,
                    GetRequiredString(arguments, "path")).ConfigureAwait(false),
                "qq_ask_user" => await InvokeAsync(pipeName, McpToolKind.AskUser,
                    GetRequiredString(arguments, "question")).ConfigureAwait(false),
                "qq_input" => await InvokeInputAsync(pipeName, ReadInput(arguments)).ConfigureAwait(false),
                _ => throw new McpMethodNotFoundException($"未知工具：{name ?? "(null)"}")
            };

            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result }),
                ["isError"] = false
            };
        }
        catch (Exception ex)
        {
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = ex.Message }),
                ["isError"] = true
            };
        }
    }

    private static KeyboardMouseInput ReadInput(JsonElement arguments)
    {
        var action = GetRequiredString(arguments, "action");
        return new KeyboardMouseInput(action,
            GetOptionalString(arguments, "text"),
            GetOptionalString(arguments, "key"),
            GetOptionalInt32(arguments, "x"),
            GetOptionalInt32(arguments, "y"),
            GetOptionalString(arguments, "button"),
            GetOptionalInt32(arguments, "scrollDelta"));
    }

    private static string GetRequiredString(JsonElement element, string property) =>
        GetOptionalString(element, property) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"{property} 参数不能为空。");

    private static string? GetOptionalString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetOptionalInt32(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static async Task<string> InvokeAsync(string pipeName, McpToolKind kind, string? argument)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10_000).ConfigureAwait(false);
        using var writer = new BinaryWriter(pipe, Encoding.UTF8, leaveOpen: true);
        using var reader = new BinaryReader(pipe, Encoding.UTF8, leaveOpen: true);
        writer.Write((byte)kind);
        writer.Write(argument is not null);
        if (argument is not null) writer.Write(argument);
        writer.Flush();

        var succeeded = reader.ReadBoolean();
        var result = reader.ReadString();
        if (!succeeded) throw new InvalidOperationException(result);
        return result;
    }

    private static async Task<string> InvokeInputAsync(string pipeName, KeyboardMouseInput input)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10_000).ConfigureAwait(false);
        using var writer = new BinaryWriter(pipe, Encoding.UTF8, leaveOpen: true);
        using var reader = new BinaryReader(pipe, Encoding.UTF8, leaveOpen: true);
        writer.Write((byte)McpToolKind.Input);
        writer.Write(input.Action);
        WriteNullableString(writer, input.Text);
        WriteNullableString(writer, input.Key);
        WriteNullableInt32(writer, input.X);
        WriteNullableInt32(writer, input.Y);
        WriteNullableString(writer, input.Button);
        WriteNullableInt32(writer, input.ScrollDelta);
        writer.Flush();

        var succeeded = reader.ReadBoolean();
        var result = reader.ReadString();
        if (!succeeded) throw new InvalidOperationException(result);
        return result;
    }

    private static void WriteNullableString(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null) writer.Write(value);
    }

    private static void WriteNullableInt32(BinaryWriter writer, int? value)
    {
        writer.Write(value.HasValue);
        if (value.HasValue) writer.Write(value.Value);
    }

    private static async Task WriteResponseAsync(JsonNode? id, JsonObject result)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result
        };
        await WriteMessageAsync(response).ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(JsonNode? id, int code, string message)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        };
        await WriteMessageAsync(response).ConfigureAwait(false);
    }

    private static async Task WriteMessageAsync(JsonObject message)
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(message, AppJsonContext.Default.JsonObject))
            .ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);
    }

    private sealed class McpMethodNotFoundException(string method) : Exception($"不支持的 MCP 方法：{method}");
}
