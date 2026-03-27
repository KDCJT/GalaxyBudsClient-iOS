using System;
using System.IO;
using UIKit;

namespace GalaxyBudsClient.iOS;

public class Program
{
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
