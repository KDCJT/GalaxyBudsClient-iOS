using Foundation;
using Splat;

// Force the iOS AOT compiler to include Splat's interface types as full definitions.
// Without this, the AOT compiler only has a typeref (external reference) to
// IMutableDependencyResolver, which cannot be resolved at runtime, causing a
// TypeLoadException when UseReactiveUI() calls Splat.Locator.RegisterResolverCallbackChanged().
[assembly: Preserve]

namespace GalaxyBudsClient.iOS;

public static class SplatPreservation
{
    // Keeping a static field of the interface type forces the AOT compiler to include
    // the full typedef (type definition) for IMutableDependencyResolver in its metadata,
    // rather than just a typeref (cross-assembly reference) that fails to resolve at runtime.
    [Preserve]
    private static readonly IMutableDependencyResolver? _resolver = null;

    [Preserve]
    private static readonly IReadonlyDependencyResolver? _readonlyResolver = null;

    [Preserve]
    public static void EnsureRegistered()
    {
        // Touch types to force AOT inclusion. This method never needs to be called - 
        // the static field declarations above are sufficient. This is just a belt-and-suspenders.
        GC.KeepAlive(typeof(IMutableDependencyResolver));
        GC.KeepAlive(typeof(IReadonlyDependencyResolver));
        GC.KeepAlive(typeof(InternalLocator));
        GC.KeepAlive(typeof(ModernDependencyResolver));
    }
}
