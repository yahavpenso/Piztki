using System.IO;

namespace Piztki.Core;

/// <summary>One thing the search box can open.</summary>
public sealed record SearchHit(string Name, string Path, bool IsApp);

/// <summary>
/// Finds apps (the Start menu's shortcuts) and files (Desktop, Documents, Downloads, a few folders
/// deep) by name. Everything stays on this PC; the lists are read once and refreshed now and then.
/// </summary>
public static class AppSearch
{
    static List<SearchHit> _apps = new();
    static List<SearchHit> _files = new();
    static DateTime _readAt = DateTime.MinValue;
    static Task? _reading;

    /// <summary>Starts reading the lists in the background if they are missing or stale.</summary>
    public static Task Warm()
    {
        if (_reading is { IsCompleted: false }) return _reading;
        if (DateTime.Now - _readAt < TimeSpan.FromMinutes(10)) return Task.CompletedTask;
        _reading = Task.Run(Read);
        return _reading;
    }

    static void Read()
    {
        var apps = new Dictionary<string, SearchHit>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                     Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                 })
        {
            foreach (var file in Walk(root, depth: 4, limit: 3000))
            {
                string ext = Path.GetExtension(file);
                if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".url", StringComparison.OrdinalIgnoreCase)) continue;
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                apps.TryAdd(name, new SearchHit(name, file, true));
            }
        }

        var files = new List<SearchHit>();
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                     Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                 })
        {
            foreach (var file in Walk(root, depth: 3, limit: 6000))
            {
                string ext = Path.GetExtension(file);
                if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ini", StringComparison.OrdinalIgnoreCase)) continue;
                files.Add(new SearchHit(Path.GetFileName(file), file, false));
            }
        }

        _apps = apps.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _files = files;
        _readAt = DateTime.Now;
    }

    /// <summary>Files under a folder, a limited number of levels deep, skipping what can't be read.</summary>
    static IEnumerable<string> Walk(string root, int depth, int limit)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) yield break;
        var todo = new Queue<(string dir, int level)>();
        todo.Enqueue((root, 0));
        int count = 0;

        while (todo.Count > 0 && count < limit)
        {
            var (dir, level) = todo.Dequeue();
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(dir);
                dirs = level < depth ? Directory.GetDirectories(dir) : Array.Empty<string>();
            }
            catch
            {
                continue; // no permission, or it vanished
            }

            foreach (var file in files)
            {
                if (++count > limit) yield break;
                yield return file;
            }
            foreach (var sub in dirs)
            {
                string name = Path.GetFileName(sub);
                if (name.StartsWith('.') || name is "node_modules" or "bin" or "obj" or "__pycache__") continue;
                todo.Enqueue((sub, level + 1));
            }
        }
    }

    /// <summary>The best matches for what was typed: apps first, names that start with it before names that contain it.</summary>
    public static List<SearchHit> Find(string text, int max)
    {
        text = text.Trim();
        var hits = new List<SearchHit>();
        if (text.Length < 2) return hits;

        static int Rank(string name, string text)
            => name.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 0
             : name.Contains(" " + text, StringComparison.OrdinalIgnoreCase) ? 1
             : name.Contains(text, StringComparison.OrdinalIgnoreCase) ? 2 : -1;

        foreach (var group in new[] { _apps, _files })
        {
            hits.AddRange(group
                .Select(h => (hit: h, rank: Rank(h.Name, text)))
                .Where(x => x.rank >= 0)
                .OrderBy(x => x.rank).ThenBy(x => x.hit.Name.Length)
                .Take(max)
                .Select(x => x.hit));
            if (hits.Count >= max) break;
        }
        return hits.Take(max).ToList();
    }

    public static int AppCount => _apps.Count;
    public static int FileCount => _files.Count;
}
