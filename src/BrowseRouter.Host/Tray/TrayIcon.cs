using BrowseRouter.Core;
using BrowseRouter.Host.Interop;
using BrowseRouter.Host.Logging;
using BrowseRouter.Host.Notify;
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
    /// The argument is the tray window's HWND — useful because most subscribers
    /// pass it to <c>TrackPopupMenu</c> as the owner, and threading it through
    /// the event lets subscribers avoid capturing the tray in a closure.
    /// </summary>
    public event Action<IntPtr>? OnTrayRightClick;

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
        var ownedIcon = IconLoader.LoadEmbedded(0, 0, shared: true, defaultSize: true, _log);
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
            uVersion = Shell32.NotifyiconVersion4
        };
        _nid.SetTip(Constants.AppName);

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
                var eventMsg = unchecked((uint) (lParam.ToInt64() & 0xFFFF));
                if (eventMsg is User32.WmRbuttonup or User32.WmContextmenu)
                {
                    try
                    {
                        OnTrayRightClick?.Invoke(hWnd);
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
                var cmdId = unchecked((int) (wParam.ToInt64() & 0xFFFF));
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
}