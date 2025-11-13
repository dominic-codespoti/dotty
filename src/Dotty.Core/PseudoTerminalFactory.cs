using System.Runtime.InteropServices;

namespace Dotty.Core;

public static class PseudoTerminalFactory
{
    public static IPseudoTerminalFactory CreateDefault()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("Pseudo-terminals are not supported on Windows yet.");
        }

        return new UnixPtyFactory();
    }
}

