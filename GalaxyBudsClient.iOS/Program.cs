using System;
using System.IO;
using UIKit;

namespace GalaxyBudsClient.iOS;

public class Program
{
    static Program()
    {
        try
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Logs");
            if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "boot.log"), $"[BOOT] {DateTime.Now}: Program class static constructor hit.\n");
        }
        catch { /* Ignore logging error */ }
    }

    static void Main(string[] args)
    {
        var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Logs", "boot.log");
        try
        {
            File.AppendAllText(logPath, $"[BOOT] {DateTime.Now}: Calling UIApplication.Main with delegate: AppDelegate\n");
            
            // 使用显式字符串名称通常比 typeof 更能解决 Native 回调问题
            UIApplication.Main(args, null, "AppDelegate");
            
            File.AppendAllText(logPath, $"[BOOT] {DateTime.Now}: UIApplication.Main returned (unexpectedly)\n");
        }
        catch (Exception ex)
        {
            File.AppendAllText(logPath, $"[BOOT] {DateTime.Now}: CRITICAL EXCEPTION IN MAIN: {ex}\n");
            throw;
        }
    }
}
