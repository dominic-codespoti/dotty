using System.Text.Json.Serialization;

namespace Dotty.App.Services;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(RuntimeSettingsData))]
internal partial class RuntimeSettingsJsonContext : JsonSerializerContext
{
}
