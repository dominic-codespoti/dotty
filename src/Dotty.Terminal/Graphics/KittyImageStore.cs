using System;
using System.Collections.Generic;

namespace Dotty.Terminal.Graphics;

/// <summary>
/// Images of one screen: id model, byte accounting and quota eviction. Not thread-safe; callers hold the terminal
/// buffer lock. Placements (see <c>KittyImageStore.Placements.cs</c>) hang off their image
/// (<see cref="KittyImage.PlacementCount"/>; an image with none is "unreferenced").
/// </summary>
/// <remarks>
/// Ids: <see cref="KittyImage.InternalId"/> is monotonic and never reused (also across <see cref="Clear"/>);
/// <see cref="KittyImage.ClientId"/> is the protocol <c>i</c> (0 = anonymous); <see cref="KittyImage.Number"/> is the
/// protocol <c>I</c>, resolved to the newest image carrying it. Client ids allocated for <c>I=</c> transmissions come
/// from <c>[0x80000000, 0xFFFFFFFF]</c> and skip ids in use.
/// <para>Eviction (after every add and when the limit shrinks): unreferenced anonymous images always go; while the
/// store is over its limit the remaining images go in the order unreferenced first, then transient first, then oldest
/// access, then lowest internal id.</para>
/// </remarks>
internal sealed partial class KittyImageStore : IDisposable
{
    /// <summary>First client id handed out for <c>I=</c> transmissions.</summary>
    public const uint FirstAllocatedClientId = 0x8000_0000u;

    private readonly Dictionary<long, KittyImage> _byInternalId = new();
    private readonly Dictionary<uint, KittyImage> _byClientId = new();
    private readonly Dictionary<uint, KittyImage> _newestByNumber = new();
    private long _nextInternalId = 1;
    private uint _nextAllocatedClientId = FirstAllocatedClientId;
    private long _usedBytes;
    private int _anonymousCount;
    private long _storageLimitBytes;
    private bool _disposed;

    public KittyImageStore(long storageLimitBytes)
    {
        if (storageLimitBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(storageLimitBytes));
        _storageLimitBytes = storageLimitBytes;
    }

    public long StorageLimitBytes => _storageLimitBytes;

    /// <summary>Sum of the byte lengths of all live images' frames.</summary>
    public long UsedBytes => _usedBytes;

    public int Count => _byInternalId.Count;

    /// <summary>The live images in no particular order. Do not mutate the store while enumerating.</summary>
    public IEnumerable<KittyImage> Images => _byInternalId.Values;

    public KittyImage? FindByInternalId(long internalId) =>
        _byInternalId.TryGetValue(internalId, out var image) ? image : null;

    /// <summary>The image with protocol id <paramref name="clientId"/>; null for 0 or an unknown id.</summary>
    public KittyImage? FindByClientId(uint clientId) =>
        clientId != 0 && _byClientId.TryGetValue(clientId, out var image) ? image : null;

    /// <summary>The newest image transmitted with image number <paramref name="number"/>; null for 0 or none.</summary>
    public KittyImage? FindByNumber(uint number) =>
        number != 0 && _newestByNumber.TryGetValue(number, out var image) ? image : null;

    /// <summary>
    /// Returns an unused client id from <c>[0x80000000, 0xFFFFFFFF]</c>, or 0 when that whole range is in use.
    /// Successive calls walk the range upwards (wrapping), so a just-freed id is not handed out again soon.
    /// </summary>
    public uint AllocateClientId()
    {
        ulong rangeSize = 0x1_0000_0000UL - FirstAllocatedClientId;
        for (ulong tried = 0; tried < rangeSize && tried <= (ulong)_byClientId.Count; tried++)
        {
            uint candidate = _nextAllocatedClientId;
            _nextAllocatedClientId = candidate == uint.MaxValue ? FirstAllocatedClientId : candidate + 1;
            if (!_byClientId.ContainsKey(candidate))
                return candidate;
        }
        return 0;
    }

    /// <summary>
    /// Adds an image and applies the quota. Takes ownership of <paramref name="pixels"/> only when it returns true.
    /// Returns false, leaving the store and <paramref name="pixels"/> untouched, when the pixels alone exceed the
    /// storage limit. An existing image with the same non-zero <paramref name="clientId"/> is deleted first. The new
    /// image is never evicted by its own add.
    /// </summary>
    public bool TryAdd(ImagePixels pixels, uint clientId, uint number, bool transient, long nowTicks, out KittyImage? image)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ObjectDisposedException.ThrowIf(_disposed, this);
        image = null;
        if (pixels.ByteLength > _storageLimitBytes)
            return false;

        if (clientId != 0 && _byClientId.TryGetValue(clientId, out var old))
            Delete(old);

        image = new KittyImage(_nextInternalId++, clientId, number, transient, pixels, nowTicks);
        _byInternalId.Add(image.InternalId, image);
        if (clientId != 0)
            _byClientId.Add(clientId, image);
        else
            _anonymousCount++;
        if (number != 0)
            _newestByNumber[number] = image; // internal ids grow, so the latest add is the newest
        _usedBytes += pixels.ByteLength;

        EnforceQuota(protect: image);
        return true;
    }

    /// <summary>Marks the image as used now (LRU).</summary>
    public void TouchAccess(KittyImage image, long nowTicks)
    {
        if (!image.IsDeleted && nowTicks > image.LastAccessTicks)
            image.LastAccessTicks = nowTicks;
    }

    /// <summary>Deletes an image and releases its pixels. Returns false when it is not (or no longer) in the store.</summary>
    public bool Delete(KittyImage image)
    {
        if (!_byInternalId.TryGetValue(image.InternalId, out var live) || !ReferenceEquals(live, image))
            return false;

        RemovePlacementsOf(image);
        _byInternalId.Remove(image.InternalId);
        if (image.ClientId != 0)
            _byClientId.Remove(image.ClientId);
        else
            _anonymousCount--;
        if (image.Number != 0 && _newestByNumber.TryGetValue(image.Number, out var newest) && ReferenceEquals(newest, image))
            RecomputeNewestForNumber(image.Number);
        _usedBytes -= image.ByteLength;
        image.ReleasePixels();
        return true;
    }

    /// <summary>Deletes the image with the given client id; false when there is none.</summary>
    public bool DeleteByClientId(uint clientId)
    {
        var image = FindByClientId(clientId);
        return image != null && Delete(image);
    }

    /// <summary>Changes the quota and evicts down to it immediately.</summary>
    public void SetStorageLimit(long storageLimitBytes)
    {
        if (storageLimitBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(storageLimitBytes));
        _storageLimitBytes = storageLimitBytes;
        EnforceQuota(protect: null);
    }

    /// <summary>Deletes every image. Ids already handed out are not reused.</summary>
    public void Clear()
    {
        if (_byInternalId.Count == 0)
            return;
        var all = new List<KittyImage>(_byInternalId.Values);
        for (int i = 0; i < all.Count; i++)
            Delete(all[i]);
    }

    /// <summary>Clears the store; further <see cref="TryAdd"/> calls throw.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        Clear();
        _disposed = true;
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when the internal structures disagree: byte accounting
    /// versus the live images, the three lookup maps, the newest-by-number choice, placement counts, the limit.
    /// </summary>
    public void ValidateInvariants()
    {
        long sum = 0;
        int withClientId = 0;
        var newest = new Dictionary<uint, long>();
        foreach (var (internalId, image) in _byInternalId)
        {
            Require(image.InternalId == internalId, "image stored under a foreign internal id");
            Require(!image.IsDeleted, "deleted image still in the store");
            Require(image.Frames.Count >= 1, "image without frames");
            Require(internalId < _nextInternalId, "internal id not below the next id");
            sum += image.ByteLength;
            if (image.ClientId != 0)
            {
                withClientId++;
                Require(_byClientId.TryGetValue(image.ClientId, out var byId) && ReferenceEquals(byId, image),
                    "client id map misses a live image");
            }
            if (image.Number != 0 && (!newest.TryGetValue(image.Number, out long best) || internalId > best))
                newest[image.Number] = internalId;
        }
        Require(sum == _usedBytes, $"used bytes {_usedBytes} != sum of images {sum}");
        Require(withClientId == _byClientId.Count, "client id map has stale entries");
        Require(_byInternalId.Count - withClientId == _anonymousCount, "anonymous image count is wrong");
        foreach (var (clientId, image) in _byClientId)
            Require(image.ClientId == clientId && _byInternalId.ContainsKey(image.InternalId), "client id map entry is inconsistent");
        Require(newest.Count == _newestByNumber.Count, "newest-by-number map has wrong size");
        foreach (var (number, image) in _newestByNumber)
            Require(newest.TryGetValue(number, out long best) && best == image.InternalId, "newest-by-number entry is not the newest");
        Require(_usedBytes <= _storageLimitBytes, "store is over its storage limit");
        ValidatePlacements();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("KittyImageStore invariant violated: " + message);
    }

    private void RecomputeNewestForNumber(uint number)
    {
        KittyImage? best = null;
        foreach (var image in _byInternalId.Values)
        {
            if (image.Number == number && (best == null || image.InternalId > best.InternalId))
                best = image;
        }
        if (best == null)
            _newestByNumber.Remove(number);
        else
            _newestByNumber[number] = best;
    }

    private void EnforceQuota(KittyImage? protect)
    {
        // 1. Unreferenced anonymous images can never be addressed again.
        List<KittyImage>? doomed = null;
        int othersAnonymous = _anonymousCount - (protect is { ClientId: 0 } ? 1 : 0);
        if (othersAnonymous > 0)
        {
            foreach (var image in _byInternalId.Values)
            {
                if (image.ClientId == 0 && image.PlacementCount == 0 && !ReferenceEquals(image, protect))
                    (doomed ??= new List<KittyImage>()).Add(image);
            }
        }
        if (doomed != null)
        {
            for (int i = 0; i < doomed.Count; i++)
                Delete(doomed[i]);
        }

        // 2. Still over the limit: unreferenced first, then transient first, then least recently used.
        if (_usedBytes <= _storageLimitBytes)
            return;
        var candidates = new List<KittyImage>(_byInternalId.Count);
        foreach (var image in _byInternalId.Values)
        {
            if (!ReferenceEquals(image, protect))
                candidates.Add(image);
        }
        candidates.Sort(CompareForEviction);
        for (int i = 0; i < candidates.Count && _usedBytes > _storageLimitBytes; i++)
            Delete(candidates[i]);
    }

    private static int CompareForEviction(KittyImage a, KittyImage b)
    {
        int c = (a.PlacementCount == 0 ? 0 : 1).CompareTo(b.PlacementCount == 0 ? 0 : 1);
        if (c != 0) return c;
        c = (a.Transient ? 0 : 1).CompareTo(b.Transient ? 0 : 1);
        if (c != 0) return c;
        c = a.LastAccessTicks.CompareTo(b.LastAccessTicks);
        return c != 0 ? c : a.InternalId.CompareTo(b.InternalId);
    }
}
