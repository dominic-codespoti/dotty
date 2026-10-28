namespace Dotty.Runtime.Sessions;

/// <summary>Shells Dotty knows how to inject its shell integration into.</summary>
public enum ShellKind
{
    Unknown,
    Bash,
    Zsh,
    Fish,
    PowerShell,
}
