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
        try
        {
            UIApplication.Main(args, null, typeof(AppDelegate));
        }
        catch (Exception ex)
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "crash.log");
            File.WriteAllText(path, ex.ToString());
            throw;
        }
    }
}
