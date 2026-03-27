using GalaxyBudsClient.Platform.Interfaces;
using MediaPlayer;

namespace GalaxyBudsClient.Platform.iOS;

public class MediaKeyRemote : IMediaKeyRemote
{
    public void Play() => MPRemoteCommandCenter.SharedCenter.PlayCommand.Enabled = true;
    public void Pause() => MPRemoteCommandCenter.SharedCenter.PauseCommand.Enabled = true;
    public void PlayPause() => MPRemoteCommandCenter.SharedCenter.TogglePlayPauseCommand.Enabled = true;
    public void Stop() => MPRemoteCommandCenter.SharedCenter.StopCommand.Enabled = true;
    public void Next() => MPRemoteCommandCenter.SharedCenter.NextTrackCommand.Enabled = true;
    public void Previous() => MPRemoteCommandCenter.SharedCenter.PreviousTrackCommand.Enabled = true;
}
