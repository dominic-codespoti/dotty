using System;
using Dotty.Abstractions.Config;

namespace Dotty.Abstractions.Graphics;

/// <summary>Transmission media a Kitty graphics client may use (<c>t=</c> key).</summary>
[Flags]
public enum KittyMediaFlags
{
    None = 0,
    /// <summary>Payload inside the escape sequence (<c>t=d</c>).</summary>
    Direct = 1,
    /// <summary>Regular file (<c>t=f</c>).</summary>
    File = 2,
    /// <summary>Temporary file the terminal deletes after reading (<c>t=t</c>).</summary>
    TempFile = 4,
    /// <summary>POSIX shared memory object (<c>t=s</c>).</summary>
    SharedMemory = 8,
}

/// <summary>Effective Kitty graphics protocol settings for a session.</summary>
public sealed record KittyGraphicsOptions
{
    /// <summary>Hard upper bound for <see cref="StorageLimitBytes"/>.</summary>
    public const long HardMaxStorageLimitBytes = 4096L * 1024 * 1024;
    /// <summary>Hard upper bound for <see cref="MaxPayloadBytes"/>.</summary>
    public const long HardMaxPayloadBytes = 1024L * 1024 * 1024;
    /// <summary>Hard upper bound for <see cref="MaxImageDimension"/>.</summary>
    public const int HardMaxImageDimension = 100_000;

    public bool Enabled { get; init; } = DottyDefaults.KittyGraphicsEnabled;
    public long StorageLimitBytes { get; init; } = DottyDefaults.KittyImageStorageLimitBytes;
    public long MaxPayloadBytes { get; init; } = DottyDefaults.KittyMaxPayloadBytes;
    public int MaxImageDimension { get; init; } = DottyDefaults.KittyMaxImageDimension;
    public int MaxPlacementsPerScreen { get; init; } = DottyDefaults.KittyMaxPlacementsPerScreen;
    public KittyMediaFlags Media { get; init; } = KittyMediaFlags.Direct;

    /// <summary>The built-in options.</summary>
    public static KittyGraphicsOptions DefaultOptions { get; } = new();

    /// <summary>
    /// Non-positive limits fall back to the defaults, over-large ones clamp to the hard maxima,
    /// and <see cref="KittyMediaFlags.Direct"/> is always set.
    /// </summary>
    public KittyGraphicsOptions Normalize() => this with
    {
        StorageLimitBytes = StorageLimitBytes <= 0
            ? DottyDefaults.KittyImageStorageLimitBytes
            : Math.Min(StorageLimitBytes, HardMaxStorageLimitBytes),
        MaxPayloadBytes = MaxPayloadBytes <= 0
            ? DottyDefaults.KittyMaxPayloadBytes
            : Math.Min(MaxPayloadBytes, HardMaxPayloadBytes),
        MaxImageDimension = MaxImageDimension <= 0
            ? DottyDefaults.KittyMaxImageDimension
            : Math.Min(MaxImageDimension, HardMaxImageDimension),
        MaxPlacementsPerScreen = MaxPlacementsPerScreen <= 0
            ? DottyDefaults.KittyMaxPlacementsPerScreen
            : MaxPlacementsPerScreen,
        Media = Media | KittyMediaFlags.Direct,
    };
}
