using System.Text.Json;
using System.Text.Json.Serialization;

namespace Easydict.TranslationService.LocalApi;

// Request / response DTOs for the OpenAI-compatible local API surface.
// Only the fields we actually use are modelled; unknown fields are ignored on input
// and absent from output. Keep these schemas minimal but compatible with what
// KISS Translator and standard OpenAI client libraries send/expect.

public sealed class ChatRequest
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; set; } = new();
    [JsonPropertyName("stream")] public bool Stream { get; set; }

    /// <summary>Vendor extension: <c>extra_body.easydict.{target_language, source_language}</c>.</summary>
    [JsonPropertyName("extra_body")] public JsonElement? ExtraBody { get; set; }
}

public sealed class ChatMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;

    /// <summary>String content; for vision parts we read text parts only and ignore others.</summary>
    [JsonPropertyName("content")] public JsonElement Content { get; set; }
}

public sealed class ModelList
{
    [JsonPropertyName("object")] public string Object { get; set; } = "list";
    [JsonPropertyName("data")] public required List<ModelInfo> Data { get; set; }
}

public sealed class ModelInfo
{
    [JsonPropertyName("id")] public required string Id { get; set; }
    [JsonPropertyName("object")] public string Object { get; set; } = "model";
    [JsonPropertyName("created")] public long Created { get; set; }
    [JsonPropertyName("owned_by")] public string OwnedBy { get; set; } = "easydict";
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    [JsonPropertyName("supports_streaming")] public bool SupportsStreaming { get; set; }
}

public sealed class ChatCompletionResponse
{
    [JsonPropertyName("id")] public required string Id { get; set; }
    [JsonPropertyName("object")] public string Object { get; set; } = "chat.completion";
    [JsonPropertyName("created")] public long Created { get; set; }
    [JsonPropertyName("model")] public required string Model { get; set; }
    [JsonPropertyName("choices")] public required List<ChatChoice> Choices { get; set; }
}

public sealed class ChatChoice
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("message")] public ChatMessageOut? Message { get; set; }
    [JsonPropertyName("delta")] public ChatDelta? Delta { get; set; }
    [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
}

public sealed class ChatMessageOut
{
    [JsonPropertyName("role")] public string Role { get; set; } = "assistant";
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
}

public sealed class ChatDelta
{
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
}

public sealed class ChatCompletionChunk
{
    [JsonPropertyName("id")] public required string Id { get; set; }
    [JsonPropertyName("object")] public string Object { get; set; } = "chat.completion.chunk";
    [JsonPropertyName("created")] public long Created { get; set; }
    [JsonPropertyName("model")] public required string Model { get; set; }
    [JsonPropertyName("choices")] public required List<ChatChoice> Choices { get; set; }
}

public sealed class ErrorEnvelope
{
    [JsonPropertyName("error")] public required ErrorBody Error { get; set; }
}

public sealed class ErrorBody
{
    [JsonPropertyName("message")] public required string Message { get; set; }
    [JsonPropertyName("type")] public required string Type { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
}

public sealed class HealthResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; } = true;
}

[JsonSerializable(typeof(ChatRequest))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(ModelList))]
[JsonSerializable(typeof(ModelInfo))]
[JsonSerializable(typeof(ChatCompletionResponse))]
[JsonSerializable(typeof(ChatCompletionChunk))]
[JsonSerializable(typeof(ErrorEnvelope))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
internal sealed partial class LocalApiJsonContext : JsonSerializerContext;
