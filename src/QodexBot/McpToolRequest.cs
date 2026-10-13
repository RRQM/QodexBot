/// <summary>
/// QodexBot 暴露给 Codex 的工具类型。
/// </summary>
internal enum McpToolKind : byte
{
    CaptureScreen = 1,
    SendImage = 2,
    AskUser = 3,
    Input = 4
}

/// <summary>
/// 经本机管道传递的工具调用参数。
/// </summary>
internal sealed record McpToolRequest(McpToolKind Kind, string? Argument, KeyboardMouseInput? Input = null);

/// <summary>
/// 键鼠模拟动作参数。
/// </summary>
internal sealed record KeyboardMouseInput(
    string Action,
    string? Text = null,
    string? Key = null,
    int? X = null,
    int? Y = null,
    string? Button = null,
    int? ScrollDelta = null);
