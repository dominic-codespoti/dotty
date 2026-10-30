using System.Reflection;

namespace Dotty.Runtime;

/// <summary>
/// The one version string Dotty reports everywhere: <c>dotty --version</c>, <c>TERM_PROGRAM_VERSION</c>, DA2 and
/// XTVERSION. Release builds (published with <c>IncludeSourceRevisionInInformationalVersion=false</c>) report the
/// central version exactly (<c>0.3.0</c>, <c>0.4.0-beta.1</c>); builds whose informational version carries a source
/// revision report a dev version (<c>0.3.0-dev+abc1234</c>).
/// </summary>
public static class DottyVersion
{
    private const int ShortRevisionLength = 7;

    private static readonly string Described = Describe(
        typeof(DottyVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        typeof(DottyVersion).Assembly.GetName().Version);

    /// <summary>The version of the running build; see <see cref="Describe(string?, Version?)"/>.</summary>
    public static string Describe() => Described;

    /// <summary>
    /// Formats a build's version from its informational version (<c>Version[+metadata]</c>), falling back to the
    /// assembly version's <c>major.minor.build</c> (or <c>0.0.0</c>) when it is missing. Build metadata marks a dev
    /// build: <c>0.3.0+&lt;sha&gt;</c> becomes <c>0.3.0-dev+&lt;sha7&gt;</c>, and a prerelease gains a <c>.dev</c>
    /// identifier instead (<c>0.4.0-beta.1+&lt;sha&gt;</c> becomes <c>0.4.0-beta.1.dev+&lt;sha7&gt;</c>). A hex
    /// revision is shortened to 7 digits; other metadata is kept as written.
    /// </summary>
    public static string Describe(string? informationalVersion, Version? assemblyVersion)
    {
        string core = StripBuildMetadata(informationalVersion);
        if (core.Length == 0)
        {
            return assemblyVersion is null
                ? "0.0.0"
                : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{Math.Max(0, assemblyVersion.Build)}";
        }

        string metadata = BuildMetadata(informationalVersion!);
        if (metadata.Length == 0) return core;
        string revision = metadata.Length > ShortRevisionLength && IsHex(metadata)
            ? metadata[..ShortRevisionLength]
            : metadata;
        return core + (core.Contains('-') ? ".dev+" : "-dev+") + revision;
    }

    /// <summary>Removes SemVer build metadata (everything from the first '+'); prerelease labels are kept.</summary>
    public static string StripBuildMetadata(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return string.Empty;
        string trimmed = version.Trim();
        int plus = trimmed.IndexOf('+');
        return plus >= 0 ? trimmed[..plus] : trimmed;
    }

    // The metadata between the first '+' and any further '+' (some SDKs append the revision twice).
    private static string BuildMetadata(string version)
    {
        string trimmed = version.Trim();
        int plus = trimmed.IndexOf('+');
        if (plus < 0) return string.Empty;
        string metadata = trimmed[(plus + 1)..];
        int next = metadata.IndexOf('+');
        return (next >= 0 ? metadata[..next] : metadata).Trim();
    }

    private static bool IsHex(string value)
    {
        foreach (char c in value)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }
        return true;
    }
}
