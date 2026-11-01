using System;
using System.Collections.Generic;
using System.IO;

namespace Dotty.Runtime.Sessions;

/// <summary>
/// Pure core of shell-integration auto-injection. Given the resolved shell it computes the argv /
/// environment rewrite that makes the shell load Dotty's scripts without touching the user's dotfiles.
/// Never throws for bad input: any doubt means "launch unchanged".
/// </summary>
public static class ShellIntegrationLauncher
{
    public const string ModeNone = "none";
    public const string ModeDetect = "detect";

    /// <summary>Relative locations inside the integration directory; part of the packaging contract.</summary>
    internal static readonly string[] BashRcfileParts = ["inject", "bash", "dotty-rcfile.bash"];
    internal static readonly string[] ZshDirParts = ["inject", "zsh"];
    internal static readonly string[] FishDirParts = ["inject", "xdg"];

    private const string DefaultXdgDataDirs = "/usr/local/share:/usr/share";

    /// <summary>
    /// Classifies a shell by file-name stem (case-insensitive, <c>.exe</c> stripped). Accepts both
    /// <c>/</c> and <c>\</c> separators regardless of host OS. Returns <see cref="ShellKind.Unknown"/>
    /// for anything else (including <c>sh</c>, <c>dash</c>, <c>nu</c>, <c>cmd</c>).
    /// </summary>
    public static ShellKind Classify(string? executableOrShellString)
    {
        if (string.IsNullOrWhiteSpace(executableOrShellString))
            return ShellKind.Unknown;
        string value = executableOrShellString.Trim();
        int separator = value.LastIndexOfAny(['/', '\\']);
        string name = separator >= 0 ? value[(separator + 1)..] : value;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name.ToLowerInvariant() switch
        {
            "bash" => ShellKind.Bash,
            "zsh" => ShellKind.Zsh,
            "fish" => ShellKind.Fish,
            "pwsh" or "powershell" => ShellKind.PowerShell,
            _ => ShellKind.Unknown,
        };
    }

    /// <summary>Normalizes a configured mode: <c>detect</c> or <c>none</c>; anything else is <c>none</c>.</summary>
    public static bool IsDetectMode(string? mode) =>
        string.Equals(mode?.Trim(), ModeDetect, StringComparison.OrdinalIgnoreCase);

    /// <param name="shell">Resolved shell string (path, bare name, or command line) or <c>null</c>.</param>
    /// <param name="shellIsExecutable">True when <paramref name="shell"/> is a literal executable path.</param>
    /// <param name="userCommand">Explicit user command argv; never rewritten.</param>
    /// <param name="mode"><c>detect</c> enables injection; everything else (including typos) disables it.</param>
    /// <param name="integrationDir">Directory holding the scripts, or <c>null</c> when not found.</param>
    /// <param name="getenv">Environment lookup of the Dotty process.</param>
    /// <param name="fileExists">Test seam; defaults to <see cref="File.Exists(string?)"/>.</param>
    public static ShellLaunch Prepare(
        string? shell,
        bool shellIsExecutable,
        IReadOnlyList<string>? userCommand,
        string? mode,
        string? integrationDir,
        Func<string, string?> getenv,
        bool isWindows,
        Func<string, bool>? fileExists = null)
    {
        try
        {
            return PrepareCore(shell, shellIsExecutable, userCommand, mode, integrationDir, getenv, isWindows,
                fileExists ?? File.Exists);
        }
        catch (Exception)
        {
            return ShellLaunch.None;
        }
    }

    private static ShellLaunch PrepareCore(
        string? shell,
        bool shellIsExecutable,
        IReadOnlyList<string>? userCommand,
        string? mode,
        string? integrationDir,
        Func<string, string?> getenv,
        bool isWindows,
        Func<string, bool> fileExists)
    {
        if (userCommand is not null || !IsDetectMode(mode))
            return ShellLaunch.None;
        if (string.IsNullOrWhiteSpace(shell) || string.IsNullOrWhiteSpace(integrationDir))
            return ShellLaunch.None;
        if (IsOptedOut(getenv("DOTTY_SHELL_INTEGRATION")))
            return ShellLaunch.None;

        string executable = shell.Trim();
        // A command line with arguments ("/usr/bin/env bash", "bash --login") is never rewritten,
        // unless the whole string is itself an existing file (e.g. "C:\Program Files\...\pwsh.exe").
        if (ContainsWhitespace(executable) && !(shellIsExecutable || fileExists(executable) || fileExists(shell)))
            return ShellLaunch.None;

        ShellKind kind = Classify(executable);
        if (kind == ShellKind.Unknown)
            return ShellLaunch.None;

        Dictionary<string, string> env = new(StringComparer.Ordinal)
        {
            ["DOTTY_SHELL_INTEGRATION"] = "1",
            ["DOTTY_SHELL_INTEGRATION_DIR"] = integrationDir,
        };

        switch (kind)
        {
            case ShellKind.Bash:
                {
                    string rcfile = Path.Combine(integrationDir, Path.Combine(BashRcfileParts));
                    return new ShellLaunch([executable, "--rcfile", rcfile, "-i"], env);
                }
            case ShellKind.Zsh:
                {
                    if (isWindows) return ShellLaunch.None;
                    env["ZDOTDIR"] = Path.Combine(integrationDir, Path.Combine(ZshDirParts));
                    string? userZdotdir = getenv("ZDOTDIR");
                    if (!string.IsNullOrEmpty(userZdotdir))
                        env["DOTTY_ZSH_ZDOTDIR"] = userZdotdir;
                    return new ShellLaunch(null, env);
                }
            case ShellKind.Fish:
                {
                    // ':' separates XDG_DATA_DIRS entries; a directory containing one cannot be expressed.
                    if (isWindows || integrationDir.Contains(':')) return ShellLaunch.None;
                    string? existing = getenv("XDG_DATA_DIRS");
                    if (string.IsNullOrEmpty(existing)) existing = DefaultXdgDataDirs;
                    env["XDG_DATA_DIRS"] = Path.Combine(integrationDir, Path.Combine(FishDirParts)) + ":" + existing;
                    return new ShellLaunch(null, env);
                }
            case ShellKind.PowerShell:
                {
                    string script = EscapePowerShellSingleQuoted(Path.Combine(integrationDir, "Dotty.ps1"));
                    string expression =
                        "try { Invoke-Expression ([System.IO.File]::ReadAllText('" + script + "')) } catch { }";
                    return new ShellLaunch([executable, "-NoLogo", "-NoExit", "-Command", expression], env);
                }
            default:
                return ShellLaunch.None;
        }
    }

    /// <summary>
    /// Escapes text for a PowerShell single-quoted string. PowerShell treats the typographic quotes U+2018/2019/201A/201B
    /// as single quotes too, so every one of them is doubled.
    /// </summary>
    private static string EscapePowerShellSingleQuoted(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 2);
        foreach (char c in value)
        {
            builder.Append(c);
            if (c is '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B')
                builder.Append(c);
        }
        return builder.ToString();
    }

    private static bool IsOptedOut(string? value) =>
        value is not null && value.Trim().ToLowerInvariant() is "0" or "none" or "false" or "off";

    private static bool ContainsWhitespace(string value)
    {
        foreach (char c in value)
            if (char.IsWhiteSpace(c)) return true;
        return false;
    }
}
