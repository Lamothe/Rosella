namespace Rosella.Compositor;

internal readonly record struct NotificationKey(uint Number, string? AppId = null, string? Token = null);

internal enum NotificationCloseReason : uint
{
    Expired = 1,
    Dismissed = 2,
    Closed = 3,
    ReplacedInStack = 4,
}

/// <summary>The transient notification banners for one output.</summary>
internal sealed class NotificationStack
{
    private const int MaxVisible = 3;
    private const int DefaultTimeout = 5000;
    private const int CardHeight = 88;
    private const int CardGap = 8;

    private readonly object _gate = new();
    private readonly List<Toast> _toasts = [];
    private uint _nextId;

    internal Action? Dirty { get; set; }
    internal Action<uint, NotificationCloseReason>? Closed { get; set; }

    internal NotificationKey Show(NotificationKey key, string app, string title, string body, int timeout)
    {
        var notification = new Toast(key, Clean(app, 80), Clean(title, 240), Clean(body, 512),
            timeout == 0 ? long.MaxValue : Environment.TickCount64 + (timeout < 0 ? DefaultTimeout : timeout));
        Toast? overflow = null;
        lock (_gate)
        {
            var index = _toasts.FindIndex(toast => toast.Key == key);
            if (index >= 0)
            {
                _toasts[index] = notification;
            }
            else
            {
                if (key.AppId is null)
                {
                    do
                    {
                        key = new NotificationKey(++_nextId);
                    }
                    while (key.Number == 0 || _toasts.Exists(toast => toast.Key == key));
                    notification = notification with { Key = key };
                }

                if (_toasts.Count == MaxVisible)
                {
                    overflow = _toasts[^1];
                    _toasts.RemoveAt(_toasts.Count - 1);
                }
                _toasts.Insert(0, notification);
            }
        }

        if (overflow is not null)
        {
            NotifyClosed(overflow, NotificationCloseReason.ReplacedInStack);
        }
        Dirty?.Invoke();
        return key;
    }

    internal bool Close(NotificationKey key)
    {
        Toast removed;
        lock (_gate)
        {
            var index = _toasts.FindIndex(toast => toast.Key == key);
            if (index < 0)
            {
                return false;
            }

            removed = _toasts[index];
            _toasts.RemoveAt(index);
        }
        NotifyClosed(removed, NotificationCloseReason.Closed);
        Dirty?.Invoke();
        return true;
    }

    internal void Expire()
    {
        List<Toast>? expired = null;
        var now = Environment.TickCount64;
        lock (_gate)
        {
            for (var index = _toasts.Count - 1; index >= 0; index--)
            {
                if (_toasts[index].Deadline <= now)
                {
                    (expired ??= []).Add(_toasts[index]);
                    _toasts.RemoveAt(index);
                }
            }
        }

        if (expired is not null)
        {
            foreach (var toast in expired)
            {
                NotifyClosed(toast, NotificationCloseReason.Expired);
            }
            Dirty?.Invoke();
        }
    }

    internal bool Hit(int width, int x, int y, bool dismiss = false)
    {
        Toast? removed = null;
        lock (_gate)
        {
            for (var index = 0; index < _toasts.Count; index++)
            {
                if (!Card(width, index).Contains(x, y))
                {
                    continue;
                }
                if (!dismiss)
                {
                    return true;
                }
                removed = _toasts[index];
                _toasts.RemoveAt(index);
                break;
            }
        }

        if (removed is null)
        {
            return false;
        }
        NotifyClosed(removed, NotificationCloseReason.Dismissed);
        Dirty?.Invoke();
        return true;
    }

    internal void Draw(Span<byte> pixels, int width, int height, int stride, int scale, in DamageRect damage)
    {
        Toast[] visible;
        lock (_gate)
        {
            if (_toasts.Count == 0)
            {
                return;
            }
            visible = _toasts.ToArray();
        }

        for (var index = 0; index < visible.Length; index++)
        {
            var card = Card(width / scale, index);
            var physical = card.Scaled(scale);
            if (!damage.Intersects(new DamageRect(physical.X, physical.Y, physical.W, physical.H)))
            {
                continue;
            }

            Chrome.FillRoundedRect(pixels, width, height, stride, card, 16, 0xFF42424A, scale);
            Chrome.FillRoundedRect(pixels, width, height, stride,
                new Rect(card.X + 1, card.Y + 1, card.W - 2, card.H - 2), 15, 0xFF222226, scale);
            var toast = visible[index];
            var textWidth = card.W - 48;
            var x = (card.X + 16) * scale;
            ShellText.DrawText(pixels, width, height, stride, ShellText.Fit(toast.App, 12, textWidth), x,
                (card.Y + 11) * scale, 0xFFB8B8BF, 12, scale);
            ShellText.DrawText(pixels, width, height, stride, "×", (card.X + card.W - 26) * scale,
                (card.Y + 9) * scale, 0xFFB8B8BF, 17, scale);
            ShellText.DrawText(pixels, width, height, stride, ShellText.Fit(toast.Title, 16, textWidth), x,
                (card.Y + 31) * scale, 0xFFF4F4F5, 16, scale);
            ShellText.DrawText(pixels, width, height, stride, ShellText.Fit(toast.Body, 13, textWidth), x,
                (card.Y + 57) * scale, 0xFFD9D9DF, 13, scale);
        }
    }

    private static Rect Card(int width, int index)
    {
        var cardWidth = Math.Clamp(width - 32, 0, 440);
        return new Rect((width - cardWidth) / 2, 16 + index * (CardHeight + CardGap), cardWidth, CardHeight);
    }

    private void NotifyClosed(Toast toast, NotificationCloseReason reason)
    {
        if (toast.Key.AppId is null)
        {
            Closed?.Invoke(toast.Key.Number, reason);
        }
    }

    private static string Clean(string text, int maxLength) =>
        new string(text.Take(maxLength).Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();

    private sealed record Toast(NotificationKey Key, string App, string Title, string Body, long Deadline);
}
