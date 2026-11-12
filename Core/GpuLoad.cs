using System.Runtime.InteropServices;

namespace Piztki.Core;

/// <summary>
/// How busy the graphics card is, 0..1, from the same Windows counters Task Manager uses (so it
/// works for any brand of card). Needs two readings, so the first call returns null.
/// </summary>
public sealed class GpuLoad
{
    const uint PDH_FMT_DOUBLE = 0x00000200;
    const uint PDH_MORE_DATA = 0x800007D2;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQueryW(string? source, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);

    // one entry of the array PDH fills in: a name pointer, a status, and the value
    [StructLayout(LayoutKind.Sequential)]
    struct Item
    {
        public IntPtr Name;
        public uint Status;
        public double Value;
    }

    IntPtr _query, _counter;
    bool _primed, _broken;

    public string LastError { get; private set; } = "";

    public double? Read()
    {
        if (_broken) return null;
        try
        {
            if (_query == IntPtr.Zero)
            {
                uint opened = PdhOpenQueryW(null, IntPtr.Zero, out _query);
                // each app has an entry per engine; "3D" is the one games draw with
                uint added = opened == 0 ? PdhAddEnglishCounterW(_query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _counter) : opened;
                if (added != 0) return Fail($"counter not available (0x{added:X8})");
            }

            uint collected = PdhCollectQueryData(_query);
            if (collected != 0) return Fail($"collect failed (0x{collected:X8})");
            if (!_primed)
            {
                _primed = true; // a rate needs two samples
                return null;
            }

            uint size = 0;
            uint status = PdhGetFormattedCounterArrayW(_counter, PDH_FMT_DOUBLE, ref size, out uint count, IntPtr.Zero);
            if (status != PDH_MORE_DATA || size == 0) return null; // nothing is drawing right now

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                status = PdhGetFormattedCounterArrayW(_counter, PDH_FMT_DOUBLE, ref size, out count, buffer);
                if (status != 0) return null;

                double total = 0;
                int step = Marshal.SizeOf<Item>();
                for (int i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<Item>(buffer + i * step);
                    if (item.Status == 0 && item.Value > 0) total += item.Value;
                }
                return Math.Clamp(total / 100.0, 0, 1);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            return Fail(ex.GetType().Name + ": " + ex.Message);
        }
    }

    double? Fail(string why)
    {
        // counters missing on this PC: stop asking
        LastError = why;
        _broken = true;
        Reset();
        return null;
    }

    /// <summary>Lets go of the counter; the next Read starts fresh.</summary>
    public void Reset()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = _counter = IntPtr.Zero;
        _primed = false;
    }
}
