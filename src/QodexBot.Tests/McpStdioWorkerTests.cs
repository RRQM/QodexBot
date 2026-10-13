using System.Diagnostics;
using System.Collections.Concurrent;
using TouchSocket.Mcp;
using Xunit;

namespace QodexBot.Tests;

public sealed class McpStdioWorkerTests
{
    [Fact]
    public async Task Worker_ExposesQqToolsThroughTouchSocketMcp()
    {
        await using var ipcServer = new LocalToolIpcServer();
        var requests = new ConcurrentQueue<McpToolRequest>();
        ipcServer.SetHandler((request, _) =>
        {
            requests.Enqueue(request);
            return Task.FromResult($"received {request.Kind}");
        });
        await ipcServer.StartAsync();

        using var client = new McpStdioClient();
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(typeof(LocalToolIpcServer).Assembly.Location);
        startInfo.ArgumentList.Add("--mcp-server");
        startInfo.ArgumentList.Add(ipcServer.PipeName);

        await client.SetupAsync(new McpStdioClientOptions
        {
            StartInfo = startInfo,
            KillOnDispose = true
        });
        await client.ConnectAsync();
        await client.InitializeAsync();
        var result = await client.ListToolsAsync();
        var toolNames = result.Tools.Select(tool => tool.Name).ToArray();

        Assert.Contains("qq_capture_screen", toolNames);
        Assert.Contains("qq_send_image", toolNames);
        Assert.Contains("qq_ask_user", toolNames);
        Assert.Contains("qq_input", toolNames);

        await client.CallToolAsync("qq_capture_screen", new Dictionary<string, object> { ["processName"] = "Code" });
        await client.CallToolAsync("qq_send_image", new Dictionary<string, object> { ["path"] = "C:/qq-outbox/chart.png" });
        await client.CallToolAsync("qq_ask_user", new Dictionary<string, object> { ["question"] = "继续吗？" });
        var textInputResult = await client.CallToolAsync("qq_input", new Dictionary<string, object>
        {
            ["action"] = "type_text",
            ["text"] = "测试输入"
        });
        var clickInputResult = await client.CallToolAsync("qq_input", new Dictionary<string, object>
        {
            ["action"] = "click",
            ["x"] = 10,
            ["y"] = 20,
            ["button"] = "right"
        });

        Assert.False(textInputResult.IsError);
        Assert.False(clickInputResult.IsError);

        Assert.Collection(requests,
            request => Assert.Equal(new McpToolRequest(McpToolKind.CaptureScreen, "Code"), request),
            request => Assert.Equal(new McpToolRequest(McpToolKind.SendImage, "C:/qq-outbox/chart.png"), request),
            request => Assert.Equal(new McpToolRequest(McpToolKind.AskUser, "继续吗？"), request),
            request => Assert.Equal(new McpToolRequest(McpToolKind.Input, null,
                new KeyboardMouseInput("type_text", "测试输入")), request),
            request => Assert.Equal(new McpToolRequest(McpToolKind.Input, null,
                new KeyboardMouseInput("click", null, null, 10, 20, "right")), request));
    }
}
