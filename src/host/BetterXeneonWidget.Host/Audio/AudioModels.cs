namespace BetterXeneonWidget.Host.Audio;

public sealed record AudioDeviceDto(
    string Id,
    string Name,
    bool IsDefault,
    int Volume,
    bool Muted);

public sealed record VolumeStateDto(int Level, bool Muted);

public sealed record AudioSessionDto(
    string Id,
    string DeviceId,
    string DeviceName,
    int ProcessId,
    string ProcessName,
    string DisplayName,
    string State,
    int Volume,
    bool Muted,
    /// <summary>
    /// Real-time peak amplitude from WASAPI's AudioMeterInformation
    /// (0..1). Non-zero means this session has produced sound in the
    /// last audio buffer. The widget uses this for two things:
    /// (1) sorting the Apps list with "currently active" first,
    /// (2) dimming session rows that haven't produced sound recently.
    /// </summary>
    float Peak);

public sealed record SetDefaultRequest(string Id);
public sealed record SetVolumeRequest(int Level);
public sealed record SetMuteRequest(bool Muted);

public sealed record SetDeviceVolumeRequest(string Id, int Level);
public sealed record SetDeviceMuteRequest(string Id, bool Muted);

public sealed record SetSessionVolumeRequest(string Id, int Level);
public sealed record SetSessionMuteRequest(string Id, bool Muted);
