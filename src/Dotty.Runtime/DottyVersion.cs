using System.Reflection;

namespace Dotty.Runtime;

/// <summary>The Dotty release version without SemVer build metadata (<c>0.3.0+sha</c> becomes <c>0.3.0</c>).</summary>
public static class DottyVersion
{
    public static string Current { get; } = Resolve();

    /// <summary>Removes SemVer build metadata (everything from the first '+'); prerelease labels are kept.</summary>
    public static string StripBuildMetadata(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return string.Empty;
        string trimmed = version.Trim();
        int plus = trimmed.IndexOf('+');
        return plus >= 0 ? trimmed[..plus] : trimmed;
    }

    private static string Resolve()
    {
        string stripped = StripBuildMetadata(
            typeof(DottyVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        if (stripped.Length > 0) return stripped;

        Version? assemblyVersion = typeof(DottyVersion).Assembly.GetName().Version;
        return assemblyVersion is null
            ? "0.0.0"
            : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{Math.Max(0, assemblyVersion.Build)}";
    }
}
