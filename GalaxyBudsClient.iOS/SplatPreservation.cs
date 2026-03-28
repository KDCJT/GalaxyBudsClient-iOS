using System;
using Foundation;
using Splat;

// Force the iOS AOT compiler to include Splat's interface types as full definitions.
[assembly: Preserve]

namespace GalaxyBudsClient.iOS;

public static class SplatPreservation
{
    // Static fields of interface types force the AOT compiler to include
    // the full typedef (type definition) for these types in its metadata.
    [Preserve]
    private static readonly IMutableDependencyResolver? _resolver = null;

    [Preserve]
    private static readonly IReadonlyDependencyResolver? _readonlyResolver = null;

    [Preserve]
    public static void EnsureRegistered()
    {
        GC.KeepAlive(typeof(IMutableDependencyResolver));
        GC.KeepAlive(typeof(IReadonlyDependencyResolver));
        GC.KeepAlive(typeof(ModernDependencyResolver));
    }
}
