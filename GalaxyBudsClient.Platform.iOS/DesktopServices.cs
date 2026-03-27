using Foundation;
using UIKit;
using GalaxyBudsClient.Platform.Interfaces;

namespace GalaxyBudsClient.Platform.iOS;

public class DesktopServices : IDesktopServices
{
    public bool IsAutoStartEnabled 
    { 
        get => false; 
        set { /* Not supported on iOS */ } 
    }

    public void OpenUri(string uri)
    {
        var url = NSUrl.FromString(uri);
        if (url != null)
        {
            UIApplication.SharedApplication.OpenUrl(url, new NSDictionary(), null);
        }
    }
}
