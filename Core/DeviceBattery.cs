using System.Diagnostics;
using Windows.Devices.Enumeration;

namespace Piztki.Core;

/// <summary>
/// Battery level of Bluetooth headphones, mice, and controllers that report one to Windows (the
/// same number Windows shows in its own Bluetooth settings). Read fresh on each call.
/// </summary>
public static class DeviceBattery
{
    const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    const string ConnectedKey = "{83DA6326-97A6-4088-9453-A1923F573B29} 15";

    public static string LastError { get; private set; } = "";
    public static int LastScanned { get; private set; }

    /// <summary>Connected devices and their battery percent. Empty when none report one.</summary>
    public static async Task<List<(string name, int percent)>> ReadAsync()
    {
        var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        LastError = "";
        try
        {
            var devices = await DeviceInformation.FindAllAsync("", new[] { BatteryKey, ConnectedKey }, DeviceInformationKind.Device);
            LastScanned = devices.Count;
            foreach (var device in devices)
            {
                if (!device.Properties.TryGetValue(BatteryKey, out var raw) || raw == null) continue;
                if (device.Properties.TryGetValue(ConnectedKey, out var connected) && connected is false) continue;

                int percent = Convert.ToInt32(raw);
                if (percent is < 0 or > 100) continue;

                string name = Clean(device.Name);
                if (name.Length == 0) continue;
                // one headset shows up as several entries: keep one
                if (!found.TryGetValue(name, out int had) || percent < had) found[name] = percent;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            Debug.WriteLine(LastError);
        }
        return found.Select(p => (p.Key, p.Value)).OrderBy(p => p.Key).ToList();
    }

    static string Clean(string name)
    {
        foreach (var tail in new[] { " Hands-Free AG", " Hands-Free", " Avrcp Transport", " Stereo" })
            if (name.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) name = name[..^tail.Length];
        return name.Trim();
    }
}
