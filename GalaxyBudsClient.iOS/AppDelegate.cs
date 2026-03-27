using System;
using Foundation;
using UIKit;
using Avalonia;
using Avalonia.iOS;
using Avalonia.ReactiveUI;

namespace GalaxyBudsClient.iOS;

[Register("AppDelegate")]
public class AppDelegate : AvaloniaAppDelegate<App>
{
    private static readonly string LogPath = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments),
        "Logs", "boot.log");

    public AppDelegate()
    {
        try
        {
            System.IO.File.AppendAllText(LogPath, $"[BOOT] {DateTime.Now}: AppDelegate constructor hit.\n");

            // Catch any unhandled managed exception (covers Avalonia internal init crashes)
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                try { System.IO.File.AppendAllText(LogPath, $"[BOOT] {DateTime.Now}: AppDomain UnhandledException: {e.ExceptionObject}\n"); } catch { }
            };

            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                try
                {
                    System.IO.File.AppendAllText(LogPath, $"[BOOT] {DateTime.Now}: UnobservedTaskException: {e.Exception}\n");
                    e.SetObserved();
                }
                catch { }
            };
        }
        catch { }
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        try
        {
            System.IO.File.AppendAllText(LogPath, $"[BOOT] {DateTime.Now}: Injecting Platform Backend...\n");
            GalaxyBudsClient.Platform.PlatformImpl.InjectExternalBackend(new GalaxyBudsClient.Platform.iOS.iOSPlatformImplCreator());
            System.IO.File.AppendAllText(LogPath, $"[BOOT] {DateTime.Now}: Platform Backend Injected.\n");
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText(LogPath, $"[BOOT] {DateTime.Now}: ERROR during backend injection: {ex}\n");
        }

        System.IO.File.AppendAllText(LogPath, $"[BOOT] {DateTime.Now}: Calling base.CustomizeAppBuilder...\n");
        var result = base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .UseReactiveUI();
        System.IO.File.AppendAllText(LogPath, $"[BOOT] {DateTime.Now}: base.CustomizeAppBuilder returned.\n");

        return result;
    }
}
