using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Piztki.Core;

/// <summary>
/// Talks to the Discord app running on this PC through its local pipe (nothing goes over the
/// internet except the one sign-in step for voice control, which Discord requires).
///
/// Two things use it: the profile status ("Focusing · 18 min left"), which only needs the user's
/// own Discord Application ID, and voice control (mute, deafen, who is talking), which also
/// needs that application's secret and a one-time "Authorize" click inside Discord.
/// </summary>
public sealed class DiscordRpc : IDisposable
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    public const string RedirectUri = "http://localhost";
    static readonly string[] VoiceScopes = { "rpc", "rpc.voice.read", "rpc.voice.write" };

    readonly SemaphoreSlim _writing = new(1, 1);
    readonly Dictionary<string, TaskCompletionSource<JsonNode?>> _waiting = new();
    NamedPipeClientStream? _pipe;
    string _clientId = "";
    CancellationTokenSource? _stop;

    /// <summary>The pipe is open and Discord accepted the Application ID.</summary>
    public bool Connected { get; private set; }
    /// <summary>Signed in for voice control.</summary>
    public bool VoiceReady { get; private set; }
    /// <summary>The Discord user's name, once connected.</summary>
    public string UserName { get; private set; } = "";
    /// <summary>What went wrong last, in words a person can act on; empty when fine.</summary>
    public string Status { get; private set; } = "Not connected";

    public bool Muted { get; private set; }
    public bool Deafened { get; private set; }
    /// <summary>The voice channel the user is in, or empty.</summary>
    public string Channel { get; private set; } = "";
    public string ChannelId { get; private set; } = "";
    /// <summary>People in the channel by id, and the ids of those talking right now.</summary>
    public Dictionary<string, string> Members { get; } = new();
    public HashSet<string> Speaking { get; } = new();

    /// <summary>What Discord said the last time it refused a sign-in, for diagnostics.</summary>
    public string LastSignInError { get; private set; } = "";

    /// <summary>Discord swapped the saved sign-in for a fresh one: store it right away.</summary>
    public event Action<string, string>? TokensRenewed;

    /// <summary>Something above changed. Raised on a background thread.</summary>
    public event Action? Changed;

    // ---------- connecting ----------

    /// <summary>Opens the pipe and introduces the app. Safe to call again and again.</summary>
    public async Task<bool> ConnectAsync(string clientId)
    {
        clientId = clientId.Trim();
        if (Connected && clientId == _clientId) return true;
        Disconnect();
        if (clientId.Length < 15 || !clientId.All(char.IsDigit))
        {
            Status = "The Application ID should be a long number";
            return false;
        }
        _clientId = clientId;

        for (int i = 0; i < 10 && _pipe == null; i++)
        {
            var pipe = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(150);
                _pipe = pipe;
            }
            catch
            {
                pipe.Dispose();
            }
        }
        if (_pipe == null)
        {
            Status = "Discord isn't running on this PC";
            return false;
        }

        try
        {
            await WriteAsync(0, new JsonObject { ["v"] = 1, ["client_id"] = clientId });
            var (op, hello) = await ReadAsync(_pipe, CancellationToken.None);
            if (op != 1 || hello?["evt"].Text() != "READY")
            {
                string why = hello?["message"].Text() ?? "no answer";
                Status = why.Contains("client", StringComparison.OrdinalIgnoreCase) ? "Discord doesn't know this Application ID" : "Discord refused: " + why;
                Disconnect(keepStatus: true);
                return false;
            }
            UserName = hello["data"]?["user"]?["global_name"].Text()
                       ?? hello["data"]?["user"]?["username"].Text() ?? "";
        }
        catch (Exception ex)
        {
            Status = "Couldn't talk to Discord (" + ex.GetType().Name + ")";
            Disconnect(keepStatus: true);
            return false;
        }

        Connected = true;
        Status = "";
        _stop = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoop(_pipe, _stop.Token));
        Changed?.Invoke();
        return true;
    }

    public void Disconnect(bool keepStatus = false)
    {
        _stop?.Cancel();
        _stop = null;
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        bool was = Connected;
        Connected = VoiceReady = false;
        Channel = ChannelId = "";
        Members.Clear();
        Speaking.Clear();
        lock (_waiting)
        {
            foreach (var w in _waiting.Values) w.TrySetResult(null);
            _waiting.Clear();
        }
        if (!keepStatus) Status = "Not connected";
        if (was) Changed?.Invoke();
    }

    public void Dispose() => Disconnect();

    // ---------- the wire: [int32 opcode][int32 length][json] ----------

    async Task WriteAsync(int op, JsonNode payload)
    {
        var pipe = _pipe ?? throw new IOException("not connected");
        byte[] json = Encoding.UTF8.GetBytes(payload.ToJsonString());
        byte[] frame = new byte[8 + json.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), op);
        BitConverter.TryWriteBytes(frame.AsSpan(4, 4), json.Length);
        json.CopyTo(frame, 8);

        await _writing.WaitAsync();
        try
        {
            await pipe.WriteAsync(frame);
            await pipe.FlushAsync();
        }
        finally
        {
            _writing.Release();
        }
    }

    static async Task<(int op, JsonNode? body)> ReadAsync(Stream pipe, CancellationToken stop)
    {
        byte[] head = new byte[8];
        await pipe.ReadExactlyAsync(head, stop);
        int op = BitConverter.ToInt32(head, 0), length = BitConverter.ToInt32(head, 4);
        if (length < 0 || length > 4 * 1024 * 1024) throw new IOException("bad frame");
        byte[] body = new byte[length];
        await pipe.ReadExactlyAsync(body, stop);
        return (op, length == 0 ? null : JsonNode.Parse(body));
    }

    async Task ReadLoop(NamedPipeClientStream pipe, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var (op, body) = await ReadAsync(pipe, stop);
                if (op == 3) { await WriteAsync(4, body ?? new JsonObject()); continue; } // ping
                if (op == 2) break;                                                         // Discord closed it
                if (op != 1 || body == null) continue;

                string? nonce = body["nonce"].Text();
                if (nonce != null)
                {
                    TaskCompletionSource<JsonNode?>? waiter;
                    lock (_waiting) _waiting.Remove(nonce, out waiter);
                    waiter?.TrySetResult(body);
                    continue;
                }
                OnEvent(body["evt"].Text() ?? "", body["data"]);
            }
        }
        catch
        {
            // the pipe broke: Discord was closed or restarted
        }
        if (ReferenceEquals(pipe, _pipe))
        {
            Disconnect();
            Status = "Discord was closed";
        }
    }

    /// <summary>Sends a command and waits for Discord's answer to it (null when there is none).</summary>
    async Task<JsonNode?> AskAsync(string cmd, JsonNode? args, string? evt = null)
    {
        if (!Connected) return null;
        string nonce = Guid.NewGuid().ToString();
        var waiter = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waiting) _waiting[nonce] = waiter;

        var message = new JsonObject { ["cmd"] = cmd, ["nonce"] = nonce };
        if (args != null) message["args"] = args;
        if (evt != null) message["evt"] = evt;
        try
        {
            await WriteAsync(1, message);
        }
        catch
        {
            lock (_waiting) _waiting.Remove(nonce);
            return null;
        }

        // the Authorize window waits for the user, so that one gets a long time
        var done = await Task.WhenAny(waiter.Task, Task.Delay(cmd == "AUTHORIZE" ? 120_000 : 6_000));
        if (done != waiter.Task)
        {
            lock (_waiting) _waiting.Remove(nonce);
            return null;
        }
        return await waiter.Task;
    }

    static bool Failed(JsonNode? answer) => answer == null || answer["evt"].Text() == "ERROR";
    static string ErrorOf(JsonNode? answer) => answer?["data"]?["message"].Text() ?? "no answer from Discord";

    // ---------- profile status ----------

    /// <summary>Shows a status on the user's Discord profile. Null lines clear it.</summary>
    public async Task<bool> SetActivityAsync(string? details, string? state, DateTimeOffset? started = null, DateTimeOffset? ends = null)
    {
        JsonNode? activity = null;
        if (!string.IsNullOrWhiteSpace(details))
        {
            var a = new JsonObject { ["details"] = Fit(details), ["type"] = 0 };
            if (!string.IsNullOrWhiteSpace(state)) a["state"] = Fit(state);
            if (started != null || ends != null)
            {
                var times = new JsonObject();
                if (started != null) times["start"] = started.Value.ToUnixTimeMilliseconds();
                if (ends != null) times["end"] = ends.Value.ToUnixTimeMilliseconds();
                a["timestamps"] = times;
            }
            activity = a;
        }

        var answer = await AskAsync("SET_ACTIVITY", new JsonObject { ["pid"] = Environment.ProcessId, ["activity"] = activity });
        if (Failed(answer))
        {
            if (answer != null) Status = "Discord didn't take the status: " + ErrorOf(answer);
            return false;
        }
        return true;
    }

    // Discord wants each line between 2 and 128 characters
    static string Fit(string text)
    {
        text = text.Trim();
        if (text.Length > 120) text = text[..119] + "…";
        return text.Length < 2 ? text + " " : text;
    }

    // ---------- voice control ----------

    /// <summary>
    /// Signs in for voice control with a saved token; when there is none (or it has expired and
    /// can't be renewed) asks the user to click Authorize inside Discord. Returns the tokens to
    /// save, or null when it didn't work (Status says why).
    /// </summary>
    public async Task<(string access, string refresh)?> SignInForVoiceAsync(string secret, string access, string refresh, bool mayAsk)
    {
        if (!Connected) return null;
        secret = secret.Trim();
        // already signed in on this connection: asking Discord again only earns an error
        if (VoiceReady && access.Length > 0) return (access, refresh);

        if (access.Length > 0 && await AuthenticateAsync(access)) return (access, refresh);

        if (refresh.Length > 0 && secret.Length > 0)
        {
            var renewed = await TokenAsync(secret, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh });
            if (renewed is { } r)
            {
                // renewing makes the old pair worthless, so the new pair must be kept whatever happens next
                TokensRenewed?.Invoke(r.access, r.refresh);
                if (await AuthenticateAsync(r.access)) return r;
            }
        }

        if (!mayAsk) return null;
        if (secret.Length == 0)
        {
            Status = "Voice control needs the Client Secret too";
            return null;
        }

        var scopes = new JsonArray();
        foreach (var s in VoiceScopes) scopes.Add(s);
        var asked = await AskAsync("AUTHORIZE", new JsonObject { ["client_id"] = _clientId, ["scopes"] = scopes });
        string? code = asked?["data"]?["code"].Text();
        if (Failed(asked) || code == null)
        {
            Status = asked == null ? "Nobody clicked Authorize in Discord" : "Discord said no: " + ErrorOf(asked);
            return null;
        }

        var fresh = await TokenAsync(secret, new() { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = RedirectUri });
        if (fresh is not { } f) return null;
        if (!await AuthenticateAsync(f.access))
        {
            Status = "Discord didn't accept the new sign-in";
            return null;
        }
        return f;
    }

    async Task<(string access, string refresh)?> TokenAsync(string secret, Dictionary<string, string> form)
    {
        form["client_id"] = _clientId;
        form["client_secret"] = secret;
        try
        {
            using var reply = await Http.PostAsync("https://discord.com/api/oauth2/token", new FormUrlEncodedContent(form));
            string text = await reply.Content.ReadAsStringAsync();
            var json = JsonNode.Parse(text);
            string? access = json?["access_token"].Text();
            if (!reply.IsSuccessStatusCode || access == null)
            {
                string why = json?["error_description"].Text() ?? json?["error"].Text() ?? reply.StatusCode.ToString();
                Status = why.Contains("redirect", StringComparison.OrdinalIgnoreCase) ? "Add " + RedirectUri + " under OAuth2 > Redirects in your Discord application"
                    : why.Contains("client", StringComparison.OrdinalIgnoreCase) ? "The Client Secret doesn't match the Application ID"
                    : "Discord sign-in failed: " + why;
                return null;
            }
            return (access, json?["refresh_token"].Text() ?? "");
        }
        catch
        {
            Status = "Couldn't reach discord.com for the sign-in";
            return null;
        }
    }

    async Task<bool> AuthenticateAsync(string access)
    {
        var answer = await AskAsync("AUTHENTICATE", new JsonObject { ["access_token"] = access });
        if (Failed(answer))
        {
            LastSignInError = ErrorOf(answer);
            return false;
        }
        LastSignInError = "";

        VoiceReady = true;
        Status = "";
        await AskAsync("SUBSCRIBE", null, "VOICE_SETTINGS_UPDATE");
        await AskAsync("SUBSCRIBE", null, "VOICE_CHANNEL_SELECT");
        ReadVoiceSettings((await AskAsync("GET_VOICE_SETTINGS", null))?["data"]);
        await RefreshChannelAsync();
        Changed?.Invoke();
        return true;
    }

    void ReadVoiceSettings(JsonNode? data)
    {
        if (data == null) return;
        if (data["mute"].Flag() is { } m) Muted = m;
        if (data["deaf"].Flag() is { } d) Deafened = d;
    }

    async Task RefreshChannelAsync()
    {
        var data = (await AskAsync("GET_SELECTED_VOICE_CHANNEL", null))?["data"];
        string id = data?["id"].Text() ?? "";

        if (id != ChannelId)
        {
            foreach (var evt in new[] { "SPEAKING_START", "SPEAKING_STOP", "VOICE_STATE_CREATE", "VOICE_STATE_DELETE" })
            {
                if (ChannelId.Length > 0) await AskAsync("UNSUBSCRIBE", new JsonObject { ["channel_id"] = ChannelId }, evt);
                if (id.Length > 0) await AskAsync("SUBSCRIBE", new JsonObject { ["channel_id"] = id }, evt);
            }
        }

        ChannelId = id;
        Channel = data?["name"].Text() ?? "";
        lock (Members)
        {
            Members.Clear();
            Speaking.Clear();
            if (data?["voice_states"] is JsonArray states)
                foreach (var state in states) Remember(state);
        }
    }

    void Remember(JsonNode? state)
    {
        string? id = state?["user"]?["id"].Text();
        if (id == null) return;
        Members[id] = state?["nick"].Text()
                      ?? state?["user"]?["global_name"].Text()
                      ?? state?["user"]?["username"].Text() ?? "Someone";
    }

    void OnEvent(string evt, JsonNode? data)
    {
        switch (evt)
        {
            case "VOICE_SETTINGS_UPDATE":
                ReadVoiceSettings(data);
                break;
            case "VOICE_CHANNEL_SELECT":
                _ = RefreshChannelAsync().ContinueWith(_ => Changed?.Invoke());
                return;
            case "SPEAKING_START":
                lock (Members) Speaking.Add(data?["user_id"].Text() ?? "");
                break;
            case "SPEAKING_STOP":
                lock (Members) Speaking.Remove(data?["user_id"].Text() ?? "");
                break;
            case "VOICE_STATE_CREATE":
                lock (Members) Remember(data);
                break;
            case "VOICE_STATE_DELETE":
                lock (Members)
                {
                    string id = data?["user"]?["id"].Text() ?? "";
                    Members.Remove(id);
                    Speaking.Remove(id);
                }
                break;
            default:
                return;
        }
        Changed?.Invoke();
    }

    /// <summary>Names of the people talking right now.</summary>
    public List<string> SpeakingNames()
    {
        lock (Members) return Speaking.Where(Members.ContainsKey).Select(id => Members[id]).ToList();
    }

    public int MemberCount
    {
        get { lock (Members) return Members.Count; }
    }

    public async Task SetMutedAsync(bool muted)
    {
        var answer = await AskAsync("SET_VOICE_SETTINGS", new JsonObject { ["mute"] = muted });
        if (!Failed(answer)) ReadVoiceSettings(answer!["data"]);
        Changed?.Invoke();
    }

    public async Task SetDeafenedAsync(bool deafened)
    {
        var answer = await AskAsync("SET_VOICE_SETTINGS", new JsonObject { ["deaf"] = deafened });
        if (!Failed(answer)) ReadVoiceSettings(answer!["data"]);
        Changed?.Invoke();
    }
}

/// <summary>Reading Discord's answers without ever throwing: a field of an unexpected kind is just "not there".</summary>
static class JsonRead
{
    /// <summary>The text of a string field. A number comes back as its digits; anything else as null.</summary>
    public static string? Text(this JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue(out string? text)) return text;
        return value.GetValueKind() == JsonValueKind.Number ? value.ToJsonString() : null;
    }

    public static bool? Flag(this JsonNode? node)
        => node is JsonValue value && value.TryGetValue(out bool flag) ? flag : null;
}
