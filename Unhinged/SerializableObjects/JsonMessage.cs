using System.Text.Json.Serialization;

namespace Unhinged;

public struct JsonMessage { public string Message { get; set; } }

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Serialization | JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(JsonMessage))]
internal partial class JsonContext : JsonSerializerContext
{
}