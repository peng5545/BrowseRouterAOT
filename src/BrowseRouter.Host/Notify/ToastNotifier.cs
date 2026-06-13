using BrowseRouter.Core;
using BrowseRouter.Core.Config;
using BrowseRouter.Host.Interop;
using BrowseRouter.Host.Logging;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace BrowseRouter.Host.Notify;

/// <summary>
/// Owns a dedicated STA message-loop thread on which self-drawn toast popup
/// windows live. Cross-thread <see cref="Notify"/> calls are marshalled to the
/// loop via <c>PostMessage</c> on a hidden message-only dispatcher window,
/// mirroring the pattern <see cref="Tray.TrayIcon"/> uses but with a different
/// goal (multiple short-lived toast HWNDs instead of one persistent tray
/// icon).
///
/// Maintains the stack of currently visible toasts so a newly arrived one
/// pushes older ones up rather than overlapping them, and so a dismissed one
/// pulls the toasts above it back down. The math runs entirely in physical
/// pixels at the primary monitor's DPI — multi-monitor edge cases (toast
/// shown on a non-primary monitor with a different DPI) are not handled.
///
/// <see cref="Notify"/> is a no-op when <see cref="NotifyOptions.Enabled"/> is
/// false — callers don't need to check.
/// </summary>
internal sealed class ToastNotifier : IDisposable
{
    private const string DispatcherClassName = $"{Constants.AppId}.ToastDispatcher";
    private const string ToastClassName = $"{Constants.AppId}.Toast";
    private const uint InvokeMessage = User32.WmUser + 1;
    private const int LogicalSpacing = 8;
    private const int LogicalMargin = 12;
    private const int MaxConcurrentToasts = 5;

    private readonly FileLogger _log;
    private readonly User32.WndProcDelegate _dispatcherWndProcKeepAlive;
    private readonly User32.WndProcDelegate _toastWndProcKeepAlive;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ConcurrentQueue<Action> _invokeQueue = new();

    // Touched only on the message-loop thread — no lock needed.
    private readonly List<ToastWindow> _active = [];

    private NotifyOptions _options;
    private Thread? _thread;
    private IntPtr _dispatcherHwnd;
    private ushort _dispatcherClassAtom;
    private ushort _toastClassAtom;

    // Cached once in MessageLoop (must run on a thread with a live HWND). Used by
    // Reflow so a 5-toast layout only queries the DPI once, not five times.
    private uint _dispatcherDpi;

    private volatile bool _started;

    // Lifecycle state, encoded so the two transitions (running → draining
    // → disposed) are observably ordered. We only need to know "is the
    // notifier past the point of no return" — any non-zero value means
    // "ignore further Notify() calls, the message loop is on its way out".
    // Notify() treats any non-zero as "shut down". Interlocked ops guarantee
    // that the read-modify-write in Dispose is race-free without a lock.
    private int _shutdownState; // 0 = alive, 1 = draining, 2 = disposed

    public ToastNotifier(FileLogger log, NotifyOptions options)
    {
        _log = log;
        _options = options;
        _dispatcherWndProcKeepAlive = DispatcherWndProc;
        _toastWndProcKeepAlive = ToastWndProc;
    }

    /// <summary>
    /// Update preferences after a config reload. Cheap reference swap; the new
    /// snapshot takes effect on the next <see cref="Notify"/> call.
    /// </summary>
    public void UpdateOptions(NotifyOptions options) => _options = options;

    /// <summary>
    /// Show a toast that bypasses <see cref="NotifyOptions.Enabled"/>. Intended
    /// for the "this needs your attention even if you'd normally mute toasts"
    /// case — currently only the host startup path that surfaces an invalid
    /// config (the config itself controls the user's preference, so a bad
    /// config shouldn't be able to silence the warning that says it's bad).
    /// Subject to the same shutdown gating as <see cref="Notify"/>.
    /// </summary>
    public void ForceNotify(string message)
    {
        if (Volatile.Read(ref _shutdownState) != 0 || _dispatcherHwnd == IntPtr.Zero)
            return;
        var duration = _options.DurationMs;
        _invokeQueue.Enqueue(() => ShowToast(message, duration));
        User32.PostMessage(_dispatcherHwnd, InvokeMessage, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Spin up the message thread. Blocks until the dispatcher window is ready.
    /// Subsequent calls are no-ops.
    /// </summary>
    public void Start()
    {
        if (_started)
            return;
        _started = true;
        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "BrowseRouterAOT.ToastMessageLoop"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>
    /// Show a toast with the app name as the title and <paramref name="message"/>
    /// as the body. Safe to call from any thread; queued and rendered on the
    /// message-loop thread. Safe to call after <see cref="Dispose"/> — becomes
    /// a no-op (silently drops the message). This means callers can capture a
    /// <c>ToastNotifier</c> reference in a long-lived event handler without
    /// worrying about the disposal order: the captured call is always safe.
    /// </summary>
    public void Notify(string message)
    {
        // _shutdownState is the FIRST gate: a Notify that races Dispose can't
        // enqueue a ShowToast for a dispatcher that's about to close. The
        // _dispatcherHwnd == 0 check is a backstop for the moment after the
        // HWND has been zeroed but before the state is observed.
        if (Volatile.Read(ref _shutdownState) != 0 || _dispatcherHwnd == IntPtr.Zero)
            return;
        if (!_options.Enabled)
            return;

        var duration = _options.DurationMs;
        _invokeQueue.Enqueue(() => ShowToast(message, duration));
        User32.PostMessage(_dispatcherHwnd, InvokeMessage, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Shut down the message loop, destroy any visible toasts, unregister
    /// window classes. Blocks until the thread finishes.
    /// </summary>
    public void Dispose()
    {
        // Move to the draining state; idempotent — a second concurrent Dispose
        // observes a non-zero state and returns. Notify() also observes this
        // and bails, so no new ShowToast is enqueued.
        if (Interlocked.Exchange(ref _shutdownState, 1) != 0)
            return;

        // Discard any not-yet-processed InvokeMessage payloads. Items already
        // dequeued by the loop are handled by the ShowToast-level guard.
        while (_invokeQueue.TryDequeue(out _))
        {
        }

        // Zero the HWND BEFORE posting WM_CLOSE. Notify() checks this on entry
        // (not just _shutdownState) to make the post-dispose enqueue window
        // atomic; a Notify that arrives after this line is guaranteed to see
        // _dispatcherHwnd == IntPtr.Zero and bail.
        var hwnd = Interlocked.Exchange(ref _dispatcherHwnd, IntPtr.Zero);
        if (hwnd != IntPtr.Zero)
        {
            User32.PostMessage(hwnd, User32.WmClose, IntPtr.Zero, IntPtr.Zero);
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();

        // Final state — purely diagnostic, ensures repeated Dispose calls
        // always short-circuit at the gate.
        Volatile.Write(ref _shutdownState, 2);
    }

    // ─── Message loop ─────────────────────────────────────────────────────────

    private void MessageLoop()
    {
        try
        {
            RegisterClasses();
            CreateDispatcherWindow();

            // Cache the dispatcher's DPI once. It's the primary-monitor DPI by
            // construction (the HWND is on a message-only parent), so it won't
            // change while the notifier is alive. Reflow reads this field instead
            // of re-querying on every layout pass.
            var dpi = User32.GetDpiForWindow(_dispatcherHwnd);
            _dispatcherDpi = dpi == 0 ? 96u : dpi;

            _ready.Set();

            while (User32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                User32.TranslateMessage(ref msg);
                User32.DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Toast message loop crashed", ex);
            _ready.Set();
        }
        finally
        {
            CloseAllToasts();

            if (_dispatcherHwnd != IntPtr.Zero)
                User32.DestroyWindow(_dispatcherHwnd);
            var hInstance = Kernel32.GetModuleHandle(null);
            if (_dispatcherClassAtom != 0)
                User32.UnregisterClass(DispatcherClassName, hInstance);
            if (_toastClassAtom != 0)
                User32.UnregisterClass(ToastClassName, hInstance);
        }
    }

    private void RegisterClasses()
    {
        var hInstance = Kernel32.GetModuleHandle(null);
        _dispatcherClassAtom = RegisterOne(DispatcherClassName, _dispatcherWndProcKeepAlive, hInstance);
        _toastClassAtom = RegisterOne(ToastClassName, _toastWndProcKeepAlive, hInstance);
    }

    private static ushort RegisterOne(string name, User32.WndProcDelegate wndProc, IntPtr hInstance)
    {
        var classNamePtr = Marshal.StringToHGlobalUni(name);
        try
        {
            var wc = new User32.Wndclassex
            {
                cbSize = (uint) Marshal.SizeOf<User32.Wndclassex>(),
                style = 0,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = hInstance,
                hIcon = IntPtr.Zero,
                hCursor = User32.LoadCursor(IntPtr.Zero, User32.IdcArrow),
                hbrBackground = IntPtr.Zero,
                lpszMenuName = IntPtr.Zero,
                lpszClassName = classNamePtr,
                hIconSm = IntPtr.Zero
            };
            var atom = User32.RegisterClassEx(ref wc);
            return atom == 0
                ? throw new InvalidOperationException($"RegisterClassEx({name}) failed: {Marshal.GetLastWin32Error()}")
                : atom;
        }
        finally
        {
            Marshal.FreeHGlobal(classNamePtr);
        }
    }

    private void CreateDispatcherWindow()
    {
        _dispatcherHwnd = User32.CreateWindowEx(0, DispatcherClassName, Constants.AppName, 0, 0, 0, 0, 0,
            User32.HwndMessage, IntPtr.Zero, Kernel32.GetModuleHandle(null), IntPtr.Zero);
        if (_dispatcherHwnd == IntPtr.Zero)
            throw new InvalidOperationException(
                $"CreateWindowEx (toast dispatcher) failed: {Marshal.GetLastWin32Error()}");
    }

    // ─── WndProcs ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Hidden message-only window: drains the invoke queue on <see cref="InvokeMessage"/>,
    /// posts WM_QUIT on WM_CLOSE so the loop exits cleanly.
    /// </summary>
    private IntPtr DispatcherWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case InvokeMessage:
                while (_invokeQueue.TryDequeue(out var action))
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        _log.Error("Toast invoke", ex);
                    }
                }

                return IntPtr.Zero;

            case User32.WmClose:
                User32.DestroyWindow(hWnd);
                return IntPtr.Zero;

            case User32.WmDestroy:
                User32.PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return User32.DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }

    /// <summary>
    /// Shared WndProc for every toast HWND. Dispatches to the owning
    /// <see cref="ToastWindow"/> instance via the GCHandle stored in
    /// GWLP_USERDATA. Falls through to DefWindowProc before the toast has
    /// finished initialising (i.e. WM_CREATE, etc).
    /// </summary>
    private static IntPtr ToastWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var ud = User32.GetWindowLongPtr(hWnd, User32.GwlpUserdata);
        if (ud == IntPtr.Zero)
            return User32.DefWindowProc(hWnd, msg, wParam, lParam);
        var handle = GCHandle.FromIntPtr(ud);
        return handle is { IsAllocated: true, Target: ToastWindow toast }
            ? toast.HandleMessage(msg, wParam, lParam)
            : User32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // ─── Toast lifecycle (runs on the message-loop thread) ────────────────────

    private void ShowToast(string message, int durationMs)
    {
        // Defense-in-depth: even if an action slipped past the Notify gate, the
        // closing message loop has the final say. Creating an HWND with no loop
        // to process its messages would leak it (the GCHandle would be cleaned
        // up by the finalizer, but the HWND itself is OS-reclaimed only at
        // process exit).
        if (Volatile.Read(ref _shutdownState) != 0)
            return;

        // Drop new toasts when the stack is already full, rather than fighting
        // for screen space.
        if (_active.Count >= MaxConcurrentToasts)
        {
            _log.Info($"Toast suppressed (already showing {_active.Count}): \"{Truncate(message, 40)}\"");
            return;
        }

        var hInstance = Kernel32.GetModuleHandle(null);
        var toast = new ToastWindow(Constants.AppName, message, durationMs, _log, OnToastClosed);
        var workArea = GetPrimaryWorkArea();

        // Provisional position — Show needs an HWND to query DPI, so we offer a
        // best-effort initial location and re-layout right after.
        if (!toast.Show(ToastClassName, workArea.Right, workArea.Bottom, hInstance))
        {
            toast.Dispose();
            return;
        }

        _active.Add(toast);
        Reflow();
    }

    /// <summary>
    /// Invoked from <see cref="ToastWindow"/> on WM_NCDESTROY (same thread, so
    /// no locking). Removes the toast from the stack, frees its GDI resources,
    /// and slides the remaining toasts back down.
    /// </summary>
    private void OnToastClosed(ToastWindow toast)
    {
        _active.Remove(toast);
        toast.Dispose();
        Reflow();
    }

    /// <summary>
    /// Lay the active stack out from bottom-up at the bottom-right corner of
    /// the primary work area. Each toast is right-aligned with the others.
    /// </summary>
    private void Reflow()
    {
        if (_active.Count == 0)
            return;

        var workArea = GetPrimaryWorkArea();
        // Reuse the dispatcher DPI cached at startup — querying GetDpiForWindow
        // on every layout pass is wasted work since the value can't change for
        // a message-only window in this process's lifetime.
        var dpi = _dispatcherDpi == 0 ? 96u : _dispatcherDpi;
        var spacing = (int) (LogicalSpacing * dpi / 96.0);
        var margin = (int) (LogicalMargin * dpi / 96.0);

        var y = workArea.Bottom - margin;
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var t = _active[i];
            y -= t.Height;
            var x = workArea.Right - t.Width - margin;
            t.MoveTo(x, y);
            y -= spacing;
        }
    }

    private void CloseAllToasts()
    {
        // Snapshot the list — Close() → WM_NCDESTROY → OnToastClosed
        // removes the toast from _active, modifying the collection we're
        // iterating. (The copy itself isn't modified; only _active is.)
        var copy = _active.ToList();
        foreach (var t in copy)
        {
            try
            {
                t.Close();
            }
            catch (Exception ex)
            {
                _log.Warn($"Toast close: {ex.Message}");
            }
            finally
            {
                // Safety net: Close()'s DestroyWindow normally fires
                // WM_NCDESTROY synchronously, which calls OnToastClosed →
                // Dispose. If Close() throws, or if the underlying
                // DestroyWindow ever fails to surface WM_NCDESTROY (the
                // old AnimateWindow-in-WndProc bug), this finally clause
                // makes sure the GCHandle + GDI handles are still released.
                // Idempotent.
                t.Dispose();
            }
        }

        _active.Clear();
    }

    private static User32.Rect GetPrimaryWorkArea()
    {
        var rect = new User32.Rect();
        if (User32.SystemParametersInfo(User32.SpiGetworkarea, 0, ref rect, 0))
            return rect;

        // Fallback — full primary screen (no taskbar exclusion). SM_CXSCREEN=0,
        // SM_CYSCREEN=1. Better than nothing.
        return new User32.Rect
        {
            Left = 0,
            Top = 0,
            Right = User32.GetSystemMetrics(0),
            Bottom = User32.GetSystemMetrics(1)
        };
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty : s.Length <= max ? s : s[..max];
}