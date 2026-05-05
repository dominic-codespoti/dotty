using System.Runtime.InteropServices;

namespace Dotty.Terminal.Adapter;

[StructLayout(LayoutKind.Sequential)]
public struct ColdCell
{
    public ushort HyperlinkId;
    public short GraphemeIndex;

    public void Reset()
    {
        HyperlinkId = 0;
        GraphemeIndex = -1;
    }
}
