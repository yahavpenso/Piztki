using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Piztki.Core;

/// <summary>One app that is making (or could make) sound, as the Sound tab shows it.</summary>
public sealed class MixerApp
{
    public int ProcessId;
    public string Name = "";
    public string? ExePath;
    public double Volume;
    public bool Muted;
    internal readonly List<SimpleAudioVolume> Controls = new();

    public void SetVolume(double level)
    {
        Volume = Math.Clamp(level, 0, 1);
        foreach (var control in Controls)
        {
            try
            {
                control.Volume = (float)Volume;
                if (Volume > 0 && control.Mute) control.Mute = false;
            }
            catch { /* the app closed while its row was still on screen */ }
        }
        if (Volume > 0) Muted = false;
    }

    public void SetMuted(bool muted)
    {
        Muted = muted;
        foreach (var control in Controls)
        {
            try { control.Mute = muted; } catch { }
        }
    }
}

/// <summary>
/// Per-app volume (the same thing Windows' own volume mixer changes) and the microphone's mute
/// switch. Everything is read fresh on each call; nothing is kept open between calls.
/// </summary>
public static class Mixer
{
    /// <summary>Why the last read came back empty, for diagnostics.</summary>
    public static string LastError { get; private set; } = "";

    public static List<MixerApp> ReadApps()
    {
        LastError = "";
        var apps = new List<MixerApp>();
        try
        {
            using var devices = new MMDeviceEnumerator();
            if (!devices.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return apps;
            var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var manager = device.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;

            var byProcess = new Dictionary<int, MixerApp>();
            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session.State == AudioSessionState.AudioSessionStateExpired) continue;

                bool system = session.IsSystemSoundsSession;
                int pid = system ? 0 : (int)session.GetProcessID;
                if (pid == Environment.ProcessId) continue;

                if (!byProcess.TryGetValue(pid, out var app))
                {
                    app = new MixerApp { ProcessId = pid };
                    if (system) app.Name = "System sounds";
                    else if (!Describe(pid, app)) continue; // the process is already gone
                    app.Volume = session.SimpleAudioVolume.Volume;
                    app.Muted = session.SimpleAudioVolume.Mute;
                    byProcess[pid] = app;
                    apps.Add(app);
                }
                app.Controls.Add(session.SimpleAudioVolume);
            }
        }
        catch (Exception ex)
        {
            // no sound device, or the audio service is restarting: an empty list is the honest answer
            LastError = ex.GetType().Name + ": " + ex.Message;
        }

        // apps first by name, system sounds last
        apps.Sort((a, b) => a.ProcessId == 0 ? 1 : b.ProcessId == 0 ? -1 : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return apps;
    }

    static bool Describe(int pid, MixerApp app)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            app.Name = process.ProcessName;
            try
            {
                var module = process.MainModule;
                app.ExePath = module?.FileName;
                string? description = module?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) app.Name = description.Trim();
            }
            catch
            {
                // a protected process: the plain process name will do
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Reads the default microphone's mute switch. False when there is no microphone.</summary>
    public static bool TryGetMicMuted(out bool muted)
    {
        muted = false;
        try
        {
            using var devices = new MMDeviceEnumerator();
            if (!devices.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)) return false;
            var mic = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            muted = mic.AudioEndpointVolume.Mute;
            return true;
        }
        catch (Exception ex)
        {
            LastError = "mic " + ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    public static bool SetMicMuted(bool muted)
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            if (!devices.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)) return false;
            var mic = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            mic.AudioEndpointVolume.Mute = muted;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
