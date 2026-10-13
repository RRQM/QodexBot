using System.Collections.Concurrent;

internal sealed record CodexImage(byte[] Bytes, string ContentType, string Extension);
internal sealed record CodexTurnReply(string Text, IReadOnlyList<CodexImage> Images, string ReasoningSummary = "");

internal sealed class ReplyContext(string messageId, string userOpenId)
{
    private int _sequence = 1;
    private string _messageId = messageId;
    public string MessageId => Volatile.Read(ref _messageId);
    public string UserOpenId { get; } = userOpenId;
    public int Sequence => _sequence;
    public ConcurrentDictionary<string, byte> SentImageHashes { get; } = new(StringComparer.Ordinal);
    public int NextSequence() => Interlocked.Increment(ref _sequence) - 1;
    public void StartReplyTo(string newMessageId)
    {
        Volatile.Write(ref _messageId, newMessageId);
        Interlocked.Exchange(ref _sequence, 1);
    }
}

internal sealed class PendingQuestion(ReplyContext context)
{
    private readonly object _sync = new();
    public ReplyContext Context { get; } = context;
    public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool TryAnswer(string messageId, string answer)
    {
        lock (_sync)
        {
            if (Completion.Task.IsCompleted) return false;
            Context.StartReplyTo(messageId);
            return Completion.TrySetResult(answer);
        }
    }
    public void Close() => Completion.TrySetCanceled();
}
