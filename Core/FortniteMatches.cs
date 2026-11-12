using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Piztki.Core;

/// <summary>How one Fortnite match went, as the game itself wrote it into the match's replay file.</summary>
public sealed record FortniteMatch(
    string File, DateTime Ended, int Place, int Players, int Eliminations, int Assists,
    int Damage, double Accuracy, int Revives, int DamageTaken, double Kilometres, double Minutes)
{
    /// <summary>The game this match was played in.</summary>
    public string Game => "Fortnite";

    public bool Won => Place == 1;

    /// <summary>"#5 of 80" or "Victory Royale".</summary>
    public string Headline => Won ? "Victory Royale" : Players >= Place ? $"#{Place} of {Players}" : $"#{Place}";

    /// <summary>"2 elims · 230 dmg · 31% hit".</summary>
    public string Detail =>
        $"{Eliminations} {(Eliminations == 1 ? "elim" : "elims")} · {Damage} dmg · {Math.Round(Accuracy * 100)}% hit";

    /// <summary>Everything on one line, for the strip shown over the lobby.</summary>
    public string Line => $"{Game}    {Headline}    {Detail}    {Math.Round(Minutes)} min";
}

/// <summary>
/// Reads match results out of Fortnite's own replay files (Saved\Demos). Only files on this PC are
/// read: nothing touches the running game, its memory, or the network.
/// </summary>
public static class FortniteReplay
{
    const uint Magic = 0x1CA2E27F;
    const uint EventChunk = 3;

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved", "Demos");

    /// <summary>The match in this replay, or null when the file holds no final stats (left early, creative, still being written).</summary>
    public static FortniteMatch? Read(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new BinaryReader(file);
            if (file.Length < 64 || r.ReadUInt32() != Magic) return null;
            uint version = r.ReadUInt32();
            if (version >= 7)
            {
                int custom = r.ReadInt32();
                if (custom < 0 || custom > 4096) return null;
                file.Seek(custom * 20L, SeekOrigin.Current);
            }
            int lengthMs = r.ReadInt32();
            file.Seek(8, SeekOrigin.Current);          // network version, changelist
            Text(r);                                   // the replay's display name
            file.Seek(4, SeekOrigin.Current);          // "is live"
            if (version >= 3) file.Seek(8, SeekOrigin.Current); // recorded at
            if (version >= 2) file.Seek(4, SeekOrigin.Current); // compressed
            byte[] key = Array.Empty<byte>();
            bool encrypted = false;
            if (version >= 6)
            {
                encrypted = r.ReadUInt32() != 0;
                int keyLength = r.ReadInt32();
                if (keyLength < 0 || keyLength > 64) return null;
                key = r.ReadBytes(keyLength);
            }

            byte[]? stats = null, team = null;
            while (file.Position + 8 <= file.Length)
            {
                uint type = r.ReadUInt32();
                int size = r.ReadInt32();
                long next = file.Position + size;
                if (size < 0 || next > file.Length) break; // the game is still writing this piece
                if (type == EventChunk)
                {
                    Text(r);                 // id
                    Text(r);                 // group
                    string what = Text(r);
                    file.Seek(8, SeekOrigin.Current); // start and end time
                    int dataSize = r.ReadInt32();
                    if (dataSize is > 0 and <= 4096 && what is "AthenaMatchStats" or "AthenaMatchTeamStats")
                    {
                        var data = Open(r.ReadBytes(dataSize), key, encrypted);
                        if (what == "AthenaMatchStats") stats = data;
                        else team = data;
                    }
                }
                file.Seek(next, SeekOrigin.Begin);
            }
            if (stats == null || team == null || stats.Length < 48 || team.Length < 12) return null;

            uint U(byte[] from, int at) => BitConverter.ToUInt32(from, at);
            int weapon = (int)U(stats, 16), other = (int)U(stats, 20);
            return new FortniteMatch(
                File: Path.GetFileName(path),
                Ended: System.IO.File.GetLastWriteTime(path),
                Place: (int)U(team, 4),
                Players: (int)U(team, 8),
                Eliminations: (int)U(stats, 8),
                Assists: (int)U(stats, 12),
                Damage: weapon + other,
                Accuracy: Math.Clamp(BitConverter.ToSingle(stats, 4), 0, 1),
                Revives: (int)U(stats, 24),
                DamageTaken: (int)U(stats, 28),
                Kilometres: U(stats, 44) / 100000.0,
                Minutes: lengthMs / 60000.0);
        }
        catch
        {
            return null; // a file the game still holds, or a format this reader doesn't know
        }
    }

    static byte[] Open(byte[] data, byte[] key, bool encrypted)
    {
        if (!encrypted || key.Length != 32 || data.Length % 16 != 0) return data;
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptEcb(data, PaddingMode.None);
    }

    /// <summary>An Unreal string: a length, then that many bytes (negative length = two bytes a letter).</summary>
    static string Text(BinaryReader r)
    {
        int length = r.ReadInt32();
        if (length == 0) return "";
        if (length is > 8192 or < -8192) throw new InvalidDataException();
        return length < 0
            ? Encoding.Unicode.GetString(r.ReadBytes(-length * 2)).TrimEnd('\0', ' ')
            : Encoding.Latin1.GetString(r.ReadBytes(length)).TrimEnd('\0', ' ');
    }
}

/// <summary>
/// Notices when a match has just ended: Fortnite stops writing the replay the moment the player is
/// back in the lobby. Poll it every few seconds.
/// </summary>
public sealed class FortniteWatch
{
    string _watching = "";
    long _size = -1;
    int _stillFor;
    bool _reading;

    /// <summary>The newest replay already dealt with, so nothing is announced twice.</summary>
    public string Done { get; set; } = "";

    /// <summary>A match was read. "fresh" = it ended just now, rather than being found from an earlier day.</summary>
    public event Action<FortniteMatch, bool>? Finished;

    /// <summary>A replay turned out to hold no result (left early, creative mode): just remember it.</summary>
    public event Action<string>? Skipped;

    public void Poll()
    {
        if (_reading) return;
        FileInfo? newest;
        try
        {
            var folder = new DirectoryInfo(FortniteReplay.Folder);
            if (!folder.Exists) return;
            newest = folder.EnumerateFiles("*.replay").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        }
        catch
        {
            return;
        }
        if (newest == null || newest.Name == Done) return;

        // still growing = still playing (or watching the end of the match)
        if (newest.Name != _watching || newest.Length != _size)
        {
            _watching = newest.Name;
            _size = newest.Length;
            _stillFor = 0;
            return;
        }
        if (++_stillFor < 2) return;

        _reading = true;
        string path = newest.FullName, name = newest.Name;
        bool fresh = (DateTime.UtcNow - newest.LastWriteTimeUtc).TotalMinutes < 3;
        var ui = System.Windows.Application.Current.Dispatcher;
        Task.Run(() =>
        {
            var match = FortniteReplay.Read(path);
            ui.BeginInvoke(() =>
            {
                _reading = false;
                Done = name;
                if (match != null) Finished?.Invoke(match, fresh);
                else Skipped?.Invoke(name);
            });
        });
    }
}
