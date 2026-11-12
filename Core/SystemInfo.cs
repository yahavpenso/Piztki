using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Piztki.Core;

/// <summary>Reads the master volume of the default speakers through the Windows Core Audio API.</summary>
public sealed class VolumeReader
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    // Only the two getters are called; the other entries exist to keep the method slots in order.
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);
        int UnregisterControlChangeNotify(IntPtr notify);
        int GetChannelCount(out int count);
        int SetMasterVolumeLevel(float levelDb, ref Guid context);
        int SetMasterVolumeLevelScalar(float level, ref Guid context);
        int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(int channel, float levelDb, ref Guid context);
        int SetChannelVolumeLevelScalar(int channel, float level, ref Guid context);
        int GetChannelVolumeLevel(int channel, out float levelDb);
        int GetChannelVolumeLevelScalar(int channel, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    // The speakers' built-in level meter: how loud the sound coming out is at this instant.
    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }

    IAudioEndpointVolume? _volume;
    IAudioMeterInformation? _meter;
    DateTime _acquired = DateTime.MinValue;

    void Acquire()
    {
        Release();
        _acquired = DateTime.Now;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out var device) != 0) return;

            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var obj) == 0)
                _volume = (IAudioEndpointVolume)obj;

            iid = typeof(IAudioMeterInformation).GUID;
            if (device.Activate(ref iid, 23, IntPtr.Zero, out var meter) == 0)
                _meter = (IAudioMeterInformation)meter;

            Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
        catch
        {
            _volume = null; // no audio device right now
            _meter = null;
        }
    }

    void Release()
    {
        try { if (_volume != null) Marshal.ReleaseComObject(_volume); } catch { }
        try { if (_meter != null) Marshal.ReleaseComObject(_meter); } catch { }
        _volume = null;
        _meter = null;
    }

    // re-resolve now and then so we follow the user switching to headphones or another output
    void EnsureFresh()
    {
        if ((_volume == null && _meter == null) || DateTime.Now - _acquired > TimeSpan.FromSeconds(8)) Acquire();
    }

    /// <summary>
    /// How loud the sound playing right now is, 0..1. This only reads the output device's own
    /// level meter; no audio is captured or recorded. False when there is no audio device.
    /// </summary>
    public bool TryReadPeak(out double peak)
    {
        peak = 0;
        EnsureFresh();
        if (_meter == null) return false;
        try
        {
            if (_meter.GetPeakValue(out float p) != 0) return false;
            peak = p;
            return true;
        }
        catch
        {
            Release();
            return false;
        }
    }

    /// <summary>Volume 0..1 and mute state. False when there is no audio device.</summary>
    public bool TryRead(out double level, out bool muted)
    {
        level = 0;
        muted = false;

        EnsureFresh();
        if (_volume == null) return false;

        try
        {
            if (_volume.GetMasterVolumeLevelScalar(out float l) != 0 || _volume.GetMute(out muted) != 0)
            {
                Release();
                return false;
            }
            level = l;
            return true;
        }
        catch
        {
            Release();
            return false;
        }
    }
}

/// <summary>CPU, memory, and network numbers for the System tab.</summary>
public sealed class SystemStats
{
    [StructLayout(LayoutKind.Sequential)]
    struct FILETIME { public uint Low, High; public readonly ulong Value => ((ulong)High << 32) | Low; }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    ulong _idle, _total;
    long _received, _sent;
    DateTime _netAt = DateTime.MinValue;

    public double Cpu { get; private set; }            // 0..1
    public double Memory { get; private set; }         // 0..1
    public double MemoryUsedGb { get; private set; }
    public double MemoryTotalGb { get; private set; }
    public double DownBytesPerSec { get; private set; }
    public double UpBytesPerSec { get; private set; }

    public void Update()
    {
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            ulong total = kernel.Value + user.Value; // kernel time already includes idle time
            if (_total != 0 && total > _total)
                Cpu = Math.Clamp(1.0 - (double)(idle.Value - _idle) / (total - _total), 0, 1);
            _idle = idle.Value;
            _total = total;
        }

        var mem = new MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem) && mem.TotalPhys > 0)
        {
            const double Gb = 1024.0 * 1024 * 1024;
            MemoryTotalGb = mem.TotalPhys / Gb;
            MemoryUsedGb = (mem.TotalPhys - mem.AvailPhys) / Gb;
            Memory = MemoryUsedGb / MemoryTotalGb;
        }

        try
        {
            long received = 0, sent = 0;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var s = nic.GetIPStatistics();
                received += s.BytesReceived;
                sent += s.BytesSent;
            }

            var now = DateTime.Now;
            double seconds = (now - _netAt).TotalSeconds;
            if (_netAt != DateTime.MinValue && seconds is > 0.2 and < 30)
            {
                DownBytesPerSec = Math.Max(0, (received - _received) / seconds);
                UpBytesPerSec = Math.Max(0, (sent - _sent) / seconds);
            }
            _received = received;
            _sent = sent;
            _netAt = now;
        }
        catch
        {
            // network counters are a nice-to-have
        }
    }

    public static string Speed(double bytesPerSec) => bytesPerSec switch
    {
        >= 1024 * 1024 => $"{bytesPerSec / (1024 * 1024):0.0} MB/s",
        >= 1024 => $"{bytesPerSec / 1024:0} KB/s",
        _ => "0 KB/s",
    };
}

/// <summary>Battery state. HasBattery is false on desktop PCs.</summary>
public static class Battery
{
    public static bool HasBattery =>
        !System.Windows.Forms.SystemInformation.PowerStatus.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)
        && System.Windows.Forms.SystemInformation.PowerStatus.BatteryLifePercent <= 1f;

    public static int Percent => (int)Math.Round(System.Windows.Forms.SystemInformation.PowerStatus.BatteryLifePercent * 100);

    public static bool PluggedIn =>
        System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
}
