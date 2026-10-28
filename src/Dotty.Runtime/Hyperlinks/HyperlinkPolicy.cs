using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Dotty.Terminal.Adapter;

namespace Dotty.Runtime.Hyperlinks;

/// <summary>Outcome class of a policy evaluation.</summary>
public enum HyperlinkVerdict
{
    Open,
    Confirm,
    Block,
}

/// <summary>Reasons a link needs explicit user confirmation.</summary>
[Flags]
public enum HyperlinkRisk
{
    None = 0,
    NonWebScheme = 1,
    TextMismatch = 2,
    Userinfo = 4,
    Idn = 8,
    LocalFile = 16,

    /// <summary><c>hyperlinks.confirm = always</c>: every allowed link prompts.</summary>
    ConfirmAlways = 32,
}

/// <summary>Result of <see cref="HyperlinkPolicy.Evaluate"/>.</summary>
/// <param name="Verdict">Open silently, ask first, or refuse.</param>
/// <param name="Target">Canonical target; null only when blocked before parsing completed.</param>
/// <param name="Risks">All risk flags found (also populated for <see cref="HyperlinkVerdict.Open"/> when confirm is <c>never</c>).</param>
/// <param name="BlockReason">Human-readable reason when blocked.</param>
public readonly record struct HyperlinkDecision(
    HyperlinkVerdict Verdict,
    HyperlinkTarget? Target,
    HyperlinkRisk Risks,
    string? BlockReason);

/// <summary>
/// Pure link-opening policy: validation, canonicalization, scheme allowlist, hard blocks and
/// risk classification. Performs no I/O and never mutates input to make it acceptable.
/// </summary>
public sealed class HyperlinkPolicy
{
    private const int WindowsMaxUrlLength = 2083;

    // Schemes that are never openable regardless of configuration (known URI-handler abuse).
    private static readonly HashSet<string> HardDeniedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "javascript", "vbscript", "data", "blob", "about", "jar", "shell", "ms-msdt", "search-ms",
        "search", "ms-search", "ms-officecmd", "ms-appinstaller", "ms-excel", "ms-word",
        "ms-powerpoint", "ms-cxh", "mhtml", "ms-its", "mk", "res", "ie",
    };

    private readonly HyperlinkOptions _options;
    private readonly HashSet<string> _allowed;
    private readonly string _machineName;
    private readonly bool _isWindows;

    public HyperlinkPolicy(HyperlinkOptions options, string? machineName = null, bool? isWindows = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _allowed = new HashSet<string>(options.AllowedSchemes, StringComparer.OrdinalIgnoreCase);
        _machineName = machineName ?? Environment.MachineName;
        _isWindows = isWindows ?? OperatingSystem.IsWindows();
    }

    /// <summary>
    /// Structural validation and canonicalization only: no allowlist, no risk classification.
    /// Used to give the Lua <c>open_url</c> hook a safe, canonical string before scheme policy runs.
    /// Fails (with a human-readable reason) for empty/over-long/control-character input, text that is
    /// not an absolute URL, hard-blocked schemes, and URIs that cannot be canonicalized to ASCII.
    /// </summary>
    public bool TryCanonicalize(
        string? rawUrl,
        [NotNullWhen(true)] out string? canonical,
        [NotNullWhen(false)] out string? blockReason)
    {
        canonical = null;
        if (!TryParse(rawUrl, out Uri? uri, out blockReason))
            return false;

        blockReason = CheckCanonical(uri, out string value);
        if (blockReason is not null)
            return false;

        canonical = value;
        return true;
    }

    /// <summary>Evaluates <paramref name="rawUrl"/> as found in the terminal.</summary>
    /// <param name="rawUrl">Unmodified link text or OSC 8 target.</param>
    /// <param name="source">Detected text or explicit OSC 8.</param>
    /// <param name="displayText">Visible text of an explicit link (for mismatch detection); null otherwise.</param>
    public HyperlinkDecision Evaluate(string? rawUrl, HyperlinkSource source, string? displayText)
    {
        if (!TryParse(rawUrl, out Uri? uri, out string? parseError))
            return Block(parseError);

        string scheme = uri.Scheme.ToLowerInvariant();
        if (!_allowed.Contains(scheme))
            return Block($"scheme '{scheme}' is not allowed (hyperlinks.allowedSchemes)");

        string? canonicalError = CheckCanonical(uri, out string canonical);
        if (canonicalError is not null)
            return Block(canonicalError);

        bool web = scheme == Uri.UriSchemeHttp || scheme == Uri.UriSchemeHttps;
        HyperlinkRisk risks = HyperlinkRisk.None;
        string host = string.Empty;
        string? localPath = null;

        if (uri.HostNameType == UriHostNameType.Dns)
        {
            host = uri.IdnHost;
            if (host.Contains("xn--", StringComparison.OrdinalIgnoreCase) || !IsAscii(uri.Host))
                risks |= HyperlinkRisk.Idn;
        }
        else if (uri.Host.Length != 0)
        {
            host = uri.Host;
        }

        if (web)
        {
            if (uri.Host.Length == 0)
                return Block("link has no host");
            if (uri.UserInfo.Length != 0)
                risks |= HyperlinkRisk.Userinfo;
        }
        else if (scheme == Uri.UriSchemeFile)
        {
            string? reason = FileTargetRules.Check(uri, _machineName, _isWindows, out localPath);
            if (reason is not null)
                return Block(reason);
            risks |= HyperlinkRisk.LocalFile;
        }
        else
        {
            risks |= HyperlinkRisk.NonWebScheme;
            if (scheme == Uri.UriSchemeMailto && MailtoHasAttachment(uri))
                return Block("mailto links with attachments are not allowed");
        }

        if (source == HyperlinkSource.Explicit && web && HyperlinkTextMismatch.IsMismatch(displayText, uri))
            risks |= HyperlinkRisk.TextMismatch;

        var target = new HyperlinkTarget(canonical, scheme, host, localPath, source);
        switch (_options.Confirm)
        {
            case HyperlinkConfirmMode.Never:
                return new HyperlinkDecision(HyperlinkVerdict.Open, target, risks, null);
            case HyperlinkConfirmMode.Always:
                return new HyperlinkDecision(HyperlinkVerdict.Confirm, target, risks | HyperlinkRisk.ConfirmAlways, null);
            default:
                return new HyperlinkDecision(
                    risks == HyperlinkRisk.None ? HyperlinkVerdict.Open : HyperlinkVerdict.Confirm,
                    target,
                    risks,
                    null);
        }
    }

    private bool TryParse(
        string? rawUrl,
        [NotNullWhen(true)] out Uri? uri,
        [NotNullWhen(false)] out string? error)
    {
        uri = null;
        if (string.IsNullOrEmpty(rawUrl))
        {
            error = "link is empty";
            return false;
        }

        switch (HyperlinkUri.Validate(rawUrl.AsSpan()))
        {
            case HyperlinkUriStatus.Empty:
                error = "link is empty";
                return false;
            case HyperlinkUriStatus.TooLong:
                error = "link is too long";
                return false;
            case HyperlinkUriStatus.ControlChar:
                error = "link contains control characters";
                return false;
            case HyperlinkUriStatus.FormatChar:
                error = "link contains invisible or bidirectional-text characters";
                return false;
        }

        string text = rawUrl.Trim(' ');
        if (text.Length == 0)
        {
            error = "link is empty";
            return false;
        }

        if (TryNormalizeScp(text, out string? scp))
            text = scp;

        int colon = SchemeLength(text);
        if (colon < 0)
        {
            error = "not a URL";
            return false;
        }

        // The textual scheme must survive parsing: "C:\\x.exe" parses as a file path, not a "c" URL.
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? parsed)
            || !parsed.Scheme.Equals(text.Substring(0, colon), StringComparison.OrdinalIgnoreCase))
        {
            error = "not a valid URL";
            return false;
        }

        string scheme = parsed.Scheme.ToLowerInvariant();
        if (HardDeniedSchemes.Contains(scheme))
        {
            error = $"scheme '{scheme}' is blocked";
            return false;
        }

        uri = parsed;
        error = null;
        return true;
    }

    private string? CheckCanonical(Uri uri, out string canonical)
    {
        canonical = EscapeCommandLineUnsafe(uri.AbsoluteUri);
        if (canonical.Length > HyperlinkUri.MaxLength)
            return "link is too long";
        if (_isWindows && canonical.Length > WindowsMaxUrlLength)
            return "link is too long for the Windows shell";
        if (HyperlinkUri.Validate(canonical.AsSpan()) != HyperlinkUriStatus.Ok || !IsAscii(canonical))
            return "link could not be canonicalized";
        return null;
    }

    /// <summary>
    /// Percent-encodes the characters that are not valid in a URI and could break out of a Windows
    /// handler command line (<c>space " &lt; &gt; \ ^ ` { | }</c>). <see cref="Uri.AbsoluteUri"/> already escapes them
    /// in paths; this keeps the guarantee independent of runtime canonicalization details (queries, fragments).
    /// </summary>
    private static string EscapeCommandLineUnsafe(string value)
    {
        int first = -1;
        for (int i = 0; i < value.Length; i++)
        {
            if (IsCommandLineUnsafe(value[i]))
            {
                first = i;
                break;
            }
        }

        if (first < 0)
            return value;

        var sb = new System.Text.StringBuilder(value.Length + 8);
        sb.Append(value, 0, first);
        for (int i = first; i < value.Length; i++)
        {
            char c = value[i];
            if (IsCommandLineUnsafe(c))
                sb.Append('%').Append(((int)c).ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            else
                sb.Append(c);
        }

        return sb.ToString();
    }

    private static bool IsCommandLineUnsafe(char c) =>
        c is ' ' or '"' or '<' or '>' or '\\' or '^' or '`' or '{' or '|' or '}';

    private static HyperlinkDecision Block(string reason) =>
        new(HyperlinkVerdict.Block, null, HyperlinkRisk.None, reason);

    /// <summary>Index of the ':' ending a leading RFC 3986 scheme, or -1.</summary>
    private static int SchemeLength(string text)
    {
        if (text.Length == 0 || !char.IsAsciiLetter(text[0]))
            return -1;
        for (int i = 1; i < text.Length; i++)
        {
            char c = text[i];
            if (c == ':')
                return i;
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.'))
                return -1;
        }

        return -1;
    }

    /// <summary>Rewrites scp-style <c>git@host:owner/repo.git</c> to <c>https://host/owner/repo</c>.</summary>
    private static bool TryNormalizeScp(string text, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (!text.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
            return false;

        int colon = text.IndexOf(':', 4);
        if (colon <= 4)
            return false;

        string host = text.Substring(4, colon - 4);
        if (host.Contains('/') || host.Contains(' '))
            return false;

        string path = text.Substring(colon + 1).TrimStart('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path.Substring(0, path.Length - 4);
        if (path.Length == 0)
            return false;

        normalized = "https://" + host + "/" + path;
        return true;
    }

    private static bool MailtoHasAttachment(Uri uri)
    {
        string query = uri.Query;
        if (query.Length <= 1)
            return false;

        foreach (string pair in query.Substring(1).Split('&'))
        {
            int eq = pair.IndexOf('=');
            string key = Uri.UnescapeDataString(eq >= 0 ? pair.Substring(0, eq) : pair).Trim();
            if (key.Equals("attach", StringComparison.OrdinalIgnoreCase)
                || key.Equals("attachment", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsAscii(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] > 0x7E)
                return false;
        }

        return true;
    }
}
