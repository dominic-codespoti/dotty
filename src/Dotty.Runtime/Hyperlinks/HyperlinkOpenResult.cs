namespace Dotty.Runtime.Hyperlinks;

/// <summary>Outcome of an attempt to hand a link to the operating system.</summary>
public readonly record struct HyperlinkOpenResult(bool Success, string? Error)
{
    public static HyperlinkOpenResult Opened { get; } = new(true, null);

    public static HyperlinkOpenResult Failed(string error) => new(false, error);
}
