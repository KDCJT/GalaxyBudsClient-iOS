using Foundation;
using UIKit;
using Avalonia;
using Avalonia.iOS;
using Avalonia.ReactiveUI;

namespace GalaxyBudsClient.iOS;

[Register("AppDelegate")]
public class AppDelegate : AvaloniaAppDelegate<App>
{
    public AppDelegate()
    {
        try
        {
            var logPath = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments), "Logs", "boot.log");
            System.IO.File.AppendAllText(logPath, $"[BOOT] {System.DateTime.Now}: AppDelegate constructor hit.\n");
        }
        catch { }
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        GalaxyBudsClient.Platform.PlatformImpl.InjectExternalBackend(new GalaxyBudsClient.Platform.iOS.iOSPlatformImplCreator());

        return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .UseReactiveUI();
    }
}
