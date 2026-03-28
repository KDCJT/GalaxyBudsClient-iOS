using System;
using Foundation;
using UIKit;
using Avalonia;
using Avalonia.iOS;
using Avalonia.ReactiveUI;
using ReactiveUI;

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

        // NOTE: We deliberately call UseReactiveUI() inside a try-catch so that even if
        // the Splat TypeLoadException fires, we can log it and still return a valid builder.
        // The app will have degraded ReactiveUI functionality but will at least launch.
        System.IO.File.AppendAllText(LogPath, $"[BOOT] {DateTime.Now}: Calling base.CustomizeAppBuilder (without UseReactiveUI)...\n");
        var result = base.CustomizeAppBuilder(builder).WithInterFont();
        System.IO.File.AppendAllText(LogPath, $"[BOOT] {DateTime.Now}: base.CustomizeAppBuilder returned.\n");

        // Try to manually set up the ReactiveUI scheduler which is the only essential part
        try
        {
            System.IO.File.AppendAllText(LogPath, $"[BOOT] {DateTime.Now}: Setting RxApp.MainThreadScheduler...\n");
            RxApp.MainThreadScheduler = AvaloniaScheduler.Instance;
            System.IO.File.AppendAllText(LogPath, $"[BOOT] {DateTime.Now}: RxApp.MainThreadScheduler set.\n");
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText(LogPath, $"[BOOT] {DateTime.Now}: ERROR setting scheduler: {ex}\n");
        }

        return result;
    }
}
