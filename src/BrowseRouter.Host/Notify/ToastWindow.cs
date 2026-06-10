using BrowseRouter.Host.Interop;
using BrowseRouter.Host.Logging;
using System.Runtime.InteropServices;

namespace BrowseRouter.Host.Notify;

/// <summary>
/// One self-drawn Win32 popup notification — a small dark rounded rectangle
/// rendered at the bottom-right of the work area. Two-row layout:
///
/// <code>
///   BrowseRouter (AOT)    &lt;-- title, 12pt bold (always the app name)
///   cent->https://...     &lt;-- body, 11pt; callers supply "{browser}->{url}"
///                         (no whitespace anywhere, so the whole string is
///                          one word and DT_WORD_BREAK never splits it)
/// </code>
///
/// Inspired by <c>noxad/windows-toast-notifications</c>, but reimplemented as
/// pure Win32 to stay AOT-compatible (no WinForms / no System.Drawing).
///
/// Each toast owns one HWND on the notifier's STA message-loop thread.
/// Auto-dismisses after <c>durationMs</c> via a <c>WM_TIMER</c>; also closes on
/// any click. Avoids stealing focus by using <c>WS_EX_NOACTIVATE</c> +
/// <c>SW_SHOWNOACTIVATE</c>.
///
/// All instance methods MUST be invoked on the notifier thread — they touch the
/// HWND directly. The notifier marshals via <c>PostMessage</c>.
///
/// <para><b>Lifecycle / leak contract.</b>
/// The shared <c>WndProc</c> looks up the owning <see cref="ToastWindow"/>
/// via a <c>GCHandle</c> stored in <c>GWLP_USERDATA</c>. The handle is
/// <see cref="GCHandleType.Weak"/> — it does <b>not</b> root the instance,
/// so the GC is free to collect a <see cref="ToastWindow"/> that the
/// notifier has dropped (e.g. because <c>OnToastClosed</c> never fired).
/// When that happens, the <see cref="Finalize"/> backstop frees the
/// <c>GCHandle</c> and the three GDI handles, capping the leak at "the
/// HWND + region" (both OS-owned and small) rather than "the whole
/// managed object + GCHandle + 3 GDI handles forever".</para>
///
/// <para>The <c>WndProc</c>'s pattern match
/// <c>{ IsAllocated: true, Target: ToastWindow toast }</c> tolerates a
/// collected weak target — <c>Target</c> is <c>null</c> in that case, the
/// pattern fails, and the message falls through to <c>DefWindowProc</c>.</para>
/// </summary>
internal sealed class ToastWindow(
    string title,
    string body,
    int durationMs,
    FileLogger log,
    Action<ToastWindow> onClosed
) : IDisposable
{
    /// <summary>Logical pixels at 96 DPI — scaled per-monitor at Show() time.</summary>
    public const int LogicalWidth = 380;

    public const int LogicalHeight = 96;
    public const int LogicalPadding = 12;
    public const int LogicalCornerRadius = 14;

    /// <summary>
    /// Title row height. Must be larger than the 12pt Segoe UI bold cell
    /// (ascender + descender + internal leading ≈ 19-21px at 96 DPI, scaling
    /// with DPI), otherwise descenders on letters like <c>p</c> / <c>g</c> /
    /// <c>y</c> / <c>q</c> get clipped.
    /// </summary>
    public const int LogicalTitleHeight = 22;

    /// <summary>Vertical gap between the title row and the body row.</summary>
    public const int LogicalRowGap = 2;

    private const uint TimerId = 1;

    // Dark, high-contrast palette tuned for Win10/11. RGB values are packed by
    // Gdi32.Rgb into the COLORREF layout the GDI APIs expect. Two-row layout:
    // a bold white title (the app name) and a light-grey body (the message).
    private static readonly uint ColourBg = Gdi32.Rgb(0x20, 0x20, 0x20);
    private static readonly uint ColourTitle = Gdi32.Rgb(0xFF, 0xFF, 0xFF);
    private static readonly uint ColourBody = Gdi32.Rgb(0xCC, 0xCC, 0xCC);

    private IntPtr _backgroundBrush;
    private IntPtr _titleFont;
    private IntPtr _bodyFont;
    private GCHandle _selfHandle;
    private bool _inNcDestroy;
    private bool _closing;

    /// <summary>Actual width in physical pixels at the monitor's DPI.</summary>
    public int Width { get; private set; }

    /// <summary>Actual height in physical pixels at the monitor's DPI.</summary>
    public int Height { get; private set; }

    /// <summary>The window handle. <see cref="IntPtr.Zero"/> until <see cref="Show"/>.</summary>
    public IntPtr Handle { get; private set; }

    /// <summary>
    /// Create the window at <c>(x, y)</c>, show it without stealing focus, and
    /// kick off the auto-dismiss timer. Returns <c>true</c> if the window was
    /// created successfully.
    /// </summary>
    public bool Show(string windowClassName, int x, int y, IntPtr hInstance)
    {
        // Create initially hidden + 0×0 — we resize after we can query the target
        // monitor's DPI (which requires an HWND).
        Handle = User32.CreateWindowEx(User32.WsExTopmost | User32.WsExToolwindow | User32.WsExNoactivate,
            windowClassName, null, User32.WsPopup, x, y, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (Handle == IntPtr.Zero)
        {
            log.Warn($"CreateWindowEx (toast) failed: {Marshal.GetLastWin32Error()}");
            return false;
        }

        // Anchor a WEAK GCHandle in GWLP_USERDATA so the shared WndProc can
        // dispatch messages back to this instance. Weak is deliberate: a
        // strong (Normal) handle would root this instance and prevent the
        // finalizer backstop from ever running, which leaks the GCHandle
        // and three GDI handles if OnToastClosed ever fails to fire.
        // The WndProc's pattern match handles the (target == null) case
        // by falling through to DefWindowProc.
        _selfHandle = GCHandle.Alloc(this, GCHandleType.Weak);
        User32.SetWindowLongPtr(Handle, User32.GwlpUserdata, GCHandle.ToIntPtr(_selfHandle));

        // Scale to the monitor DPI (PerMonitorV2 — manifest declared).
        var dpi = User32.GetDpiForWindow(Handle);
        if (dpi == 0)
            dpi = 96;
        Width = Scale(LogicalWidth, dpi);
        Height = Scale(LogicalHeight, dpi);

        // Resize without activating; we'll apply the rounded region after.
        User32.SetWindowPos(Handle, User32.HwndTopmost, x, y, Width, Height,
            User32.SwpNoactivate | User32.SwpShowwindow);

        // Build the GDI resources once — fonts and the background brush live for
        // the toast's lifetime and are released in Dispose(). Title is
        // 12pt bold (a quiet header — the body is the actual content), body
        // is 11pt regular.
        _backgroundBrush = Gdi32.CreateSolidBrush(ColourBg);
        _titleFont = CreateFont(dpi, 12, Gdi32.FwBold);
        _bodyFont = CreateFont(dpi, 11, Gdi32.FwNormal);

        // Rounded corners — match the Windows 11 toast aesthetic.
        var radius = Scale(LogicalCornerRadius, dpi);
        var rgn = Gdi32.CreateRoundRectRgn(0, 0, Width, Height, radius, radius);
        // SetWindowRgn takes ownership; do not DeleteObject it ourselves.
        if (User32.SetWindowRgn(Handle, rgn, bRedraw: true) == 0)
        {
            Gdi32.DeleteObject(rgn);
        }

        // Start auto-dismiss. Clamp to [500, 60000]; values below 500ms render
        // basically as flashes and 60s is well above any reasonable use case.
        var clamped = Math.Clamp(durationMs, 500, 60_000);
        User32.SetTimer(Handle, TimerId, (uint) clamped, IntPtr.Zero);

        // Force a paint now so the toast appears with content rather than as a
        // momentarily empty rectangle.
        User32.InvalidateRect(Handle, IntPtr.Zero, bErase: true);

        return true;
    }

    /// <summary>
    /// Move the toast to <c>(x, y)</c> in physical pixels without activating.
    /// </summary>
    public void MoveTo(int x, int y)
    {
        if (Handle == IntPtr.Zero)
            return;
        User32.SetWindowPos(Handle, IntPtr.Zero, x, y, 0, 0,
            User32.SwpNosize | User32.SwpNozorder | User32.SwpNoactivate);
    }

    /// <summary>
    /// Dispatch a Win32 message to this toast. Called from the shared WndProc
    /// after GCHandle lookup. Returns the WndProc return value.
    /// </summary>
    internal IntPtr HandleMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        var hwnd = Handle;
        switch (msg)
        {
            case User32.WmPaint:
                Paint();
                return IntPtr.Zero;

            case User32.WmEraseBkgnd:
                // Suppress default erase to avoid flicker — WM_PAINT does it all.
                return new IntPtr(1);

            case User32.WmLbuttonup:
            case User32.WmRbuttonup:
                // Any click dismisses the toast immediately.
                Close();
                return IntPtr.Zero;

            case User32.WmTimer:
                if ((uint) wParam.ToInt64() == TimerId)
                {
                    Close();
                }

                return IntPtr.Zero;

            case User32.WmNcdestroy:
                // Last message we'll ever see for this HWND. Notify the manager
                // BEFORE the GCHandle is freed so the lookup in the shared
                // WndProc still succeeds. After this returns we are conceptually
                // dead.
                _inNcDestroy = true;
                onClosed.Invoke(this);
                return User32.DefWindowProc(hwnd, msg, wParam, lParam);
            default:
                return User32.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    /// <summary>
    /// Begin teardown: kill the timer, then destroy the window. The
    /// <see cref="User32.WmNcdestroy"/> message arrives synchronously inside
    /// <c>DestroyWindow</c> and drives the notifier-side cleanup
    /// (removes the toast from the stack, calls <see cref="Dispose"/>).
    ///
    /// <para>
    /// <b>No fade-out animation here.</b> The previous version called
    /// <c>AnimateWindow(AW_HIDE | AW_BLEND)</c> from inside a WndProc
    /// dispatch (this method runs from the click or timer handler). USER32
    /// forbids <c>AnimateWindow</c> re-entrantly from a WndProc, and in
    /// practice the window was left in a half-destroyed state where
    /// <c>WM_NCDESTROY</c> was never delivered — leaking the GCHandle and
    /// the three GDI handles per toast. The fade-out is sacrificed for
    /// correctness; the fade-in at show time is unaffected.
    /// </para>
    /// </summary>
    internal void Close()
    {
        if (_closing || Handle == IntPtr.Zero)
            return;

        _closing = true;
        User32.KillTimer(Handle, TimerId);
        User32.DestroyWindow(Handle);
        // WM_NCDESTROY is dispatched synchronously; the notifier's
        // OnToastClosed callback runs inside it and disposes this instance.
    }

    /// <summary>
    /// Free every GDI handle and the GCHandle, and destroy the HWND if it's
    /// still alive. Idempotent. In the normal flow <see cref="Close"/>
    /// already destroyed the window and the notifier's <c>OnToastClosed</c>
    /// calls this — by the time we get there, the HWND is invalid and the
    /// GDI objects are no longer selected into any live DC.
    /// </summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Finalizer backstop for the case where <see cref="Dispose()"/> is
    /// never invoked — e.g. the notifier dropped the toast without
    /// <c>OnToastClosed</c> firing. Runs on the GC thread.
    ///
    /// <para>
    /// Because <c>_selfHandle</c> is <see cref="GCHandleType.Weak"/>, the
    /// GC is free to collect this instance and queue this finalizer. We
    /// then release the <c>GCHandle</c> slot and the three GDI handles
    /// (best effort — <c>DeleteObject</c> on a not-currently-valid handle
    /// is a no-op). We deliberately do <b>not</b> call <c>DestroyWindow</c>
    /// here: we don't know whether the HWND is still alive, and even if it
    /// is, destroying a window from a non-message thread can deadlock. The
    /// leaked HWND is small (one kernel object) and reclaimed when its
    /// owning process exits.
    /// </para>
    /// </summary>
    ~ToastWindow()
    {
        Dispose(disposing: false);
    }

    private void Dispose(bool disposing)
    {
        var hwnd = Handle;
        // Zero out the public handle snapshot first — anyone reading Handle
        // after this point sees "gone", which is the truth.
        Handle = IntPtr.Zero;

        if (hwnd != IntPtr.Zero && disposing && !_inNcDestroy)
        {
            // Dispose called from managed code (e.g. CloseAllToasts after a
            // crash) and the window is still alive — tear it down. The
            // WM_NCDESTROY-driven path (normal flow) is filtered out by
            // _inNcDestroy so we never call DestroyWindow on a half-destroyed
            // window.
            User32.DestroyWindow(hwnd);
        }

        if (_backgroundBrush != IntPtr.Zero)
        {
            Gdi32.DeleteObject(_backgroundBrush);
            _backgroundBrush = IntPtr.Zero;
        }

        if (_titleFont != IntPtr.Zero)
        {
            Gdi32.DeleteObject(_titleFont);
            _titleFont = IntPtr.Zero;
        }

        if (_bodyFont != IntPtr.Zero)
        {
            Gdi32.DeleteObject(_bodyFont);
            _bodyFont = IntPtr.Zero;
        }

        if (_selfHandle.IsAllocated)
        {
            if (hwnd != IntPtr.Zero)
            {
                // Clear the GCHandle from the window's user data BEFORE freeing
                // the GCHandle itself, to avoid any late-arriving messages
                // hitting a freed handle in the shared WndProc.
                User32.SetWindowLongPtr(hwnd, User32.GwlpUserdata, IntPtr.Zero);
            }

            _selfHandle.Free();
            _selfHandle = default;
        }
    }

    private void Paint()
    {
        var hdc = User32.BeginPaint(Handle, out var ps);
        if (hdc == IntPtr.Zero)
            return;
        try
        {
            var dpi = User32.GetDpiForWindow(Handle);
            if (dpi == 0)
                dpi = 96;
            var pad = Scale(LogicalPadding, dpi);
            var titleH = Scale(LogicalTitleHeight, dpi);
            var rowGap = Scale(LogicalRowGap, dpi);

            // 1) Background — solid dark fill. The rounded region clips this so
            // the corners stay rounded.
            var full = new User32.Rect { Left = 0, Top = 0, Right = Width, Bottom = Height };
            User32.FillRect(hdc, ref full, _backgroundBrush);

            // Transparent text background — SetBkMode/SetTextColor return the
            // previous value, discarded explicitly to silence CA1806.
            _ = Gdi32.SetBkMode(hdc, Gdi32.Transparent);

            var prevFont = Gdi32.SelectObject(hdc, _titleFont);
            try
            {
                // Row 1: title — bold white. Conventionally the app name
                // ("BrowseRouter (AOT)"). Sits at the top of the toast.
                _ = Gdi32.SetTextColor(hdc, ColourTitle);
                var titleRect = new User32.Rect
                {
                    Left = pad,
                    Top = pad,
                    Right = Width - pad,
                    Bottom = pad + titleH
                };
                User32.DrawText(hdc, title, -1, ref titleRect,
                    User32.DtLeft | User32.DtTop | User32.DtSingleline | User32.DtEndEllipsis | User32.DtNoprefix);

                // Row 2: body — regular, word-wrap, fills remaining vertical
                // space to the bottom padding. Conventionally the action the
                // notification conveys, e.g. "cent->https://...".
                _ = Gdi32.SetTextColor(hdc, ColourBody);
                Gdi32.SelectObject(hdc, _bodyFont);
                var bodyTop = pad + titleH + rowGap;
                var bodyRect = new User32.Rect
                {
                    Left = pad,
                    Top = bodyTop,
                    Right = Width - pad,
                    Bottom = Height - pad
                };
                User32.DrawText(hdc, body, -1, ref bodyRect,
                    User32.DtLeft |
                    User32.DtTop |
                    User32.DtWordbreak |
                    User32.DtEndEllipsis |
                    User32.DtEditcontrol |
                    User32.DtNoprefix);
            }
            finally
            {
                Gdi32.SelectObject(hdc, prevFont);
            }
        }
        finally
        {
            User32.EndPaint(Handle, ref ps);
        }
    }

    private static int Scale(int logical, uint dpi) => (int) (logical * dpi / 96.0);

    private static IntPtr CreateFont(uint dpi, int pointSize, int weight)
    {
        // Standard Windows font metric: height in logical units is
        // -MulDiv(pointSize, dpi, 72). Negative = "character height" (excludes
        // internal leading) as opposed to "cell height".
        var height = -(int) (pointSize * dpi / 72.0);
        return Gdi32.CreateFont(height, 0, // width — let GDI pick
            0, 0, weight, 0, 0, 0, Gdi32.DefaultCharset, Gdi32.OutDefaultPrecis, Gdi32.ClipDefaultPrecis,
            Gdi32.CleartypeQuality, Gdi32.DefaultPitch | Gdi32.FfDontcare, "Segoe UI");
    }
}