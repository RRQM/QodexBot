using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(BotSettingsStore.BotSettingsDocument))]
[JsonSerializable(typeof(BotSettingsInput))]
[JsonSerializable(typeof(BotSettingsView))]
[JsonSerializable(typeof(ConversationPage))]
[JsonSerializable(typeof(ApiErrorResponse))]
[JsonSerializable(typeof(ApiSaveSettingsResponse))]
[JsonSerializable(typeof(ApiStatusResponse))]
internal partial class WebJsonContext : JsonSerializerContext
{
}

[JsonSerializable(typeof(ConversationRecord))]
[JsonSerializable(typeof(ConversationPage))]
[JsonSerializable(typeof(JsonRpcMessage))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(QqGatewayIdentifyPayload))]
[JsonSerializable(typeof(QqGatewayHeartbeatPayload))]
[JsonSerializable(typeof(QqMessagePayload))]
[JsonSerializable(typeof(QqUploadPayload))]
[JsonSerializable(typeof(QqTokenPayload))]
internal partial class AppJsonContext : JsonSerializerContext
{
}
