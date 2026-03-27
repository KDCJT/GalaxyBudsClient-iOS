using Foundation;
using UIKit;
using Avalonia;
using Avalonia.iOS;
using Avalonia.ReactiveUI;

namespace GalaxyBudsClient.iOS;

[Register("AppDelegate")]
public class AppDelegate : AvaloniaAppDelegate<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        GalaxyBudsClient.Platform.PlatformImpl.InjectExternalBackend(new GalaxyBudsClient.Platform.iOS.iOSPlatformImplCreator());

        return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .UseReactiveUI();
    }
}
