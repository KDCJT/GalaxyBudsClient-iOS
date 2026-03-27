using System.Threading.Tasks;
using GalaxyBudsClient.Platform.Interfaces;

namespace GalaxyBudsClient.Platform.iOS;

public class DesktopServices : IDesktopServices
{
    public Task OpenUrlAsync(string url)
    {
        // iOS implementation to open URL
        return Task.CompletedTask;
    }
}
