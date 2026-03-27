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
        var logPath = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments), "Logs", "boot.log");
        try
        {
            System.IO.File.AppendAllText(logPath, $"[BOOT] {System.DateTime.Now}: Injecting Platform Backend...\n");
            GalaxyBudsClient.Platform.PlatformImpl.InjectExternalBackend(new GalaxyBudsClient.Platform.iOS.iOSPlatformImplCreator());
            System.IO.File.AppendAllText(logPath, $"[BOOT] {System.DateTime.Now}: Platform Backend Injected.\n");
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText(logPath, $"[BOOT] {System.DateTime.Now}: ERROR during backend injection: {ex}\n");
        }

        System.IO.File.AppendAllText(logPath, $"[BOOT] {System.DateTime.Now}: Calling base.CustomizeAppBuilder...\n");
        var result = base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .UseReactiveUI();
        System.IO.File.AppendAllText(logPath, $"[BOOT] {System.DateTime.Now}: base.CustomizeAppBuilder returned.\n");
        
        return result;
    }

    public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
    {
        var logPath = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments), "Logs", "boot.log");
        try
        {
            System.IO.File.AppendAllText(logPath, $"[BOOT] {System.DateTime.Now}: FinishedLaunching - calling Avalonia base...\n");
            var result = base.FinishedLaunching(application, launchOptions);
            System.IO.File.AppendAllText(logPath, $"[BOOT] {System.DateTime.Now}: FinishedLaunching base returned: {result}\n");
            return result;
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText(logPath, $"[BOOT] {System.DateTime.Now}: FATAL in FinishedLaunching: {ex}\n");
            // Don't rethrow - return false so we get the log instead of a silent crash
            return false;
        }
    }
}
