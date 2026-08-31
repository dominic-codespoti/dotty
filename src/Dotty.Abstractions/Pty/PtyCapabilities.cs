using System;
using System.Runtime.InteropServices;

namespace Dotty.Abstractions.Pty;

public enum PtyBackend
{
    None,
    UnixHelper,
    ConPty,
}

public enum PtyErrorCode
{
    UnsupportedPlatform,
    UnsupportedArchitecture,
    ConPtyUnavailable,
    NativeHelperMissing,
    NativeHelperNotExecutable,
    ProcessStartFailed,
    ControlSocketUnavailable,
    ResizeFailed,
    InvalidDimensions,
    InvalidShell,
    InvalidWorkingDirectory,
    Disposed,
    NativeOperationFailed,
}

public readonly record struct PtyCapabilities(
    string RuntimeIdentifier,
    Architecture Architecture,
    PtyBackend Backend,
    bool PlatformSupported,
    bool NativeDependencyAvailable,
    string? Diagnostic)
{
    public bool IsSupported => PlatformSupported && NativeDependencyAvailable;
}
