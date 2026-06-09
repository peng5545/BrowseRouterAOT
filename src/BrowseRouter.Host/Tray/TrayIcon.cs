using BrowseRouter.Core;
using BrowseRouter.Host.Interop;
using BrowseRouter.Host.Logging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace BrowseRouter.Host.Tray;

/// <summary>
/// Owns a message-only window + a system-tray icon. Pumps Win32 messages on its
/// own STA thread so the rest of the Host stays free to run async pipe work.
/// Fires <see cref="OnTrayRightClick"/> and <see cref="OnMenuCommand"/> callbacks
/// on the message thread; callers should not block them.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = User32.WmUser + 1;
    private const uint IconId = 1;

    private const string WindowClassName = $"{Constants.AppId}.TrayWindow";

    private readonly FileLogger _log;
    private readonly User32.WndProcDelegate _wndProcKeepAlive;
    private readonly ManualResetEventSlim _ready = new(false);

    private Thread? _thread;
    private ushort _classAtom;
    private Shell32.Notifyicondataw _nid;
    private bool _iconAdded;

    /// <summary>
    /// True iff the HICON currently stored in <see cref="_nid"/>.hIcon was created by
    /// us via <c>CreateIconFromResourceEx</c> and therefore must be released with
    /// <c>DestroyIcon</c>. <c>LoadIcon(IDI_APPLICATION)</c> returns a shared system
    /// handle that we must NOT destroy.
    /// </summary>
    private bool _iconIsOwned;

    /// <summary>
    /// Raised when the user right-clicks the tray icon (or presses menu key).
    /// </summary>
    public event Action? OnTrayRightClick;

    /// <summary>
    /// Raised when a popup-menu item is chosen. Argument is the item id.
    /// </summary>
    public event Action<int>? OnMenuCommand;

    public TrayIcon(FileLogger log)
    {
        _log = log;
        _wndProcKeepAlive = WndProc; // keep delegate rooted for the process lifetime
    }

    /// <summary>
    /// Window handle of the hidden message-only window. Valid after <see cref="Start"/>.
    /// </summary>
    public IntPtr WindowHandle { get; private set; }

    /// <summary>
    /// Spin up the message thread and create the tray icon. Blocks until ready.
    /// </summary>
    public void Start()
    {
        if (_thread is not null)
            return;
        _thread = new Thread(MessageLoop)
        {
            IsBackground = false,
            Name = "BrowseRouterAOT.TrayMessageLoop"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>
    /// Show a balloon notification on the tray icon. On Windows 10 the Action Center
    /// uses this; on Windows 11 it renders as a native toast.
    /// </summary>
    public void ShowBalloon(string title, string message)
    {
        if (!_iconAdded)
            return;
        var nid = _nid;
        nid.uFlags = Shell32.NifInfo | Shell32.NifIcon | Shell32.NifMessage | Shell32.NifTip;
        nid.SetInfoTitle(Truncate(title, 63));
        nid.SetInfo(Truncate(message, 255));
        nid.dwInfoFlags = Shell32.NiifUser | Shell32.NiifLargeIcon;
        if (!Shell32.Shell_NotifyIcon(Shell32.NimModify, ref nid))
        {
            _log.Warn("Shell_NotifyIcon NIM_MODIFY (balloon) failed.");
        }
    }

    /// <summary>
    /// Tell the message loop to exit. Blocks until the thread finishes.
    /// </summary>
    public void Dispose()
    {
        if (WindowHandle != IntPtr.Zero)
        {
            User32.PostMessage(WindowHandle, User32.WmClose, IntPtr.Zero, IntPtr.Zero);
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    private void MessageLoop()
    {
        try
        {
            RegisterWindowClass();
            CreateHiddenWindow();
            AddTrayIcon();
            _ready.Set();

            while (User32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                User32.TranslateMessage(ref msg);
                User32.DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Tray message loop crashed", ex);
            _ready.Set(); // unblock Start() even on failure
        }
        finally
        {
            RemoveTrayIcon();
            if (WindowHandle != IntPtr.Zero)
                User32.DestroyWindow(WindowHandle);
            if (_classAtom != 0)
                User32.UnregisterClass(WindowClassName, Kernel32.GetModuleHandle(null));
        }
    }

    private void RegisterWindowClass()
    {
        var hInstance = Kernel32.GetModuleHandle(null);
        var classNamePtr = Marshal.StringToHGlobalUni(WindowClassName);
        try
        {
            var wc = new User32.Wndclassex
            {
                cbSize = (uint) Marshal.SizeOf<User32.Wndclassex>(),
                style = 0,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive),
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = hInstance,
                hIcon = User32.LoadIcon(IntPtr.Zero, User32.IdiApplication),
                hCursor = User32.LoadCursor(IntPtr.Zero, User32.IdcArrow),
                hbrBackground = IntPtr.Zero,
                lpszMenuName = IntPtr.Zero,
                lpszClassName = classNamePtr,
                hIconSm = IntPtr.Zero
            };
            _classAtom = User32.RegisterClassEx(ref wc);
            if (_classAtom == 0)
                throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            Marshal.FreeHGlobal(classNamePtr);
        }
    }

    private void CreateHiddenWindow()
    {
        WindowHandle = User32.CreateWindowEx(0, WindowClassName, Constants.AppName, 0, 0, 0, 0, 0,
            User32.HwndMessage, // message-only window
            IntPtr.Zero, Kernel32.GetModuleHandle(null), IntPtr.Zero);
        if (WindowHandle == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
    }

    private void AddTrayIcon()
    {
        var ownedIcon = LoadIconFromPngResource();
        // Fall back to the system application icon (shared handle — do NOT destroy).
        var sharedIcon = User32.LoadIcon(IntPtr.Zero, User32.IdiApplication);
        var hIcon = ownedIcon != IntPtr.Zero ? ownedIcon : sharedIcon;
        _iconIsOwned = ownedIcon != IntPtr.Zero;

        _nid = new Shell32.Notifyicondataw
        {
            cbSize = (uint) Marshal.SizeOf<Shell32.Notifyicondataw>(),
            hWnd = WindowHandle,
            uID = IconId,
            uFlags = Shell32.NifMessage | Shell32.NifIcon | Shell32.NifTip | Shell32.NifShowtip,
            uCallbackMessage = CallbackMessage,
            hIcon = hIcon,
            uVersion = Shell32.NotifyiconVersion4,
            hBalloonIcon = hIcon
        };
        _nid.SetTip(Constants.AppName);
        _nid.SetInfo(string.Empty);
        _nid.SetInfoTitle(string.Empty);

        if (!Shell32.Shell_NotifyIcon(Shell32.NimAdd, ref _nid))
        {
            _log.Warn("Shell_NotifyIcon NIM_ADD failed.");
            if (_iconIsOwned && ownedIcon != IntPtr.Zero)
                User32.DestroyIcon(ownedIcon);
            _iconIsOwned = false;
            return;
        }

        if (!Shell32.Shell_NotifyIcon(Shell32.NimSetversion, ref _nid))
        {
            _log.Warn("Shell_NotifyIcon NIM_SETVERSION failed.");
        }

        _iconAdded = true;
    }

    private IntPtr LoadIconFromPngResource()
    {
        try
        {
            var assembly = typeof(TrayIcon).Assembly;
            using var stream = assembly.GetManifestResourceStream("BrowseRouter.Host.Resources.icon.png");
            if (stream == null)
            {
                _log.Warn(
                    "Embedded icon resource 'BrowseRouter.Host.Resources.icon.png' not found; falling back to system icon.");
                return IntPtr.Zero;
            }

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();

            // 0x00030000 is the magic version for CreateIconFromResourceEx
            return User32.CreateIconFromResourceEx(bytes, (uint) bytes.Length, true, 0x00030000, 0, 0,
                User32.LrShared | User32.LrDefaultsize);
        }
        catch (Exception ex)
        {
            _log.Warn($"Native PNG to Icon conversion failed: {ex.Message}");
            return IntPtr.Zero;
        }
    }

    private void RemoveTrayIcon()
    {
        if (!_iconAdded)
            return;

        // Snapshot ownership before NIM_DELETE so we can release the right handle
        // after the system has forgotten about it. _iconIsOwned distinguishes our
        // CreateIcon* HICON (must DestroyIcon) from a shared system icon (must NOT).
        var wasOwned = _iconIsOwned;
        var ownedIcon = wasOwned ? _nid.hIcon : IntPtr.Zero;

        Shell32.Shell_NotifyIcon(Shell32.NimDelete, ref _nid);
        _iconAdded = false;
        _iconIsOwned = false;

        if (wasOwned && ownedIcon != IntPtr.Zero)
        {
            User32.DestroyIcon(ownedIcon);
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case CallbackMessage:
            {
                uint eventMsg = unchecked((uint) (lParam.ToInt64() & 0xFFFF));
                if (eventMsg is User32.WmRbuttonup or User32.WmContextmenu)
                {
                    try
                    {
                        OnTrayRightClick?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        _log.Error("OnTrayRightClick handler", ex);
                    }
                }

                return IntPtr.Zero;
            }

            case User32.WmCommand:
            {
                int cmdId = unchecked((int) (wParam.ToInt64() & 0xFFFF));
                try
                {
                    OnMenuCommand?.Invoke(cmdId);
                }
                catch (Exception ex)
                {
                    _log.Error("OnMenuCommand handler", ex);
                }

                return IntPtr.Zero;
            }

            case User32.WmClose:
                User32.DestroyWindow(hWnd);
                return IntPtr.Zero;

            case User32.WmDestroy:
                User32.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return User32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty : s.Length <= max ? s : s[..max];
}