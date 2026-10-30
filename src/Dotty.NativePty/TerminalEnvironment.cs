using System;
using System.Collections.Generic;

namespace Dotty.NativePty;

/// <summary>Terminal identity variables both PTY backends apply after the caller's environment.</summary>
internal static class TerminalEnvironment
{
    public const string DefaultTerm = "xterm-256color";

    /// <summary>
    /// The <c>TERM</c> the caller supplied (the session passes its resolved <c>term</c>), otherwise
    /// <see cref="DefaultTerm"/>. The value inherited from Dotty's own environment is never used. Names compare
    /// case-insensitively on Windows, like its environment block.
    /// </summary>
    public static string Term(IDictionary<string, string>? environmentVariables)
    {
        if (environmentVariables is null) return DefaultTerm;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (KeyValuePair<string, string> variable in environmentVariables)
        {
            if (string.Equals(variable.Key, "TERM", comparison) && !string.IsNullOrWhiteSpace(variable.Value))
                return variable.Value;
        }
        return DefaultTerm;
    }
}
