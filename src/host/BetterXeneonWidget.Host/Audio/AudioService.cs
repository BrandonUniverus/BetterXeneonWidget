using NAudio.CoreAudioApi;

namespace BetterXeneonWidget.Host.Audio;

public sealed class AudioService
{
    private readonly MMDeviceEnumerator _enumerator;
    private readonly PolicyConfigClient _policyConfig = new();

    public AudioService(MMDeviceEnumerator enumerator)
    {
        _enumerator = enumerator;
    }

    public IReadOnlyList<AudioDeviceDto> ListPlaybackDevices()
    {
        var defaultId = SafeDefaultId();
        var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

        var list = new List<AudioDeviceDto>(devices.Count);
        foreach (var d in devices)
        {
            list.Add(new AudioDeviceDto(
                Id: d.ID,
                Name: d.FriendlyName,
                IsDefault: string.Equals(d.ID, defaultId, StringComparison.Ordinal),
                Volume: ScalarToPercent(d.AudioEndpointVolume.MasterVolumeLevelScalar),
                Muted: d.AudioEndpointVolume.Mute));
        }
        return list;
    }

    public void SetDefault(string id)
    {
        _policyConfig.SetDefaultEndpoint(id, Role.Console);
        _policyConfig.SetDefaultEndpoint(id, Role.Multimedia);
        _policyConfig.SetDefaultEndpoint(id, Role.Communications);
    }

    public VolumeStateDto GetDefaultVolume()
    {
        using var d = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return new VolumeStateDto(
            ScalarToPercent(d.AudioEndpointVolume.MasterVolumeLevelScalar),
            d.AudioEndpointVolume.Mute);
    }

    public void SetDefaultVolume(int level)
    {
        var clamped = Math.Clamp(level, 0, 100);
        using var d = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        d.AudioEndpointVolume.MasterVolumeLevelScalar = clamped / 100f;
    }

    public void SetDefaultMute(bool muted)
    {
        using var d = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        d.AudioEndpointVolume.Mute = muted;
    }

    public void SetDeviceVolume(string id, int level)
    {
        var clamped = Math.Clamp(level, 0, 100);
        using var d = _enumerator.GetDevice(id);
        d.AudioEndpointVolume.MasterVolumeLevelScalar = clamped / 100f;
    }

    public void SetDeviceMute(string id, bool muted)
    {
        using var d = _enumerator.GetDevice(id);
        d.AudioEndpointVolume.Mute = muted;
    }

    /// <summary>
    /// Returns the device's IconPath ("dll,-resourceID") for use with IconExtractor.
    /// Returns null when the device has no icon or doesn't exist.
    /// </summary>
    public string? GetDeviceIconPath(string id)
    {
        try
        {
            using var d = _enumerator.GetDevice(id);
            return string.IsNullOrEmpty(d?.IconPath) ? null : d.IconPath;
        }
        catch
        {
            return null;
        }
    }

    private string? SafeDefaultId()
    {
        try
        {
            using var d = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return d.ID;
        }
        catch
        {
            return null;
        }
    }

    private static int ScalarToPercent(float scalar) =>
        (int)Math.Round(Math.Clamp(scalar, 0f, 1f) * 100f);
}
