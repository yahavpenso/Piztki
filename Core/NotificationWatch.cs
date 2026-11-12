using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Piztki.Core;

/// <summary>
/// Notices new Windows notifications (a Discord message, a WhatsApp chat, a calendar alert) so the
/// notch can show them for a moment. It only reads what Windows already put in its notification
/// list; nothing is stored and nothing leaves the PC.
/// </summary>
public sealed class NotificationWatch
{
    readonly HashSet<uint> _seen = new();
    UserNotificationListener? _listener;
    bool _baseline, _busy, _denied;

    /// <summary>App name, then the notification's first two lines.</summary>
    public event Action<string, string, string>? Arrived;

    /// <summary>Why nothing is being shown, for diagnostics; empty when all is well.</summary>
    public string Status { get; private set; } = "not started";

    public void Reset()
    {
        _seen.Clear();
        _baseline = false;
    }

    /// <summary>Call every couple of seconds from the UI thread.</summary>
    public async void Poll()
    {
        if (_busy || _denied) return;
        _busy = true;
        try
        {
            if (_listener == null)
            {
                var listener = UserNotificationListener.Current;
                var access = await listener.RequestAccessAsync();
                if (access != UserNotificationListenerAccessStatus.Allowed)
                {
                    _denied = true;
                    Status = "Windows did not allow reading notifications (" + access + ")";
                    return;
                }
                _listener = listener;
            }

            var list = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            var current = new HashSet<uint>();
            foreach (var n in list)
            {
                current.Add(n.Id);
                if (_seen.Contains(n.Id) || !_baseline) continue;
                // only fresh ones: an old notification that reappears in the list is not news
                if (DateTimeOffset.Now - n.CreationTime > TimeSpan.FromSeconds(30)) continue;

                string app = "";
                try { app = n.AppInfo?.DisplayInfo?.DisplayName ?? ""; } catch { /* some system senders have no app info */ }
                if (app.Equals("Piztki", StringComparison.OrdinalIgnoreCase)) continue;

                string title = "", body = "";
                try
                {
                    var texts = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements();
                    if (texts != null)
                    {
                        if (texts.Count > 0) title = texts[0].Text ?? "";
                        if (texts.Count > 1) body = texts[1].Text ?? "";
                    }
                }
                catch
                {
                    // an unusual layout: the app name alone still says something arrived
                }

                Arrived?.Invoke(app, title, body);
            }

            _seen.Clear();
            foreach (var id in current) _seen.Add(id);
            _baseline = true; // whatever was already there when we started is not announced
            Status = "";
        }
        catch (Exception ex)
        {
            Status = ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }
}
