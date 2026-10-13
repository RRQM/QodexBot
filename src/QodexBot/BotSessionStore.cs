using System.Text.Json;

/// <summary>Stores Codex threads owned by each QQ user and their active selection.</summary>
internal sealed class BotSessionStore(string filePath)
{
    private readonly SemaphoreSlim m_lock = new(1, 1);
    private readonly string m_filePath = Path.GetFullPath(filePath);
    private List<BotSession> m_sessions = [];

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(this.m_filePath)) return;
        try
        {
            var json = await File.ReadAllTextAsync(this.m_filePath, cancellationToken).ConfigureAwait(false);
            this.m_sessions = JsonSerializer.Deserialize(json, AppJsonContext.Default.ListBotSession) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"会话配置读取失败：{ex.Message}");
        }
    }

    public async Task<IReadOnlyList<BotSession>> GetAsync(string owner, CancellationToken cancellationToken)
    {
        await this.m_lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return this.m_sessions.Where(item => item.Owner == owner).OrderByDescending(item => item.UpdatedAt).ToArray(); }
        finally { this.m_lock.Release(); }
    }

    public async Task<BotSession?> GetActiveAsync(string owner, CancellationToken cancellationToken)
    {
        await this.m_lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return this.m_sessions.FirstOrDefault(item => item.Owner == owner && item.IsActive); }
        finally { this.m_lock.Release(); }
    }

    public async Task<BotSession> EnsureAsync(string owner, string defaultThreadId, string defaultName,
        Func<CancellationToken, Task<string>> createThread, CancellationToken cancellationToken)
    {
        await this.m_lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var active = this.m_sessions.FirstOrDefault(item => item.Owner == owner && item.IsActive);
            if (active is not null) return active;
            var threadId = this.m_sessions.All(item => item.ThreadId != defaultThreadId)
                ? defaultThreadId : await createThread(cancellationToken).ConfigureAwait(false);
            var session = new BotSession(owner, threadId, defaultName, DateTimeOffset.UtcNow, true);
            this.m_sessions.RemoveAll(item => item.Owner == owner);
            this.m_sessions.Add(session);
            await this.SaveCoreAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        finally { this.m_lock.Release(); }
    }

    public async Task<BotSession> AddAsync(string owner, string threadId, string name, CancellationToken cancellationToken)
    {
        await this.m_lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.SetActiveCore(owner, null);
            var session = new BotSession(owner, threadId, name, DateTimeOffset.UtcNow, true);
            this.m_sessions.Add(session);
            await this.SaveCoreAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        finally { this.m_lock.Release(); }
    }

    public async Task<BotSession?> SelectAsync(string owner, int index, CancellationToken cancellationToken)
    {
        await this.m_lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sessions = this.m_sessions.Where(item => item.Owner == owner).OrderByDescending(item => item.UpdatedAt).ToArray();
            if (index < 1 || index > sessions.Length) return null;
            this.SetActiveCore(owner, sessions[index - 1].ThreadId);
            await this.SaveCoreAsync(cancellationToken).ConfigureAwait(false);
            return this.m_sessions.First(item => item.Owner == owner && item.ThreadId == sessions[index - 1].ThreadId);
        }
        finally { this.m_lock.Release(); }
    }

    public async Task<BotSession?> RenameActiveAsync(string owner, string name, CancellationToken cancellationToken)
    {
        await this.m_lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = this.m_sessions.FindIndex(item => item.Owner == owner && item.IsActive);
            if (index < 0) return null;
            var updated = this.m_sessions[index] with { Name = name, UpdatedAt = DateTimeOffset.UtcNow };
            this.m_sessions[index] = updated;
            await this.SaveCoreAsync(cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally { this.m_lock.Release(); }
    }

    public async Task TouchActiveAsync(string owner, CancellationToken cancellationToken)
    {
        await this.m_lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = this.m_sessions.FindIndex(item => item.Owner == owner && item.IsActive);
            if (index < 0) return;
            this.m_sessions[index] = this.m_sessions[index] with { UpdatedAt = DateTimeOffset.UtcNow };
            await this.SaveCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { this.m_lock.Release(); }
    }

    private void SetActiveCore(string owner, string? threadId)
    {
        for (var i = 0; i < this.m_sessions.Count; i++)
            if (this.m_sessions[i].Owner == owner)
                this.m_sessions[i] = this.m_sessions[i] with { IsActive = this.m_sessions[i].ThreadId == threadId };
    }

    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(this.m_filePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = this.m_filePath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath,
                JsonSerializer.Serialize(this.m_sessions, AppJsonContext.Default.ListBotSession), cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, this.m_filePath, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }
}

internal sealed record BotSession(string Owner, string ThreadId, string Name, DateTimeOffset UpdatedAt, bool IsActive);
