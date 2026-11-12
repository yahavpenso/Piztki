using System.IO;
using System.Runtime.InteropServices;

namespace Piztki.Core;

/// <summary>
/// Notices files being downloaded by a browser. Browsers write a download into the Downloads
/// folder under a temporary name (.crdownload, .part, ...) and rename it when it is complete, so
/// watching those files gives the size so far and the speed. The total size is not knowable from
/// the outside, which is why there is no percentage.
/// </summary>
public sealed class DownloadWatch : IDisposable
{
    static readonly string[] TempExtensions = { ".crdownload", ".part", ".partial", ".download", ".opdownload" };

    sealed class Active
    {
        public long Size;
        public DateTime SeenAt;
        public double BytesPerSec;
    }

    readonly Dictionary<string, Active> _active = new(StringComparer.OrdinalIgnoreCase);
    FileSystemWatcher? _watcher;
    volatile bool _dirty = true;

    public string Folder { get; private set; } = "";
    public bool Running => _watcher != null;

    /// <summary>How many downloads are in progress.</summary>
    public int Count => _active.Count;
    /// <summary>Bytes on disk so far, all running downloads together.</summary>
    public long Bytes { get; private set; }
    public double BytesPerSec { get; private set; }

    /// <summary>The last file that finished, and when.</summary>
    public string? LastFinished { get; private set; }
    public DateTime LastFinishedAt { get; private set; }

    public event Action? Started;
    public event Action<string>? Finished;

    public void Start(string? folder = null)
    {
        if (_watcher != null) return;
        try
        {
            Folder = folder ?? DownloadsFolder();
            if (!Directory.Exists(Folder)) return;
            _watcher = new FileSystemWatcher(Folder)
            {
                NotifyFilter = NotifyFilters.FileName,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            _watcher.Created += (_, _) => _dirty = true;
            _watcher.Renamed += (_, _) => _dirty = true;
            _watcher.Deleted += (_, _) => _dirty = true;
            _dirty = true;
        }
        catch
        {
            _watcher = null;
        }
    }

    public void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
        _active.Clear();
        Bytes = 0;
        BytesPerSec = 0;
    }

    /// <summary>Call about once a second. Cheap when nothing is downloading: it only looks at the folder after a change.</summary>
    public void Poll()
    {
        if (_watcher == null || (!_dirty && _active.Count == 0)) return;
        _dirty = false;

        var now = DateTime.Now;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool started = false;

        try
        {
            foreach (var path in Directory.EnumerateFiles(Folder))
            {
                if (!IsTemp(path)) continue;
                long size;
                try { size = new FileInfo(path).Length; }
                catch { continue; }

                seen.Add(path);
                if (_active.TryGetValue(path, out var item))
                {
                    double seconds = Math.Max(0.2, (now - item.SeenAt).TotalSeconds);
                    double speed = Math.Max(0, size - item.Size) / seconds;
                    item.BytesPerSec = item.BytesPerSec <= 0 ? speed : item.BytesPerSec * 0.6 + speed * 0.4; // steadier number
                    item.Size = size;
                    item.SeenAt = now;
                }
                else
                {
                    _active[path] = new Active { Size = size, SeenAt = now };
                    started = true;
                }
            }
        }
        catch
        {
            return; // the folder is busy; try again on the next poll
        }

        List<string>? done = null;
        foreach (var path in _active.Keys)
            if (!seen.Contains(path)) (done ??= new()).Add(path);

        Bytes = 0;
        BytesPerSec = 0;
        if (done != null)
        {
            foreach (var path in done)
            {
                var item = _active[path];
                _active.Remove(path);
                // a download that was cancelled leaves nothing behind; only a real file counts as finished
                if (FindFinished(path, item.Size) is { } final)
                {
                    LastFinished = final;
                    LastFinishedAt = now;
                    Finished?.Invoke(final);
                }
            }
        }
        foreach (var item in _active.Values)
        {
            Bytes += item.Size;
            BytesPerSec += item.BytesPerSec;
        }

        if (started) Started?.Invoke();
    }

    static bool IsTemp(string path)
    {
        string ext = Path.GetExtension(path);
        foreach (var temp in TempExtensions)
            if (ext.Equals(temp, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Works out which finished file a temporary download file turned into.</summary>
    string? FindFinished(string tempPath, long size)
    {
        try
        {
            // Firefox style: "movie.mp4.part" becomes "movie.mp4"
            string stripped = Path.Combine(Folder, Path.GetFileNameWithoutExtension(tempPath));
            if (File.Exists(stripped) && new FileInfo(stripped).Length > 0) return stripped;

            // Chrome and Edge style: "Unconfirmed 123.crdownload" is renamed to the real name,
            // so look for the file that just appeared
            var recent = new DirectoryInfo(Folder).EnumerateFiles()
                .Where(f => !IsTemp(f.Name) && (DateTime.Now - f.LastWriteTime).TotalSeconds < 15)
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault(f => size <= 0 || f.Length >= size * 0.9);
            return recent?.FullName;
        }
        catch
        {
            return null;
        }
    }

    public static string Size(long bytes)
        => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.00} GB"
         : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.0} MB"
         : $"{Math.Max(1, bytes >> 10)} KB";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    static string DownloadsFolder()
    {
        try
        {
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var pointer) == 0)
            {
                string? path = Marshal.PtrToStringUni(pointer);
                Marshal.FreeCoTaskMem(pointer);
                if (!string.IsNullOrEmpty(path)) return path;
            }
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    public void Dispose() => Stop();
}
