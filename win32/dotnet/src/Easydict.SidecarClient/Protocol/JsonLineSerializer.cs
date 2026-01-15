using System.Text.Json;

namespace Easydict.SidecarClient.Protocol;

/// <summary>
/// JSON Lines (JSONL) serializer/deserializer.
/// Each message is a single line of JSON, terminated by newline.
/// </summary>
public static class JsonLineSerializer
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Serialize an object to a JSON line (no trailing newline).
    /// </summary>
    public static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, s_options);
    }

    /// <summary>
    /// Serialize an object to a JSON line with trailing newline.
    /// </summary>
    public static string SerializeLine<T>(T value)
    {
        return JsonSerializer.Serialize(value, s_options) + "\n";
    }

    /// <summary>
    /// Deserialize a JSON line to an object.
    /// </summary>
    public static T? Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, s_options);
    }

    /// <summary>
    /// Try to deserialize a JSON line to an object.
    /// </summary>
    public static bool TryDeserialize<T>(string json, out T? result)
    {
        try
        {
            result = JsonSerializer.Deserialize<T>(json, s_options);
            return true;
        }
        catch (JsonException)
        {
            result = default;
            return false;
        }
    }
}

