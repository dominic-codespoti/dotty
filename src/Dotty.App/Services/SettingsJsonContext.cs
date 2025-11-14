using System.Text.Json.Serialization;

namespace Dotty.App.Services
{
    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(UserSettings))]
    public partial class SettingsJsonContext : JsonSerializerContext
    {
    }
}
