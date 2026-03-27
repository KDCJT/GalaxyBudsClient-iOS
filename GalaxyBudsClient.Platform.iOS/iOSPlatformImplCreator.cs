using GalaxyBudsClient.Platform.Interfaces;

namespace GalaxyBudsClient.Platform.iOS;

public class iOSPlatformImplCreator : IPlatformImplCreator
{
    public IDesktopServices CreateDesktopServices() => new DesktopServices();
    public IBluetoothService CreateBluetoothService() => new BluetoothService();
    public IHotkeyBroadcast? CreateHotkeyBroadcast() => null;
    public IHotkeyReceiver? CreateHotkeyReceiver() => null;
    public IMediaKeyRemote? CreateMediaKeyRemote() => null; // To be implemented later
    public INotificationListener? CreateNotificationListener() => null;
    public IOfficialAppDetector? CreateOfficialAppDetector() => null;
}
