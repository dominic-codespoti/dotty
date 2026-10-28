namespace Dotty.Terminal.Graphics;

internal sealed partial class KittyGraphicsController
{
    /// <summary>
    /// <c>a=p</c>/<c>a=T</c> with <c>P</c>/<c>Q</c>: places <paramref name="image"/> relative to the parent placement
    /// (<c>H</c> columns and <c>V</c> rows from its top-left cell). Position is resolved when the screen is captured,
    /// so nothing is anchored here and the cursor never moves. The errors are the store's
    /// (<see cref="KittyImageStore.TryPlaceRelative"/>).
    /// </summary>
    private KittyStatus PlaceRelative(in KittyGraphicsCommand cmd, KittyImage image)
    {
        var store = Store;
        var status = store.TryPlaceRelative(image, in cmd, _host.CellPixelWidth, _host.CellPixelHeight, out var placement);
        if (status != KittyStatus.Ok || placement == null)
            return status;

        store.TouchAccess(image, _host.NowTicks);
        _host.GraphicsChanged();
        return KittyStatus.Ok;
    }
}
