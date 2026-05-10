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
    bool Muted);

public sealed record SetDefaultRequest(string Id);
public sealed record SetVolumeRequest(int Level);
public sealed record SetMuteRequest(bool Muted);

public sealed record SetDeviceVolumeRequest(string Id, int Level);
public sealed record SetDeviceMuteRequest(string Id, bool Muted);

public sealed record SetSessionVolumeRequest(string Id, int Level);
public sealed record SetSessionMuteRequest(string Id, bool Muted);
