using System.IO;
using Microsoft.Win32;

namespace Piztki.Core;

/// <summary>
/// Tells whether some app is using the microphone or the camera right now. Windows keeps a small
/// record per app in the registry (the same one behind Settings > Privacy); an app that is still
/// using the device has a start time but no stop time. Nothing is listened to or looked at.
/// </summary>
public static class PrivacyWatch
{
    const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

    /// <summary>The app using the microphone, or null when nobody is.</summary>
    public static string? MicrophoneUser() => UserOf("microphone");

    /// <summary>The app using the camera, or null when nobody is.</summary>
    public static string? CameraUser() => UserOf("webcam");

    static string? UserOf(string capability)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Store + capability);
            if (root == null) return null;

            foreach (var name in root.GetSubKeyNames())
            {
                if (name == "NonPackaged")
                {
                    using var desktop = root.OpenSubKey(name);
                    if (desktop == null) continue;
                    foreach (var app in desktop.GetSubKeyNames())
                    {
                        using var key = desktop.OpenSubKey(app);
                        if (InUse(key)) return DesktopName(app);
                    }
                }
                else
                {
                    using var key = root.OpenSubKey(name);
                    if (InUse(key)) return PackageName(name);
                }
            }
        }
        catch
        {
            // the record is unreadable: treat it as "not in use" rather than raise a false alarm
        }
        return null;
    }

    static bool InUse(RegistryKey? key)
        => key?.GetValue("LastUsedTimeStart") is long start && start > 0
           && key.GetValue("LastUsedTimeStop") is long stop && stop == 0;

    // desktop apps are stored by path, with # in place of the backslashes
    static string DesktopName(string key)
    {
        string name = Path.GetFileNameWithoutExtension(key.Replace('#', '\\'));
        return name.Length == 0 ? "An app" : char.ToUpperInvariant(name[0]) + name[1..];
    }

    // store apps are stored as Publisher.AppName_hash
    static string PackageName(string key)
    {
        int underscore = key.IndexOf('_');
        string family = underscore > 0 ? key[..underscore] : key;
        int dot = family.LastIndexOf('.');
        string name = dot >= 0 && dot < family.Length - 1 ? family[(dot + 1)..] : family;
        return name.Length == 0 ? "An app" : name;
    }
}
