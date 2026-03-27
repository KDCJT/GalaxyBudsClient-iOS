using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Serilog;

namespace GalaxyBudsClient.Platform;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class PlatformUtils
{
    public enum Platforms
    {
        Windows,
        Linux,
        OSX,
        Android,
        iOS,
        Other
    }

    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public static bool IsOSX => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    public static bool IsAndroid => RuntimeInformation.IsOSPlatform(OSPlatform.Create("ANDROID")) || RuntimeInformation.RuntimeIdentifier.Contains("android", StringComparison.OrdinalIgnoreCase);
    public static bool IsiOS => RuntimeInformation.IsOSPlatform(OSPlatform.Create("IOS")) || RuntimeInformation.RuntimeIdentifier.Contains("ios", StringComparison.OrdinalIgnoreCase);
    public static bool IsMobile => IsAndroid || IsiOS;
    public static bool IsDesktop => IsWindows || IsLinux || IsOSX;
    public static bool IsRunningInFlatpak => Environment.GetEnvironmentVariable("container") != null;

    public static bool SupportsTrayIcon => IsWindows || IsLinux || IsOSX;
    public static bool SupportsAutoboot => IsWindows || (IsLinux && !IsRunningInFlatpak) || IsOSX;
    public static bool SupportsHotkeys => IsWindows || IsLinux || IsOSX;
    public static bool SupportsHotkeysBroadcast => IsWindows || IsLinux || IsOSX;
    public static bool SupportsNotificationListener => IsLinux;
    public static bool SupportsMicaTheme => IsWindows && Environment.OSVersion.Version.Build >= 22000;
    public static bool SupportsBlurTheme => IsWindows || IsOSX || (IsLinux && Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")?.Contains("KDE") == true);
        
    public static Platforms Platform
    {
        get
        {
            if (IsWindows)
                return Platforms.Windows;
            if (IsLinux)
                return Platforms.Linux;
            if (IsOSX)
                return Platforms.OSX;
            if (IsiOS)
                return Platforms.iOS;
            return IsAndroid ? Platforms.Android : Platforms.Other;
        }
    }
        
    public static bool IsWindowsContractsSdkSupported
    {
        get
        {
            if (!IsWindows)
            {
                return false;
            }
                
            try
            {
#pragma warning disable CA1416
                var release = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                    "ReleaseId", "")?.ToString();
                var major = Convert.ToInt32(release?[..2]);
                var minor = Convert.ToInt32(release?[2..4]);
                return major >= 18 && minor >= 03;
#pragma warning restore CA1416
            }
            catch (Exception ex)
            {
                Log.Error("PlatformUtils: Cannot determine build version: {ExMessage}", ex.Message);
                return false;
            }
        }
    }
        
    public static string CombineDataPath(string postfix)
    {
        var path = Path.Combine(AppDataPath, postfix);
        var dir = Path.GetDirectoryName(path);
        if (dir != null && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        return path;
    }

    public static string AppDataPath => IsiOS 
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GalaxyBudsClient")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GalaxyBudsClient");
}