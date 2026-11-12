using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace Piztki.Core;

/// <summary>Reads "now playing" from Windows (Spotify, browsers, etc.) and sends play/pause/skip/seek.</summary>
public sealed class MediaService
{
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;
    int _refreshId;

    string _artKey = "\0";
    TimeSpan _position;
    DateTimeOffset _positionAt = DateTimeOffset.Now;

    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public bool IsPlaying { get; private set; }
    public bool HasSession { get; private set; }
    public ImageSource? Art { get; private set; }
    /// <summary>The cover's main color, or null when there is no cover or it has no real color.</summary>
    public Color? ArtColor { get; private set; }
    /// <summary>Up to three of the cover's strongest colors, for the clouds behind the lyrics view.</summary>
    public List<Color> ArtColors { get; private set; } = new();
    public TimeSpan Duration { get; private set; }

    /// <summary>Where the song is right now, estimated between the player's own updates.</summary>
    public TimeSpan Position
    {
        get
        {
            var p = _position;
            if (IsPlaying) p += DateTimeOffset.Now - _positionAt;
            if (p < TimeSpan.Zero) p = TimeSpan.Zero;
            if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
            return p;
        }
    }

    public event Action? Changed;

    public async Task InitAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => _ui.BeginInvoke(HookSession);
            HookSession();
        }
        catch
        {
            // Media info is optional; the notch works without it.
        }
    }

    void HookSession()
    {
        if (_manager == null) return;

        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnSessionEvent;
            _session.PlaybackInfoChanged -= OnSessionEvent;
            _session.TimelinePropertiesChanged -= OnSessionEvent;
        }

        try { _session = _manager.GetCurrentSession(); }
        catch { _session = null; }

        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnSessionEvent;
            _session.PlaybackInfoChanged += OnSessionEvent;
            _session.TimelinePropertiesChanged += OnSessionEvent;
        }

        _ = RefreshAsync();
    }

    void OnSessionEvent(GlobalSystemMediaTransportControlsSession sender, object args)
        => _ui.BeginInvoke(() => { _ = RefreshAsync(); });

    async Task RefreshAsync()
    {
        int id = ++_refreshId;
        var session = _session;

        string title = "", artist = "";
        bool playing = false;
        ImageSource? art = Art;
        string artKey = _artKey;
        TimeSpan position = TimeSpan.Zero, duration = TimeSpan.Zero;
        var positionAt = DateTimeOffset.Now;

        if (session != null)
        {
            try
            {
                var info = session.GetPlaybackInfo();
                playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                var timeline = session.GetTimelineProperties();
                if (timeline != null)
                {
                    position = timeline.Position - timeline.StartTime;
                    duration = timeline.EndTime - timeline.StartTime;
                    // players that never report a timestamp leave it at the year 1601
                    if (timeline.LastUpdatedTime.Year > 2000) positionAt = timeline.LastUpdatedTime;
                }

                var props = await session.TryGetMediaPropertiesAsync();
                title = props?.Title ?? "";
                artist = props?.Artist ?? "";

                // the cover only changes with the song, so don't decode it again on every tick
                string key = title + "\n" + artist;
                if (key != artKey)
                {
                    art = null;
                    if (props?.Thumbnail != null)
                    {
                        using var ras = await props.Thumbnail.OpenReadAsync();
                        using var stream = ras.AsStreamForRead();
                        var ms = new MemoryStream();
                        await stream.CopyToAsync(ms);
                        ms.Position = 0;

                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.DecodePixelWidth = 300; // enough to stay sharp after the cover is cut out of its card
                        bmp.StreamSource = ms;
                        bmp.EndInit();
                        bmp.Freeze();
                        art = Palette.CropToCover(bmp); // drops the gray card and logo strip some players add
                    }
                    // some players publish the title first and the cover a moment later
                    artKey = art != null ? key : "\0";
                }
            }
            catch
            {
                // A session can vanish mid-read; keep whatever we managed to get.
            }
        }
        else
        {
            art = null;
            artKey = "\0";
        }

        if (id != _refreshId) return; // a newer refresh superseded this one

        Title = title;
        Artist = artist;
        IsPlaying = playing;
        HasSession = session != null && (title.Length > 0 || artist.Length > 0);
        if (!ReferenceEquals(art, Art))
        {
            // once per cover, not per update
            ArtColor = art is BitmapSource cover ? Palette.Dominant(cover) : null;
            ArtColors = art is BitmapSource cover2 ? Palette.TopColors(cover2, 3) : new List<Color>();
        }
        Art = art;
        _artKey = artKey;
        Duration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
        _position = position;
        _positionAt = positionAt;
        Changed?.Invoke();
    }

    public async void PlayPause()
    {
        try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { }
    }

    public async void Next()
    {
        try { if (_session != null) await _session.TrySkipNextAsync(); } catch { }
    }

    public async void Previous()
    {
        try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { }
    }

    public async void Seek(TimeSpan position)
    {
        if (_session == null) return;
        _position = position; // show the jump immediately; the player confirms a moment later
        _positionAt = DateTimeOffset.Now;
        try { await _session.TryChangePlaybackPositionAsync(position.Ticks); } catch { }
    }
}
