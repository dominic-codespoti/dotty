using System.Text.Json;
using System.Text.Json.Serialization;
using Dotty.Abstractions.Themes;

namespace Dotty.App.Services;

/// <summary>
/// JSON source generation context for theme-related types.
/// Required for AOT compatibility to avoid reflection-based serialization.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(ThemeDefinition))]
[JsonSerializable(typeof(ThemeDefinition[]))]
[JsonSerializable(typeof(ThemeRoot))]
[JsonSerializable(typeof(ThemeColors))]
internal partial class ThemeJsonContext : JsonSerializerContext
{
}
