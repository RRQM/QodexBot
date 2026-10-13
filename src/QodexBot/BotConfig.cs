internal sealed record BotConfig(
    string BotId,
    string Name,
    string AppId,
    string AppSecret,
    bool Sandbox,
    ulong Intents,
    bool SubscribeC2cMessages,
    bool SubscribeGroupMessages);
