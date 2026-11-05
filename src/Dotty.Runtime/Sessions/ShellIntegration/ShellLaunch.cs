using System;
using System.Collections.Generic;

namespace Dotty.Runtime.Sessions;

/// <summary>
/// Result of <see cref="ShellIntegrationLauncher.Prepare"/>. <see cref="Command"/> is <c>null</c> when
/// the launch argv must stay unchanged; <see cref="Environment"/> holds extra variables (possibly empty).
/// </summary>
public readonly record struct ShellLaunch(
    IReadOnlyList<string>? Command,
    IReadOnlyDictionary<string, string> Environment)
{
    private static readonly IReadOnlyDictionary<string, string> NoEnvironment = new Dictionary<string, string>(0);

    /// <summary>Launch unchanged: no argv rewrite, no extra environment.</summary>
    public static ShellLaunch None => new(null, NoEnvironment);

    /// <summary>True when nothing is rewritten.</summary>
    public bool IsNone => Command is null && (Environment is null || Environment.Count == 0);
}
