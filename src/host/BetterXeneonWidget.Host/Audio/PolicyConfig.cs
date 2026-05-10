using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace BetterXeneonWidget.Host.Audio;

// Minimal IPolicyConfig wrapper for SetDefaultEndpoint. The interface is
// undocumented but stable from Vista through Windows 11 — the same pattern
// EarTrumpet, AudioDeviceCmdlets, and SoundVolumeView use.

[ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
internal class PolicyConfigClass { }

[Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr ppFormat);
    [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, bool bDefault, IntPtr ppFormat);
    [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr pEndpointFormat, IntPtr pMixFormat);
    [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, bool bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
    [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr pmftPeriod);
    [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr pMode);
    [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr pMode);
    [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, bool bFxStore, IntPtr key, IntPtr pv);
    [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, bool bFxStore, IntPtr key, IntPtr pv);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
    [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, bool visible);
}

internal sealed class PolicyConfigClient
{
    public void SetDefaultEndpoint(string deviceId, Role role)
    {
        var instance = (IPolicyConfig)new PolicyConfigClass();
        try
        {
            var hr = instance.SetDefaultEndpoint(deviceId, role);
            if (hr != 0)
                throw Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException(
                    $"IPolicyConfig.SetDefaultEndpoint failed (HRESULT 0x{hr:X8})");
        }
        finally
        {
            Marshal.FinalReleaseComObject(instance);
        }
    }
}
