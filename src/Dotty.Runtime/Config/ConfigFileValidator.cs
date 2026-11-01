using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Dotty.Runtime.Scripting;

namespace Dotty.Runtime.Config;

/// <summary>One problem found in a configuration file.</summary>
/// <param name="File">Path of the file as given to the validator.</param>
/// <param name="Line">1-based line, or 0 when the position is unknown.</param>
/// <param name="Column">1-based column (UTF-8 bytes for JSON), or 0 when unknown.</param>
/// <param name="Message">What is wrong.</param>
public readonly record struct ConfigDiagnostic(string File, int Line, int Column, string Message)
{
    /// <summary><c>file:line:column: message</c>, dropping the parts that are unknown.</summary>
    public override string ToString() =>
        Line <= 0 ? $"{File}: {Message}"
        : Column <= 0 ? $"{File}:{Line}: {Message}"
        : $"{File}:{Line}:{Column}: {Message}";
}

/// <summary>
/// Checks <c>config.json</c> and <c>config.lua</c> for the problems the loader would otherwise
/// swallow: JSON syntax errors, values of the wrong type, unknown keys (typos are ignored by the loader),
/// and Lua syntax errors. Lua is compiled, never run; runtime errors in a script still surface at launch.
/// </summary>
public static class ConfigFileValidator
{
    /// <summary>Chunk name used when compiling Lua, so messages are <c>config:LINE: text</c> whatever the path length.</summary>
    private const string LuaChunkName = "=config";

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly (HashSet<string> Options, HashSet<string> Groups) KnownPaths = BuildKnownPaths();

    /// <summary>Validates the UTF-8 content of a <c>config.json</c>; an empty list means valid.</summary>
    public static IReadOnlyList<ConfigDiagnostic> ValidateJson(string file, ReadOnlySpan<byte> utf8)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (utf8.StartsWith("\uFEFF"u8))
            utf8 = utf8[3..];

        var diagnostics = new List<ConfigDiagnostic>();
        if (utf8.Trim(" \t\r\n"u8).IsEmpty)
        {
            diagnostics.Add(new ConfigDiagnostic(file, 0, 0, "the file is empty; delete it or write '{}' to use the defaults"));
            return diagnostics;
        }

        try
        {
            var reader = new Utf8JsonReader(utf8, ReaderOptions);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                AddAt(diagnostics, file, utf8, reader.TokenStartIndex, "the top level must be a JSON object");
                return diagnostics;
            }
            WalkObject(ref reader, "", file, utf8, diagnostics);
            while (reader.Read())
            {
            }
        }
        catch (JsonException ex)
        {
            diagnostics.Add(FromJsonException(file, ex, null));
            return diagnostics;
        }

        try
        {
            _ = JsonSerializer.Deserialize(utf8, DottyUserConfigJsonContext.Default.DottyUserConfig);
        }
        catch (JsonException ex)
        {
            string? path = ex.Path is { Length: > 2 } p && p.StartsWith("$.", StringComparison.Ordinal) ? p[2..] : null;
            // A group given a non-object value was already reported by the walk; the serializer fails on that same token.
            if (path is null || !KnownPaths.Groups.Contains(path))
                diagnostics.Add(FromJsonException(file, ex, path));
        }
        return diagnostics;
    }

    /// <summary>Compiles the text of a Lua config without running it; an empty list means it compiles.</summary>
    public static IReadOnlyList<ConfigDiagnostic> ValidateLua(string file, string source)
    {
        ArgumentNullException.ThrowIfNull(file);
        string? error = LuaScriptHost.CheckSyntax(source, LuaChunkName);
        if (error is null)
            return [];
        return [ParseLuaError(file, error)];
    }

    /// <summary>Turns <c>config:LINE: text</c> from <see cref="LuaScriptHost.CheckSyntax"/> into a diagnostic.</summary>
    internal static ConfigDiagnostic ParseLuaError(string file, string message)
    {
        const string Prefix = "config:";
        if (message.StartsWith(Prefix, StringComparison.Ordinal))
        {
            int digits = Prefix.Length;
            int line = 0;
            while (digits < message.Length && char.IsAsciiDigit(message[digits]) && line < 100_000_000)
                line = (line * 10) + (message[digits++] - '0');
            if (digits > Prefix.Length && digits + 1 < message.Length && message[digits] == ':' && message[digits + 1] == ' ')
                return new ConfigDiagnostic(file, line, 0, message[(digits + 2)..]);
        }
        return new ConfigDiagnostic(file, 0, 0, message);
    }

    private static void WalkObject(
        ref Utf8JsonReader reader,
        string prefix,
        string file,
        ReadOnlySpan<byte> utf8,
        List<ConfigDiagnostic> diagnostics)
    {
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string path = prefix + reader.GetString();
            long nameStart = reader.TokenStartIndex;
            reader.Read();

            if (KnownPaths.Options.Contains(path))
            {
                reader.Skip();
            }
            else if (KnownPaths.Groups.Contains(path))
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    WalkObject(ref reader, path + ".", file, utf8, diagnostics);
                }
                else
                {
                    AddAt(diagnostics, file, utf8, nameStart, $"'{path}' is a group of options and must be an object");
                    reader.Skip();
                }
            }
            else
            {
                IReadOnlyList<ConfigOption> suggestions = ConfigOptionCatalog.Suggest(path, max: 1);
                string hint = suggestions.Count > 0 ? $"; did you mean '{suggestions[0].JsonPath}'?" : "; run 'dotty +explain-config' to list every option";
                AddAt(diagnostics, file, utf8, nameStart, $"unknown option '{path}'{hint}");
                reader.Skip();
            }
        }
    }

    private static void AddAt(List<ConfigDiagnostic> diagnostics, string file, ReadOnlySpan<byte> utf8, long offset, string message)
    {
        ReadOnlySpan<byte> before = utf8[..(int)offset];
        int lastNewline = before.LastIndexOf((byte)'\n');
        int line = before.Count((byte)'\n') + 1;
        int column = (int)offset - lastNewline;
        diagnostics.Add(new ConfigDiagnostic(file, line, column, message));
    }

    private static ConfigDiagnostic FromJsonException(string file, JsonException ex, string? optionPath)
    {
        string message = ex.Message;
        int pathMarker = message.IndexOf(" Path: ", StringComparison.Ordinal);
        if (pathMarker < 0)
            pathMarker = message.IndexOf(" LineNumber: ", StringComparison.Ordinal);
        if (pathMarker > 0)
            message = message[..pathMarker];
        if (optionPath is not null)
            message = $"invalid value for '{optionPath}': {message}";
        int line = ex.LineNumber is long l ? (int)l + 1 : 0;
        int column = ex.BytePositionInLine is long c && line > 0 ? (int)c + 1 : 0;
        return new ConfigDiagnostic(file, line, column, message);
    }

    private static (HashSet<string> Options, HashSet<string> Groups) BuildKnownPaths()
    {
        var options = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ConfigOption option in ConfigOptionCatalog.All)
        {
            options.Add(option.JsonPath);
            for (int dot = option.JsonPath.IndexOf('.'); dot > 0; dot = option.JsonPath.IndexOf('.', dot + 1))
                groups.Add(option.JsonPath[..dot]);
        }
        return (options, groups);
    }
}
