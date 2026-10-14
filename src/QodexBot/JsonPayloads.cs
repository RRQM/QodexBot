using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed record ApiErrorResponse(string Error);

internal sealed record ApiSaveSettingsResponse(BotSettingsView Settings, string Message);

internal sealed record ApiStatusResponse(string RuntimeStatus, BotSettingsView Configuration, string ConfigFile);

internal sealed record JsonRpcMessage(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] JsonElement Parameters);

internal sealed record QqGatewayIdentifyPayload(
    [property: JsonPropertyName("op")] int Op,
    [property: JsonPropertyName("d")] QqGatewayIdentifyData D);

internal sealed record QqGatewayIdentifyData(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("intents")] ulong Intents,
    [property: JsonPropertyName("shard")] int[] Shard);

internal sealed record QqGatewayHeartbeatPayload(
    [property: JsonPropertyName("op")] int Op,
    [property: JsonPropertyName("d")] long? D);

internal sealed record QqMessagePayload(
    [property: JsonPropertyName("content"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Content,
    [property: JsonPropertyName("msg_type")] int MessageType,
    [property: JsonPropertyName("msg_id")] string MessageId,
    [property: JsonPropertyName("msg_seq")] int MessageSequence,
    [property: JsonPropertyName("media"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        QqMediaPayload? Media = null,
    [property: JsonPropertyName("markdown"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        QqMarkdownPayload? Markdown = null);

internal sealed record QqMarkdownPayload([property: JsonPropertyName("content")] string Content);

internal sealed record QqMediaPayload([property: JsonPropertyName("file_info")] JsonElement FileInfo);

internal sealed record QqUploadPayload(
    [property: JsonPropertyName("file_type")] int FileType,
    [property: JsonPropertyName("file_data")] string FileData,
    [property: JsonPropertyName("srv_send_msg")] bool ServerSendMessage);

internal sealed record QqTokenPayload(
    [property: JsonPropertyName("appId")] string AppId,
    [property: JsonPropertyName("clientSecret")] string ClientSecret);
