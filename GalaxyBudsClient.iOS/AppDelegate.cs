using System;
using System.IO;
using System.Linq;
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
    private static readonly string DocsPath =
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    private static readonly string LogPath = Path.Combine(DocsPath, "Logs", "boot.log");

    public AppDelegate()
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(DocsPath, "Logs"));
            File.AppendAllText(LogPath, $"[BOOT] {DateTime.Now}: AppDelegate constructor hit.\n");

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                try { File.AppendAllText(LogPath, $"[BOOT] {DateTime.Now}: AppDomain UnhandledException: {e.ExceptionObject}\n"); } catch { }
            };

            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                try
                {
                    File.AppendAllText(LogPath, $"[BOOT] {DateTime.Now}: UnobservedTaskException: {e.Exception}\n");
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
            File.AppendAllText(LogPath, $"[BOOT] {DateTime.Now}: Injecting Platform Backend...\n");
            GalaxyBudsClient.Platform.PlatformImpl.InjectExternalBackend(
                new GalaxyBudsClient.Platform.iOS.iOSPlatformImplCreator());
            File.AppendAllText(LogPath, $"[BOOT] {DateTime.Now}: Platform Backend Injected.\n");

            // Register the dialog callback so PrivateBluetoothService can trigger it
            // without creating a circular project dependency.
            GalaxyBudsClient.Platform.iOS.PrivateBluetoothService.ShowMacInputDialog =
                () => ShowMacSetupDialog();
        }
        catch (Exception ex)
        {
            File.AppendAllText(LogPath, $"[BOOT] {DateTime.Now}: ERROR during backend injection: {ex}\n");
        }

        File.AppendAllText(LogPath, $"[BOOT] {DateTime.Now}: Calling base.CustomizeAppBuilder (without UseReactiveUI)...\n");
        var result = base.CustomizeAppBuilder(builder).WithInterFont();
        File.AppendAllText(LogPath, $"[BOOT] {DateTime.Now}: base.CustomizeAppBuilder returned.\n");

        try
        {
            File.AppendAllText(LogPath, $"[BOOT] {DateTime.Now}: Setting RxApp.MainThreadScheduler...\n");
            RxApp.MainThreadScheduler = AvaloniaScheduler.Instance;
            File.AppendAllText(LogPath, $"[BOOT] {DateTime.Now}: RxApp.MainThreadScheduler set.\n");
        }
        catch (Exception ex)
        {
            File.AppendAllText(LogPath, $"[BOOT] {DateTime.Now}: ERROR setting scheduler: {ex}\n");
        }

        // Schedule native MAC address setup dialog (deferred so Avalonia window loads first)
        ScheduleMacSetupDialogIfNeeded();

        return result;
    }

    /// <summary>
    /// Shows a native UIAlertController asking the user for the Galaxy Buds MAC address
    /// the first time the app runs, or when no device is configured.
    /// This bypasses the Avalonia UI entirely and uses native iOS dialogs.
    /// </summary>
    private void ScheduleMacSetupDialogIfNeeded()
    {
        // Delay to allow Avalonia's root view controller to initialize
        NSRunLoop.Main.BeginInvokeOnMainThread(() =>
        {
            System.Threading.Tasks.Task.Delay(1500).ContinueWith(_ =>
            {
                NSRunLoop.Main.BeginInvokeOnMainThread(() =>
                {
                    try { ShowMacSetupDialog(); }
                    catch (Exception ex)
                    {
                        File.AppendAllText(LogPath, $"[BOOT] {DateTime.Now}: ShowMacSetupDialog error: {ex}\n");
                    }
                });
            });
        });
    }

    internal static void ShowMacSetupDialog(Action? onSaved = null)
    {
        var macKey = GalaxyBudsClient.Platform.iOS.PrivateBluetoothService.MacAddressDefaultsKey;
        var currentMac = NSUserDefaults.StandardUserDefaults.StringForKey(macKey) ?? "";
        var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Logs", "boot.log");

        File.AppendAllText(logPath, $"[BOOT] {DateTime.Now}: ShowMacSetupDialog called. CurrentMac={currentMac}\n");

        var alert = UIAlertController.Create(
            "配置 Galaxy Buds 设备",
            "请在 iOS【设置 → 蓝牙】中，点击 Galaxy Buds 旁边的 ⓘ，找到\"地址\"栏（格式：AA:BB:CC:DD:EE:FF）并输入。",
            UIAlertControllerStyle.Alert);

        alert.AddTextField(tf =>
        {
            tf.Text = currentMac;
            tf.Placeholder = "AA:BB:CC:DD:EE:FF";
            tf.AutocorrectionType = UITextAutocorrectionType.No;
            tf.AutocapitalizationType = UITextAutocapitalizationType.AllCharacters;
            tf.KeyboardType = UIKeyboardType.Default;
        });

        alert.AddAction(UIAlertAction.Create("保存", UIAlertActionStyle.Default, action =>
        {
            var mac = alert.TextFields?.FirstOrDefault()?.Text?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(mac))
            {
                NSUserDefaults.StandardUserDefaults.SetString(mac, macKey);
                NSUserDefaults.StandardUserDefaults.Synchronize();
                File.AppendAllText(logPath, $"[BOOT] {DateTime.Now}: Saved MAC: {mac}\n");
                onSaved?.Invoke();
            }
        }));

        alert.AddAction(UIAlertAction.Create("取消", UIAlertActionStyle.Cancel, null));

        var rootVc = UIApplication.SharedApplication.KeyWindow?.RootViewController;
        var presenter = GetTopViewController(rootVc);
        presenter?.PresentViewController(alert, true, null);
    }

    private static UIViewController? GetTopViewController(UIViewController? vc)
    {
        if (vc == null) return null;
        if (vc.PresentedViewController != null) return GetTopViewController(vc.PresentedViewController);
        if (vc is UINavigationController nav) return GetTopViewController(nav.VisibleViewController);
        if (vc is UITabBarController tab) return GetTopViewController(tab.SelectedViewController);
        return vc;
    }
}
