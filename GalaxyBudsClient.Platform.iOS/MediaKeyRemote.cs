using GalaxyBudsClient.Platform.Interfaces;

namespace GalaxyBudsClient.Platform.iOS;

public class MediaKeyRemote : IMediaKeyRemote
{
    public void Play() { /* Handled by OS/Command Center hooks */ }
    public void Pause() { /* Handled by OS/Command Center hooks */ }
    public void PlayPause() { /* Handled by OS/Command Center hooks */ }
}
