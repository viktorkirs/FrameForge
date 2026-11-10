using NAudio.CoreAudioApi;

namespace FrameForge.Capture;

public sealed record AudioDeviceInfo(string Id, string Name);

public static class AudioDeviceCatalog
{
    public static IReadOnlyList<AudioDeviceInfo> GetOutputs() => Get(DataFlow.Render);
    public static IReadOnlyList<AudioDeviceInfo> GetMicrophones() => Get(DataFlow.Capture);

    static IReadOnlyList<AudioDeviceInfo> Get(DataFlow flow)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var result = new List<AudioDeviceInfo>();
            foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
                using (device) result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName));
            return result;
        }
        catch { return []; }
    }
}
