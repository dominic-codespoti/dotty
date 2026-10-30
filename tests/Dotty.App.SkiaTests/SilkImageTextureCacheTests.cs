using Dotty.Silk;
using Dotty.Terminal.Graphics;
using Xunit;

namespace Dotty.App.SkiaTests;

[Collection(nameof(NativeGlCollection))]
public sealed class SilkImageTextureCacheTests
{
    private static ImagePixels Image(int w, int h, byte v)
    {
        ImagePixels p = ImagePixels.Allocate(w, h, ImagePixelFormat.Rgba8Premultiplied);
        p.AsWritableSpan().Fill(v);
        return p;
    }

    [Fact]
    public void SameSerialUploadsOnceAndReusesTexture()
    {
        using ImageGlHost? host = ImageGlHost.TryCreate();
        if (host is null) return;
        using var cache = new SilkImageTextureCache(host.Gl, new DirectGlBindingCache(host.Gl));
        using ImagePixels px = Image(4, 4, 255);

        cache.BeginFrame();
        Assert.True(cache.TryGetTexture(px, out uint t1));
        Assert.True(cache.TryGetTexture(px, out uint t2));
        Assert.NotEqual(0u, t1);
        Assert.Equal(t1, t2);
        Assert.Equal(1, cache.TextureCount);
        Assert.Equal(4 * 4 * 4, cache.EstimatedTextureBytes);
        Assert.Equal(64, cache.UploadedBytesThisFrame);
    }

    [Fact]
    public void IdleTextureIsEvictedAfterLimitAndReuploadedOnDemand()
    {
        using ImageGlHost? host = ImageGlHost.TryCreate();
        if (host is null) return;
        using var cache = new SilkImageTextureCache(host.Gl, new DirectGlBindingCache(host.Gl), maxIdleFrames: 2);
        using ImagePixels px = Image(4, 4, 255);

        cache.BeginFrame();
        Assert.True(cache.TryGetTexture(px, out _));
        cache.EndFrame();

        for (int i = 0; i < 2; i++)   // idle for exactly 2 frames: still resident
        {
            cache.BeginFrame();
            cache.EndFrame();
            Assert.True(cache.Contains(px.Serial));
        }

        cache.BeginFrame();           // idle 3 > 2
        cache.EndFrame();
        Assert.False(cache.Contains(px.Serial));
        Assert.Equal(0, cache.TextureCount);
        Assert.Equal(0, cache.EstimatedTextureBytes);

        cache.BeginFrame();
        Assert.True(cache.TryGetTexture(px, out uint tex));
        Assert.NotEqual(0u, tex);
        Assert.Equal(1, cache.TextureCount);
    }

    [Fact]
    public void GpuBudgetEvictsLeastRecentlyUsedButKeepsCurrentFrame()
    {
        using ImageGlHost? host = ImageGlHost.TryCreate();
        if (host is null) return;
        // Budget fits two 64-byte textures.
        using var cache = new SilkImageTextureCache(host.Gl, new DirectGlBindingCache(host.Gl), gpuBudgetBytes: 128);
        using ImagePixels a = Image(4, 4, 1);
        using ImagePixels b = Image(4, 4, 2);
        using ImagePixels c = Image(4, 4, 3);

        cache.BeginFrame(); Assert.True(cache.TryGetTexture(a, out _)); cache.EndFrame();
        cache.BeginFrame(); Assert.True(cache.TryGetTexture(b, out _)); cache.EndFrame();
        cache.BeginFrame(); Assert.True(cache.TryGetTexture(c, out _)); cache.EndFrame();

        Assert.False(cache.Contains(a.Serial));
        Assert.True(cache.Contains(b.Serial));
        Assert.True(cache.Contains(c.Serial));
        Assert.Equal(128, cache.EstimatedTextureBytes);
    }

    [Fact]
    public void RetiredSerialsAreDeletedAtNextFrame()
    {
        using ImageGlHost? host = ImageGlHost.TryCreate();
        if (host is null) return;
        using var cache = new SilkImageTextureCache(host.Gl, new DirectGlBindingCache(host.Gl));
        using ImagePixels px = Image(2, 2, 9);
        cache.BeginFrame();
        Assert.True(cache.TryGetTexture(px, out _));
        cache.EndFrame();

        ImageRetirementQueue.Retire(px.Serial);
        cache.BeginFrame();
        Assert.False(cache.Contains(px.Serial));
        Assert.Equal(0, cache.EstimatedTextureBytes);
    }

    [Fact]
    public void ReuploadAfterDeletingTheBoundTextureBindsTheNewTexture()
    {
        using ImageGlHost? host = ImageGlHost.TryCreate();
        if (host is null) return;
        using var cache = new SilkImageTextureCache(host.Gl, new DirectGlBindingCache(host.Gl));
        using ImagePixels px = Image(4, 4, 200);
        cache.BeginFrame();
        Assert.True(cache.TryGetTexture(px, out _));   // the upload leaves this texture bound

        // Deleting it resets the GL binding to 0 and a driver may hand the same name out again: the binding cache must
        // have been told, or the second upload would be skipped-bound and land in texture 0.
        cache.Retire(px.Serial);
        Assert.True(cache.TryGetTexture(px, out uint again));

        Assert.False(cache.HasFailed(px.Serial));
        host.Gl.GetInteger(global::Silk.NET.OpenGL.GetPName.TextureBinding2D, out int bound);
        Assert.Equal((int)again, bound);
    }

    [Fact]
    public void DeferredUploadKeepsOrderAndReportsDeferred()
    {
        using ImageGlHost? host = ImageGlHost.TryCreate();
        if (host is null) return;
        using var cache = new SilkImageTextureCache(host.Gl, new DirectGlBindingCache(host.Gl), uploadBudgetBytes: 100);
        using ImagePixels a = Image(4, 4, 1);
        using ImagePixels b = Image(4, 4, 2);
        using ImagePixels c = Image(2, 2, 3);   // would fit, but must not jump the queue

        cache.BeginFrame();
        Assert.True(cache.TryGetTexture(a, out _));
        Assert.False(cache.TryGetTexture(b, out _));
        Assert.False(cache.TryGetTexture(c, out _));
        Assert.True(cache.UploadsDeferred);
        Assert.False(cache.Contains(b.Serial));

        cache.BeginFrame();
        Assert.False(cache.UploadsDeferred);
        Assert.True(cache.TryGetTexture(a, out _));   // resident: costs no budget
        Assert.True(cache.TryGetTexture(b, out _));
        Assert.True(cache.TryGetTexture(c, out _));
        Assert.Equal(64 + 16, cache.UploadedBytesThisFrame);
    }

    [Fact]
    public void ImageLargerThanWholeBudgetStillUploadsAlone()
    {
        using ImageGlHost? host = ImageGlHost.TryCreate();
        if (host is null) return;
        using var cache = new SilkImageTextureCache(host.Gl, new DirectGlBindingCache(host.Gl), uploadBudgetBytes: 10);
        using ImagePixels a = Image(4, 4, 1);
        cache.BeginFrame();
        Assert.True(cache.TryGetTexture(a, out _));
        Assert.False(cache.UploadsDeferred);
    }
}
