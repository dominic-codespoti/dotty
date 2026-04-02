namespace Dotty.Config.SourceGenerator.Models;

/// <summary>
/// Record representing a key binding.
/// </summary>
public record KeyBindingModel
{
    public string Key { get; init; } = "";
    public string Modifiers { get; init; } = "";
    public string Action { get; init; } = "";

    public KeyBindingModel() { }

    public KeyBindingModel(string key, string modifiers, string action)
    {
        Key = key;
        Modifiers = modifiers;
        Action = action;
    }
}
