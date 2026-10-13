internal sealed record BotConfig(
    string AppId,
    string AppSecret,
    bool Sandbox,
    ulong Intents);
