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

        File.AppendAllText(LogPath, $"[BOOT] {DateTime.Now}: Calling base.CustomizeAppBuilder...\n");
        var result = base.CustomizeAppBuilder(builder).WithInterFont();
        File.AppendAllText(LogPath, $"[BOOT] {DateTime.Now}: base.CustomizeAppBuilder returned.\n");

        try
        {
            RxApp.MainThreadScheduler = AvaloniaScheduler.Instance;
        }
        catch (Exception ex)
        {
            File.AppendAllText(LogPath, $"[BOOT] {DateTime.Now}: ERROR setting scheduler: {ex}\n");
        }

        // We no longer blindly schedule the dialog.
        // PrivateBluetoothService will trigger it via ShowMacInputDialog if it really cannot find any devices automatically.

        return result;
    }


    internal static void ShowMacSetupDialog(Action? onSaved = null)
    {
        var macKey = GalaxyBudsClient.Platform.iOS.PrivateBluetoothService.MacAddressDefaultsKey;
        var currentMac = NSUserDefaults.StandardUserDefaults.StringForKey(macKey) ?? "";
        var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Logs", "boot.log");

        File.AppendAllText(logPath, $"[BOOT] {DateTime.Now}: ShowMacSetupDialog called. CurrentMac={currentMac}\n");

        var alert = UIAlertController.Create(
            "请获取并输入 Galaxy Buds MAC 地址",
            "自动读取系统蓝牙配对记录失败（或者是首次安装）。\n\niOS 14+ 隐藏了真实的蓝牙 MAC 地址。\n如果您需要临时连接，请将耳机连接到 Windows 电脑（设备属性 -> 详细信息 -> 蓝牙设备地址）或任意安卓手机，在此处填入 12 位大写地址。\n\n格式：AA:BB:CC:DD:EE:FF",
            UIAlertControllerStyle.Alert);

        alert.AddTextField(tf =>
        {
            tf.Text = currentMac;
            tf.Placeholder = "AA:BB:CC:DD:EE:FF";
            tf.AutocorrectionType = UITextAutocorrectionType.No;
            tf.AutocapitalizationType = UITextAutocapitalizationType.AllCharacters;
        });

        alert.AddAction(UIAlertAction.Create("保存并连接", UIAlertActionStyle.Default, action =>
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

        // iOS 15 compatible: use UIWindowScene instead of deprecated KeyWindow
        var presenter = GetPresentingViewController(logPath);
        if (presenter == null)
        {
            File.AppendAllText(logPath, $"[BOOT] {DateTime.Now}: ERROR: Could not find presenter VC\n");
            return;
        }
        File.AppendAllText(logPath, $"[BOOT] {DateTime.Now}: Presenting alert on {presenter.GetType().Name}\n");
        presenter.PresentViewController(alert, animated: true, completionHandler: null);
    }

    /// <summary>
    /// Gets the top-most view controller using iOS 15-compatible APIs.
    /// Falls back to KeyWindow for older iOS versions.
    /// </summary>
    private static UIViewController? GetPresentingViewController(string logPath)
    {
        try
        {
            // iOS 13+: Use UIWindowScene
            UIWindow? window = null;
            var scenes = UIApplication.SharedApplication.ConnectedScenes;
            if (scenes != null)
            {
                foreach (var scene in scenes)
                {
                    if (scene is UIWindowScene windowScene)
                    {
                        window = windowScene.Windows?
                            .OrderByDescending(w => (double)w.WindowLevel)
                            .FirstOrDefault(w => !w.Hidden);
                        if (window != null) break;
                    }
                }
            }

            // Fallback to KeyWindow for iOS < 13
            window ??= UIApplication.SharedApplication.KeyWindow;

            File.AppendAllText(logPath, $"[BOOT] {DateTime.Now}: Found window: {window?.GetType().Name ?? "null"}\n");

            if (window?.RootViewController == null)
            {
                File.AppendAllText(logPath, $"[BOOT] {DateTime.Now}: RootViewController is null\n");
                return null;
            }

            return GetTopViewController(window.RootViewController);
        }
        catch (Exception ex)
        {
            File.AppendAllText(logPath, $"[BOOT] {DateTime.Now}: GetPresentingVC error: {ex.Message}\n");
            return null;
        }
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
