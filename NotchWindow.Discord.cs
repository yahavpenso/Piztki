using System.Windows;
using Piztki.Core;

namespace Piztki;

// Discord: the status on the user's profile, and mute / deafen / who's talking in the Sound tab.
public partial class NotchWindow
{
    readonly DiscordRpc _discord = new();
    bool _discordBusy;
    DateTime _discordRetryAt = DateTime.MinValue;
    string _presenceKey = "";
    DateTime _presenceAt = DateTime.MinValue;

    /// <summary>Raised whenever the line the settings page shows may have changed.</summary>
    public event Action? DiscordStatusChanged;

    /// <summary>One line for the settings page: connected as whom, or what is missing.</summary>
    public string DiscordStatus
    {
        get
        {
            if (_s.DiscordAppId.Trim().Length == 0) return "Paste your Application ID to connect";
            if (!_discord.Connected) return _discord.Status.Length > 0 ? _discord.Status : "Connecting…";
            string who = _discord.UserName.Length > 0 ? "Connected as " + _discord.UserName : "Connected";
            if (_discord.Status.Length > 0) return who + " · " + _discord.Status;
            return who + (_discord.VoiceReady ? " · voice controls ready" : _s.DiscordVoice ? " · voice controls not connected yet" : "");
        }
    }

    public bool DiscordConnected => _discord.Connected;
    public bool DiscordVoiceReady => _discord.VoiceReady;

    void InitDiscord()
    {
        _discord.TokensRenewed += (access, refresh) => Dispatcher.BeginInvoke(() =>
        {
            _s.DiscordAccess = access;
            _s.DiscordRefresh = refresh;
        });
        _discord.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            UpdateDiscordRow();
            DiscordStatusChanged?.Invoke();
        });
        UpdateDiscordRow();
    }

    /// <summary>The Application ID changed: drop the old connection and try the new one right away.</summary>
    void ResetDiscord()
    {
        _discord.Disconnect();
        _discordRetryAt = DateTime.MinValue;
        _presenceKey = "";
        DiscordStatusChanged?.Invoke();
    }

    /// <summary>Once a second, from the main tick. Cheap unless something actually changed.</summary>
    async void TickDiscord()
    {
        if (_selfTest || _discordBusy) return;

        bool wanted = _s.DiscordAppId.Trim().Length > 0 && (_s.DiscordPresence || _s.DiscordVoice);
        if (!wanted)
        {
            if (_discord.Connected) _discord.Disconnect();
            return;
        }

        _discordBusy = true;
        try
        {
            if (!_discord.Connected)
            {
                // Discord may simply not be open: look again every fifteen seconds, not every second
                if (DateTime.Now < _discordRetryAt) return;
                _discordRetryAt = DateTime.Now.AddSeconds(15);

                if (await _discord.ConnectAsync(_s.DiscordAppId))
                {
                    _presenceKey = "";
                    if (_s.DiscordVoice && (_s.DiscordAccess.Length > 0 || _s.DiscordRefresh.Length > 0))
                        await SignInDiscordVoice(mayAsk: false);
                }
                DiscordStatusChanged?.Invoke();
                WriteDiscordState();
                return;
            }

            await UpdatePresence();
        }
        catch
        {
            // a broken pipe is handled inside DiscordRpc; nothing here may take the notch down
        }
        finally
        {
            _discordBusy = false;
        }
    }

    // ---------- profile status ----------

    /// <summary>What the profile should say right now: a running timer first, then the song, else nothing.</summary>
    (string? details, string? state, DateTimeOffset? ends) PresenceNow()
    {
        if (!_s.DiscordPresence) return (null, null, null);
        var now = DateTimeOffset.Now;
        if (_pomoRunning) return (_pomo == PomoState.Break ? "On a break" : "Focusing", "Focus timer", now + _pomoLeft);
        if (_countdownRunning) return ("Timer running", null, now + _countdownLeft);
        if (_stopwatch.IsRunning) return ("Stopwatch running", null, null);
        if (_s.ShowMusic && _media.IsPlaying && _media.Title.Length > 0)
            return (_media.Title, _media.Artist.Length > 0 ? "by " + _media.Artist : "Listening to music", null);
        return (null, null, null);
    }

    async Task UpdatePresence()
    {
        var (details, state, ends) = PresenceNow();
        // the end time is recomputed every second and wobbles by a few milliseconds: compare it coarsely
        string key = $"{details}|{state}|{(ends?.ToUnixTimeSeconds() + 2) / 5}";
        if (key == _presenceKey) return;
        if (DateTime.Now - _presenceAt < TimeSpan.FromSeconds(5)) return; // Discord allows only a few changes per 20 s

        _presenceAt = DateTime.Now;
        if (await _discord.SetActivityAsync(details, state, null, ends)) _presenceKey = key;
        else DiscordStatusChanged?.Invoke();
    }

    // ---------- voice ----------

    async Task<bool> SignInDiscordVoice(bool mayAsk)
    {
        var tokens = await _discord.SignInForVoiceAsync(_s.DiscordSecret, _s.DiscordAccess, _s.DiscordRefresh, mayAsk);
        if (tokens is { } t)
        {
            _s.DiscordAccess = t.access;
            _s.DiscordRefresh = t.refresh;
        }
        UpdateDiscordRow();
        return tokens != null;
    }

    /// <summary>The settings page's "Connect voice controls" button: Discord pops up an Authorize window.</summary>
    public async Task ConnectDiscordVoice()
    {
        while (_discordBusy) await Task.Delay(100);
        _discordBusy = true;
        try
        {
            if (!_discord.Connected && !await _discord.ConnectAsync(_s.DiscordAppId)) return;
            await SignInDiscordVoice(mayAsk: true);
        }
        catch (Exception ex)
        {
            // whatever Discord answered, a button press must never take the notch down
            App.LogError("Discord voice sign-in", ex);
        }
        finally
        {
            _discordBusy = false;
            DiscordStatusChanged?.Invoke();
        }
    }

    string _discordStateWritten = "";

    /// <summary>Keeps a one-line note of the Discord connection in the settings folder, for troubleshooting. No secrets in it.</summary>
    void WriteDiscordState()
    {
        if (_selfTest) return;
        string state = $"connected={_discord.Connected} voiceReady={_discord.VoiceReady} voiceSetting={_s.DiscordVoice} rowVisible={DiscordRow.Visibility} "
                     + $"inChannel={_discord.ChannelId.Length > 0} members={_discord.MemberCount} status='{_discord.Status}' lastSignInError='{_discord.LastSignInError}' busy={_discordBusy}";
        if (state == _discordStateWritten) return;
        _discordStateWritten = state;
        try
        {
            string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piztki");
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "discord-state.txt"), DateTime.Now.ToString("HH:mm:ss") + "  " + state);
        }
        catch { }
    }

    void UpdateDiscordRow()
    {
        bool show = _s.DiscordVoice && _discord.VoiceReady;
        DiscordRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        WriteDiscordState();
        if (!show) return;
        ShowDiscordRow(_discord.Channel, _discord.MemberCount, _discord.SpeakingNames(), _discord.Muted, _discord.Deafened);
    }

    void ShowDiscordRow(string channel, int members, List<string> speaking, bool muted, bool deafened)
    {
        string text;
        if (channel.Length == 0 && members == 0) text = "Discord · not in a voice channel";
        else
        {
            text = "Discord · " + (channel.Length > 0 ? Shorten(channel, 18) : "in a call");
            if (speaking.Count == 1) text += " · " + Shorten(speaking[0], 16) + " is talking";
            else if (speaking.Count > 1) text += $" · {speaking.Count} people talking";
            else if (members > 0) text += $" · {members} here";
        }
        DiscordText.Text = text;
        DiscordDot.Fill = speaking.Count > 0 ? MicLiveBrush : muted || deafened ? MicMutedBrush : MicIdleBrush;
        DiscordMuteBtn.Content = muted ? "Unmute" : "Mute";
        DiscordDeafBtn.Content = deafened ? "Undeafen" : "Deafen";
    }

    async void DiscordMute_Click(object sender, RoutedEventArgs e)
    {
        Fx.Pop(DiscordMuteBtn, from: 0.85, ms: 300);
        try { await _discord.SetMutedAsync(!_discord.Muted); }
        catch (Exception ex) { App.LogError("Discord mute", ex); }
    }

    async void DiscordDeaf_Click(object sender, RoutedEventArgs e)
    {
        Fx.Pop(DiscordDeafBtn, from: 0.85, ms: 300);
        try { await _discord.SetDeafenedAsync(!_discord.Deafened); }
        catch (Exception ex) { App.LogError("Discord deafen", ex); }
    }
}
