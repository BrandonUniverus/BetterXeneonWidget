using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace BetterXeneonWidget.Host.Audio;

public sealed class SessionService
{
    private readonly MMDeviceEnumerator _enumerator;

    public SessionService(MMDeviceEnumerator enumerator)
    {
        _enumerator = enumerator;
    }

    public IReadOnlyList<AudioSessionDto> ListSessions()
    {
        var output = new List<AudioSessionDto>();
        var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

        foreach (var device in devices)
        {
            try
            {
                var manager = device.AudioSessionManager;
                var sessions = manager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    if (TryToDto(session, device, out var dto)) output.Add(dto);
                }
            }
            catch
            {
                // Some devices reject session enumeration (busy or transient).
                // Skip rather than fail the whole listing.
            }
        }
        return output;
    }

    public bool SetSessionVolume(string id, int level)
    {
        var clamped = Math.Clamp(level, 0, 100);
        return WithSession(id, s =>
        {
            s.SimpleAudioVolume.Volume = clamped / 100f;
        });
    }

    public bool SetSessionMute(string id, bool muted)
    {
        return WithSession(id, s =>
        {
            s.SimpleAudioVolume.Mute = muted;
        });
    }

    /// <summary>
    /// Resolves the session's process executable so its icon can be extracted.
    /// Returned in "path,0" form so the existing IconExtractor (which already
    /// expects "dll,-resourceID" or "exe,index" format) can read the first icon.
    /// </summary>
    public string? GetSessionIconPath(string id)
    {
        var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        foreach (var device in devices)
        {
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    if (!string.Equals(session.GetSessionInstanceIdentifier, id, StringComparison.Ordinal)) continue;

                    var pid = (int)session.GetProcessID;
                    if (pid == 0) return null;

                    try
                    {
                        using var process = Process.GetProcessById(pid);
                        var fileName = process.MainModule?.FileName;
                        if (string.IsNullOrEmpty(fileName)) return null;
                        return fileName + ",0"; // first icon resource in the exe
                    }
                    catch
                    {
                        return null;
                    }
                }
            }
            catch
            {
                /* try next device */
            }
        }
        return null;
    }

    private bool WithSession(string id, Action<AudioSessionControl> action)
    {
        var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        foreach (var device in devices)
        {
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    if (string.Equals(session.GetSessionInstanceIdentifier, id, StringComparison.Ordinal))
                    {
                        action(session);
                        return true;
                    }
                }
            }
            catch
            {
                /* ignore — try next device */
            }
        }
        return false;
    }

    private static bool TryToDto(AudioSessionControl session, MMDevice device, out AudioSessionDto dto)
    {
        dto = default!;
        try
        {
            // Skip system sounds and hidden sessions; they clutter the list and have no useful name.
            var pid = (int)session.GetProcessID;
            if (pid == 0) return false;

            string processName;
            try
            {
                using var process = Process.GetProcessById(pid);
                processName = string.IsNullOrWhiteSpace(process.ProcessName)
                    ? $"PID {pid}"
                    : process.ProcessName;
            }
            catch
            {
                processName = $"PID {pid}";
            }

            var displayName = string.IsNullOrWhiteSpace(session.DisplayName) ? processName : session.DisplayName;

            dto = new AudioSessionDto(
                Id: session.GetSessionInstanceIdentifier ?? $"{device.ID}|{pid}",
                DeviceId: device.ID,
                DeviceName: device.FriendlyName,
                ProcessId: pid,
                ProcessName: processName,
                DisplayName: displayName,
                State: session.State.ToString(),
                Volume: (int)Math.Round(session.SimpleAudioVolume.Volume * 100f),
                Muted: session.SimpleAudioVolume.Mute);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
