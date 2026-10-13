using System.Text.Json;

/// <summary>
/// 将机器人对话事件追加保存到本机 JSONL 文件，并提供分页查询。
/// </summary>
internal sealed class ConversationStore
{
    private readonly string m_filePath;
    private readonly SemaphoreSlim m_writeLock = new(1, 1);
    private readonly object m_syncRoot = new();
    private readonly List<ConversationRecord> m_records = [];
    private long m_nextId;

    public ConversationStore(string filePath)
    {
        this.m_filePath = filePath;
    }

    /// <summary>
    /// 从本机 JSONL 文件恢复历史记录。
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(this.m_filePath)) return;

        var records = new List<ConversationRecord>();
        foreach (var line in await File.ReadAllLinesAsync(this.m_filePath, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var record = JsonSerializer.Deserialize(line, AppJsonContext.Default.ConversationRecord);
                if (record is not null) records.Add(record);
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"跳过格式无效的会话记录：{ex.Message}");
            }
        }

        lock (this.m_syncRoot)
        {
            this.m_records.Clear();
            this.m_records.AddRange(records.OrderBy(record => record.Id));
            this.m_nextId = this.m_records.Count == 0 ? 0 : this.m_records[^1].Id;
        }
    }

    /// <summary>
    /// 追加一条对话事件并立即写入本机文件。
    /// </summary>
    public async Task AppendAsync(string userOpenId, string kind, string content, int imageCount = 0,
        CancellationToken cancellationToken = default)
    {
        await this.m_writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long id;
            lock (this.m_syncRoot) id = ++this.m_nextId;
            var record = new ConversationRecord(id, DateTimeOffset.UtcNow, userOpenId, kind, content, imageCount);
            var directory = Path.GetDirectoryName(this.m_filePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            await File.AppendAllTextAsync(this.m_filePath,
                JsonSerializer.Serialize(record, AppJsonContext.Default.ConversationRecord) + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);
            lock (this.m_syncRoot) this.m_records.Add(record);
        }
        finally
        {
            this.m_writeLock.Release();
        }
    }

    /// <summary>
    /// 读取按时间正序排列的一页对话事件。
    /// </summary>
    public ConversationPage GetPage(long? beforeId, int limit, string? userOpenId)
    {
        limit = Math.Clamp(limit, 1, 100);
        lock (this.m_syncRoot)
        {
            IEnumerable<ConversationRecord> query = this.m_records;
            if (beforeId.HasValue) query = query.Where(record => record.Id < beforeId.Value);
            if (!string.IsNullOrWhiteSpace(userOpenId))
                query = query.Where(record => record.UserOpenId.Contains(userOpenId, StringComparison.OrdinalIgnoreCase));
            var matching = query.ToArray();
            var records = matching.TakeLast(limit).ToArray();
            return new ConversationPage(records, matching.Length > records.Length,
                records.Length == 0 ? null : records[0].Id);
        }
    }
}

internal sealed record ConversationRecord(long Id, DateTimeOffset Timestamp, string UserOpenId, string Kind,
    string Content, int ImageCount);

internal sealed record ConversationPage(IReadOnlyList<ConversationRecord> Items, bool HasMore, long? NextBeforeId);
