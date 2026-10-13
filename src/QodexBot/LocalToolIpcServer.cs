using System.IO.Pipes;

/// <summary>
/// 在 QodexBot 主进程中接收 MCP worker 的类型化本机工具请求。
/// </summary>
internal sealed class LocalToolIpcServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _pipeName = $"qodexbot-{Guid.NewGuid():N}";
    private Func<McpToolRequest, CancellationToken, Task<string>>? _handler;
    private Task? _acceptTask;

    public string PipeName => _pipeName;

    public Task StartAsync()
    {
        _acceptTask = AcceptLoopAsync(_shutdown.Token);
        return Task.CompletedTask;
    }

    public void SetHandler(Func<McpToolRequest, CancellationToken, Task<string>> handler) => _handler = handler;

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 10,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (IOException ex)
            {
                if (cancellationToken.IsCancellationRequested) break;
                Console.Error.WriteLine($"本机工具管道异常：{ex.Message}");
                continue;
            }

            _ = HandlePipeRequestAsync(pipe, cancellationToken);
        }
    }

    private async Task HandlePipeRequestAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe)
        {
            using var reader = new BinaryReader(pipe);
            using var writer = new BinaryWriter(pipe);
            try
            {
                var kind = (McpToolKind)reader.ReadByte();
                var request = kind == McpToolKind.Input
                    ? new McpToolRequest(kind, null, ReadInput(reader))
                    : new McpToolRequest(kind, ReadNullableString(reader));
                var handler = _handler ?? throw new InvalidOperationException("MCP 工具尚未就绪。");
                var result = await handler(request, cancellationToken);
                writer.Write(true);
                writer.Write(result);
            }
            catch (Exception ex)
            {
                try
                {
                    writer.Write(false);
                    writer.Write(ex.Message);
                }
                catch (IOException) { }
            }
        }
    }

    private static string? ReadNullableString(BinaryReader reader) =>
        reader.ReadBoolean() ? reader.ReadString() : null;

    private static int? ReadNullableInt32(BinaryReader reader) =>
        reader.ReadBoolean() ? reader.ReadInt32() : null;

    private static KeyboardMouseInput ReadInput(BinaryReader reader) => new(
        reader.ReadString(),
        ReadNullableString(reader),
        ReadNullableString(reader),
        ReadNullableInt32(reader),
        ReadNullableInt32(reader),
        ReadNullableString(reader),
        ReadNullableInt32(reader));

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_acceptTask is not null)
        {
            try { await _acceptTask; }
            catch (IOException) { }
        }
        _shutdown.Dispose();
    }
}
