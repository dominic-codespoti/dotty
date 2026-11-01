using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Dotty.Runtime.Input;

/// <summary>Quoting dialect used when a dropped path is pasted.</summary>
public enum ShellQuoteStyle
{
    /// <summary>sh, bash, zsh, dash, ksh: bare when safe, otherwise single quotes with <c>'\''</c>.</summary>
    Posix,

    /// <summary>fish: single quotes; <c>\</c> and <c>'</c> are backslash-escaped inside them.</summary>
    Fish,

    /// <summary>PowerShell (pwsh, powershell): single quotes; every single-quote character (incl. typographic ones) doubled.</summary>
    PowerShell,

    /// <summary>cmd.exe: double quotes; paths containing <c>"</c>, <c>%</c> or <c>!</c> are rejected (cmd cannot quote them safely).</summary>
    Cmd,
}

/// <summary>
/// Pure shell-escaping of dropped file paths. Paths with control characters (C0, DEL, C1), unpaired surrogates or
/// characters the dialect cannot quote are rejected rather than pasted, so a crafted file name can never inject a
/// newline or escape sequence.
/// </summary>
public static class DropPathEscaper
{
    /// <summary>
    /// Resolves <c>drop.quote</c>: an explicit style name, or <c>auto</c> (any unknown value) to the style of the
    /// pane's launch shell (file name, case-insensitive; Windows default cmd, elsewhere posix).
    /// </summary>
    public static ShellQuoteStyle ResolveStyle(string? quote, string? launchShell, bool isWindows)
    {
        switch (quote?.Trim().ToLowerInvariant())
        {
            case "posix": return ShellQuoteStyle.Posix;
            case "fish": return ShellQuoteStyle.Fish;
            case "powershell": return ShellQuoteStyle.PowerShell;
            case "cmd": return ShellQuoteStyle.Cmd;
        }
        if (string.IsNullOrWhiteSpace(launchShell))
            return isWindows ? ShellQuoteStyle.Cmd : ShellQuoteStyle.Posix;
        string name = Path.GetFileName(launchShell.Trim().Replace('\\', '/')).ToLowerInvariant();
        if (name.EndsWith(".exe", StringComparison.Ordinal))
            name = name[..^4];
        return name switch
        {
            "fish" => ShellQuoteStyle.Fish,
            "pwsh" or "powershell" or "pwsh-preview" => ShellQuoteStyle.PowerShell,
            "cmd" => ShellQuoteStyle.Cmd,
            _ => ShellQuoteStyle.Posix,
        };
    }

    /// <summary>Escapes one path; false (and null) when the path is empty or cannot be pasted safely.</summary>
    public static bool TryEscape(string? path, ShellQuoteStyle style, out string? escaped)
    {
        escaped = null;
        if (string.IsNullOrEmpty(path) || !IsPastable(path))
            return false;
        switch (style)
        {
            case ShellQuoteStyle.Posix:
                escaped = IsPosixBare(path) ? path : "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
                return true;
            case ShellQuoteStyle.Fish:
                escaped = IsPosixBare(path) ? path
                    : "'" + path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
                return true;
            case ShellQuoteStyle.PowerShell:
                {
                    var builder = new StringBuilder(path.Length + 2).Append('\'');
                    foreach (char c in path)
                    {
                        builder.Append(c);
                        if (c is '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B')
                            builder.Append(c);
                    }
                    escaped = builder.Append('\'').ToString();
                    return true;
                }
            case ShellQuoteStyle.Cmd:
                if (path.AsSpan().IndexOfAny("\"%!") >= 0)
                    return false;
                escaped = "\"" + path + "\"";
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Escapes every path and joins them with single spaces. Unpastable paths are skipped and counted in
    /// <paramref name="rejected"/>; the result is null when nothing remains.
    /// </summary>
    public static string? Build(IEnumerable<string?> paths, ShellQuoteStyle style, out int rejected)
    {
        rejected = 0;
        StringBuilder? builder = null;
        foreach (string? path in paths)
        {
            if (!TryEscape(path, style, out string? escaped))
            {
                rejected++;
                continue;
            }
            builder ??= new StringBuilder();
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(escaped);
        }
        return builder?.ToString();
    }

    private static bool IsPastable(string path)
    {
        for (int i = 0; i < path.Length; i++)
        {
            char c = path[i];
            if (c < 0x20 || c == 0x7f || (c >= 0x80 && c <= 0x9f) || c is '\u2028' or '\u2029')
                return false;
            if (char.IsHighSurrogate(c) && i + 1 < path.Length && char.IsLowSurrogate(path[i + 1]))
            {
                i++;
                continue;
            }
            if (char.IsSurrogate(c))
                return false;
        }
        return true;
    }

    // Characters no POSIX shell or fish treats specially anywhere in a word ('~' and '=' only at the start).
    private static bool IsPosixBare(string path)
    {
        if (path[0] is '~' or '=' or '-' or '%')
            return false;
        foreach (char c in path)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or '+' or ',' or ':' or '@' or '%' or '='))
                return false;
        }
        return true;
    }
}
