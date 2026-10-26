using System.Drawing;
using System.Runtime.InteropServices;
using TabletMonitor.Net;
using static TabletMonitor.Interop.User32;

namespace TabletMonitor.Input;

/// <summary>
/// Turns tablet pointers into native Windows touch input (InjectTouchInput), so taps, scrolling, pinch zoom and
/// press-and-hold behave like on a real touch screen.
/// </summary>
/// <remarks>
/// InjectSyntheticPointerInput (the newer API that also supports pen) reports success but delivers nothing on
/// the tested Windows 11 build – verified with "--touchtest" – so the stylus is injected as a touch contact for now.
/// </remarks>
public sealed class TouchInjector : IDisposable
{
    private const int MaxContacts = 10;
    private const byte PenContactId = MaxContacts - 1;
    private const uint TOUCH_FEEDBACK_DEFAULT = 0x1;

    private static bool _initialized;

    private readonly object _gate = new();
    private readonly Dictionary<byte, POINTER_TOUCH_INFO> _contacts = new();
    private readonly Timer _keepAlive;
    private DateTime _lastInjection;
    private Rectangle _screen;

    // Windows moves the system cursor to every touch (mouse promotion for legacy apps). We put it back
    // where the user left it once the touch interaction ends, so the mouse "stays" on its own screen.
    private POINT? _savedCursor;

    public TouchInjector()
    {
        if (!_initialized)
        {
            _initialized = InitializeTouchInjection(MaxContacts, TOUCH_FEEDBACK_DEFAULT);
            if (!_initialized)
                Log.Warn($"InitializeTouchInjection failed: {Marshal.GetLastWin32Error()}");
        }
        // Windows cancels touch contacts that are not refreshed – keep held fingers alive.
        _keepAlive = new Timer(_ => KeepAlive(), null, 100, 100);
    }

    /// <summary>Injects one pointer event. <paramref name="screen"/> is the tablet display in desktop pixels.</summary>
    public void Inject(PointerData[] pointers, Rectangle screen)
    {
        lock (_gate)
        {
            _screen = screen;
            if (_contacts.Count == 0)
                SaveCursor();

            foreach (var p in pointers)
            {
                // Stylus hover has no touch equivalent; contact is mapped to a dedicated touch id.
                if (p.Tool != PointerTool.Finger && p.State is PointerState.Hover or PointerState.HoverExit)
                    continue;
                var id = p.Tool == PointerTool.Finger ? (byte)Math.Min(p.Id, (byte)(PenContactId - 1)) : PenContactId;
                Update(id, p, screen);
            }
            Flush();
        }
    }

    private void Update(byte id, PointerData p, Rectangle screen)
    {
        var isNew = !_contacts.TryGetValue(id, out var contact);
        if (isNew && p.State != PointerState.Down)
            return; // stale move for a contact we never saw going down
        if (!isNew && p.State == PointerState.Down)
            return; // already down (duplicate)

        var point = ToScreen(p, screen);
        contact.pointerInfo.pointerType = PT_TOUCH;
        contact.pointerInfo.pointerId = id;
        contact.pointerInfo.ptPixelLocation = point;
        contact.touchFlags = 0;
        contact.touchMask = TOUCH_MASK_CONTACTAREA | TOUCH_MASK_PRESSURE;
        contact.rcContact = new RECT { Left = point.X - 2, Top = point.Y - 2, Right = point.X + 2, Bottom = point.Y + 2 };
        contact.pressure = (uint)Math.Clamp(p.Pressure * 1024, 1, 1024);
        contact.pointerInfo.pointerFlags = p.State switch
        {
            PointerState.Down => POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT,
            PointerState.Up => POINTER_FLAG_UP,
            // POINTER_FLAG_CANCELED makes InjectTouchInput fail with ERROR_INVALID_PARAMETER – a plain up it is.
            PointerState.Cancel or PointerState.HoverExit => POINTER_FLAG_UP,
            _ => POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT,
        };
        _contacts[id] = contact;
    }

    /// <summary>Sends one frame describing every contact, then turns downs into updates and drops lifted ones.</summary>
    private void Flush()
    {
        var frame = _contacts.Values.OrderBy(c => c.pointerInfo.pointerId).ToArray();
        if (frame.Length == 0)
            return;

        if (!InjectTouchInput((uint)frame.Length, frame))
            Log.Warn($"Touch injection failed: {Marshal.GetLastWin32Error()}");
        _lastInjection = DateTime.UtcNow;

        foreach (var c in frame)
        {
            var id = (byte)c.pointerInfo.pointerId;
            if ((c.pointerInfo.pointerFlags & POINTER_FLAG_UP) != 0)
            {
                _contacts.Remove(id);
            }
            else
            {
                var u = c;
                u.pointerInfo.pointerFlags = POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;
                _contacts[id] = u;
            }
        }

        if (_contacts.Count == 0)
            RestoreCursorSoon();
    }

    /// <summary>Lifts every contact (session ended).</summary>
    public void ReleaseAll()
    {
        lock (_gate)
        {
            if (_contacts.Count == 0)
                return;
            foreach (var id in _contacts.Keys.ToList())
            {
                var c = _contacts[id];
                c.pointerInfo.pointerFlags = POINTER_FLAG_UP;
                _contacts[id] = c;
            }
            Flush();
        }
    }

    private void KeepAlive()
    {
        lock (_gate)
        {
            if (_contacts.Count == 0 || DateTime.UtcNow - _lastInjection < TimeSpan.FromMilliseconds(90))
                return;
            Flush();
        }
    }

    private void SaveCursor()
    {
        if (GetCursorPos(out var pt))
            _savedCursor = pt;
    }

    /// <summary>
    /// Puts the cursor back after Windows has delivered the promoted mouse click – but only if the touch really
    /// pulled it onto the tablet screen, so a mouse being used elsewhere is never overridden.
    /// </summary>
    private void RestoreCursorSoon()
    {
        if (_savedCursor is not { } saved)
            return;
        _savedCursor = null;
        var screen = _screen;
        if (screen.Contains(saved.X, saved.Y))
            return; // the mouse was on the tablet anyway
        _ = Task.Run(async () =>
        {
            await Task.Delay(150);
            lock (_gate)
            {
                if (_contacts.Count > 0)
                    return; // a new touch started – it will restore later
                if (GetCursorPos(out var now) && screen.Contains(now.X, now.Y))
                    SetCursorPos(saved.X, saved.Y);
            }
        });
    }

    private static POINT ToScreen(PointerData p, Rectangle screen) => new()
    {
        X = screen.X + (int)Math.Round(Math.Clamp(p.X, 0, 1) * (screen.Width - 1)),
        Y = screen.Y + (int)Math.Round(Math.Clamp(p.Y, 0, 1) * (screen.Height - 1)),
    };

    public void Dispose()
    {
        _keepAlive.Dispose();
        ReleaseAll();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InitializeTouchInjection(uint maxCount, uint dwMode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InjectTouchInput(uint count, [In] POINTER_TOUCH_INFO[] contacts);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
}
