using System.Threading;
using System.Threading.Tasks;

namespace Dotty.Runtime.Hyperlinks;

/// <summary>
/// Hands an already-approved, canonical <see cref="HyperlinkTarget"/> to the host system.
/// Implementations never throw: failures are reported through <see cref="HyperlinkOpenResult"/>.
/// </summary>
public interface IHyperlinkOpener
{
    Task<HyperlinkOpenResult> OpenAsync(HyperlinkTarget target, CancellationToken ct = default);
}
