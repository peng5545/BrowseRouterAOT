using System.Runtime.InteropServices;

namespace BrowseRouter.Host.Interop;

/// <summary>
/// P/Invoke wrappers for <c>user32.dll</c>. Only what the Host actually uses —
/// window creation for the tray callback target + toast popups, the message
/// loop, popup menus, and a small GDI/text-painting surface for the toast.
/// </summary>
internal static partial class User32
{
    public const int WmDestroy = 0x0002;
    public const int WmClose = 0x0010;
    public const int WmQuit = 0x0012;
    public const int WmCommand = 0x0111;
    public const int WmUser = 0x0400;

    public const int WmLbuttonup = 0x0202;
    public const int WmRbuttonup = 0x0205;
    public const int WmContextmenu = 0x007B;

    /// <summary>
    /// Special HWND meaning "message-only" parent in CreateWindowEx.
    /// </summary>
    public static readonly IntPtr HwndMessage = new(-3);

    /// <summary>
    /// TrackPopupMenu flags.
    /// </summary>
    public const uint TpmLeftalign = 0x0000;

    public const uint TpmRightbutton = 0x0002;
    public const uint TpmReturncmd = 0x0100;
    public const uint TpmNonotify = 0x0080;

    /// <summary>
    /// AppendMenu flags.
    /// </summary>
    public const uint MfString = 0x00000000;

    public const uint MfSeparator = 0x00000800;
    public const uint MfDisabled = 0x00000002;
    public const uint MfGrayed = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public Point pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct Wndclassex
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc; // pointer to delegate
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    /// <summary>
    /// Window procedure callback delegate signature.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    internal delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    public static partial ushort RegisterClassEx(ref Wndclassex lpwcx);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    public static partial IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string? lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam
    );

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    public static partial int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(ref Msg lpMsg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial IntPtr DispatchMessage(ref Msg lpMsg);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    public static partial void PostQuitMessage(int nExitCode);

    [LibraryImport("user32.dll")]
    public static partial IntPtr CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIdNewItem, string? lpNewItem);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyMenu(IntPtr hMenu);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int TrackPopupMenu(
        IntPtr hMenu,
        uint uFlags,
        int x,
        int y,
        int nReserved,
        IntPtr hWnd,
        IntPtr prcRect
    );

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out Point lpPoint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr CreateIconFromResourceEx(
        byte[] pbIconBits,
        uint cbIconBits,
        [MarshalAs(UnmanagedType.Bool)] bool fIcon,
        uint dwVersion,
        int cxDesired,
        int cyDesired,
        uint uFlags
    );

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW", SetLastError = true)]
    public static partial IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    public const uint LrShared = 0x00008000;
    public const uint LrDefaultsize = 0x00000040;

    // ─── Custom toast popup support ────────────────────────────────────────────
    // Window styles
    public const uint WsPopup = 0x80000000;
    public const uint WsClipSiblings = 0x04000000;
    public const uint WsExTopmost = 0x00000008;
    public const uint WsExToolwindow = 0x00000080;
    public const uint WsExNoactivate = 0x08000000;

    // SetWindowPos flags
    public const uint SwpNosize = 0x0001;
    public const uint SwpNomove = 0x0002;
    public const uint SwpNozorder = 0x0004;
    public const uint SwpNoactivate = 0x0010;
    public const uint SwpShowwindow = 0x0040;

    // HWND_TOPMOST sentinel for SetWindowPos
    public static readonly IntPtr HwndTopmost = new(-1);

    // SystemParametersInfo uiAction
    public const uint SpiGetworkarea = 0x0030;

    // DrawText format flags
    public const uint DtLeft = 0x00000000;
    public const uint DtTop = 0x00000000;
    public const uint DtSingleline = 0x00000020;
    public const uint DtWordbreak = 0x00000010;
    public const uint DtEndEllipsis = 0x00008000;
    public const uint DtNoprefix = 0x00000800;
    public const uint DtEditcontrol = 0x00002000;
    public const uint DtCalcrect = 0x00000400;

    // GetWindowLongPtr / SetWindowLongPtr indices
    public const int GwlpUserdata = -21;

    // WM_* messages used by the toast
    public const int WmPaint = 0x000F;
    public const int WmTimer = 0x0113;
    public const int WmEraseBkgnd = 0x0014;
    public const int WmNcdestroy = 0x0082;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Paintstruct
    {
        public IntPtr Hdc;

        // Win32 BOOL is a 4-byte int. Keep these as int so the whole struct is
        // blittable — LibraryImport's source-gen marshaller requires it (using
        // bool here would force [DisableRuntimeMarshalling] on the assembly).
        public int FErase;
        public Rect RcPaint;
        public int FRestore;
        public int FIncUpdate;

        // BYTE rgbReserved[32]
        public unsafe fixed byte RgbReserved[32];
    }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags
    );

    /// <summary>
    /// Win10 1607+: per-monitor DPI for the given window. Falls back to system DPI
    /// for earlier versions — but the manifest declares PerMonitorV2, so this is
    /// available on every supported target.
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    public static partial uint GetDpiForWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfo(uint uiAction, uint uiParam, ref Rect pvParam, uint fWinIni);

    [LibraryImport("user32.dll", EntryPoint = "SetTimer", SetLastError = true)]
    public static partial UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIdEvent, uint uElapse, IntPtr lpTimerFunc);

    [LibraryImport("user32.dll", EntryPoint = "KillTimer", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool KillTimer(IntPtr hWnd, UIntPtr uIdEvent);

    [LibraryImport("user32.dll", EntryPoint = "BeginPaint")]
    public static partial IntPtr BeginPaint(IntPtr hWnd, out Paintstruct lpPaint);

    [LibraryImport("user32.dll", EntryPoint = "EndPaint")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndPaint(IntPtr hWnd, ref Paintstruct lpPaint);

    [LibraryImport("user32.dll", EntryPoint = "FillRect")]
    public static partial int FillRect(IntPtr hDc, ref Rect lprc, IntPtr hbr);

    [LibraryImport("user32.dll", EntryPoint = "DrawTextW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int DrawText(IntPtr hDc, string lpchText, int cchText, ref Rect lprc, uint format);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowRgn")]
    public static partial int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);

    [LibraryImport("user32.dll", EntryPoint = "InvalidateRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, [MarshalAs(UnmanagedType.Bool)] bool bErase);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [LibraryImport("user32.dll", EntryPoint = "GetSystemMetrics")]
    public static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW", SetLastError = true)]
    public static partial IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// Predefined IDC_ARROW cursor id (cast from int 32512).
    /// </summary>
    public static readonly IntPtr IdcArrow = new(32512);

    /// <summary>
    /// Predefined IDI_APPLICATION icon id (cast from int 32512).
    /// </summary>
    public static readonly IntPtr IdiApplication = new(32512);
}