using System.Media;
using FrameForge.Diagnostics;

namespace FrameForge.UI;

static class ShutterSound
{
    // Keep the player and its embedded WAV stream alive during asynchronous playback.
    static readonly Lazy<SoundPlayer> Player = new(() =>
    {
        var stream = typeof(ShutterSound).Assembly.GetManifestResourceStream("FrameForge.UI.Assets.shutter.wav")
            ?? throw new InvalidOperationException("The shutter sound resource is missing.");
        var player = new SoundPlayer(stream);
        player.Load();
        return player;
    });

    public static void Play()
    {
        try { Player.Value.Play(); }
        catch (Exception ex)
        {
            // Audio feedback must not turn a saved screenshot into an error.
            Log.Error("Could not play the screenshot shutter sound.", ex);
        }
    }
}
