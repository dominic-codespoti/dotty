using System.Text;
using Dotty.Abstractions.Config;

namespace Dotty.Runtime.Input;

/// <summary>
/// Text for <c>dotty +explain-config keybindings</c>: every bindable action with its description, the live binding
/// table (built-ins plus the user's map), and the entries that were rejected and why.
/// </summary>
public static class KeybindingReport
{
    public static string Render(KeybindingManager manager)
    {
        var text = new StringBuilder(4096);
        text.Append("\nActions:\n");
        foreach (var info in ActionRegistry.All)
        {
            text.Append("  ").Append(info.TakesArgument ? info.Name + ":<text>" : info.Name)
                .Append(" - ").Append(info.Description).Append('\n');
        }

        text.Append("\nBindings (").Append(manager.Platform).Append("):\n");
        foreach (var (chord, action) in manager.GetBindings())
            text.Append("  ").Append(chord).Append(" = ").Append(ActionRegistry.Format(action)).Append('\n');

        if (manager.Diagnostics.Count > 0)
        {
            text.Append("\nRejected entries:\n");
            foreach (var diagnostic in manager.Diagnostics)
                text.Append("  ").Append(diagnostic.ToString()).Append('\n');
        }
        return text.ToString();
    }
}
