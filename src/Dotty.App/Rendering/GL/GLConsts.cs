namespace Dotty.App.Rendering;

/// <summary>GL constants used by the terminal GL surface (GlInterface exposes
/// only a subset as properties; these mirror the GL spec values).</summary>
public static class GLConsts
{
    public const int GL_ARRAY_BUFFER = 0x8892;
    public const int GL_ELEMENT_ARRAY_BUFFER = 0x8089;
    public const int GL_STATIC_DRAW = 0x88E4;
    public const int GL_DYNAMIC_DRAW = 0x88E8;
    public const int GL_FLOAT = 0x1406;
    public const int GL_TRIANGLES = 0x0004;
    public const int GL_UNSIGNED_SHORT = 0x1403;
    public const int GL_COLOR_BUFFER_BIT = 0x4000;
    public const int GL_TEXTURE0 = 0x84C0;
}
