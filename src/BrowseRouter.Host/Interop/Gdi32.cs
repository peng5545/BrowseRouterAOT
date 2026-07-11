using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace BrowseRouter.Host.Interop;

/// <summary>
/// P/Invoke wrappers for <c>gdi32.dll</c>. Limited to what the self-drawn toast
/// popup needs — solid brushes, fonts, regions, text colours. AOT-friendly via
/// source generation.
/// </summary>
internal static partial class Gdi32
{
    // CreateFontW weights
    public const int FwNormal = 400;
    public const int FwBold = 700;

    // CreateFontW charset / precision / quality / family — all "default-ish"
    public const uint DefaultCharset = 1;
    public const uint OutDefaultPrecis = 0;
    public const uint ClipDefaultPrecis = 0;
    public const uint CleartypeQuality = 5;
    public const uint DefaultPitch = 0;
    public const uint FfDontcare = 0;

    // SetBkMode constants
    public const int Transparent = 1;

    /// <summary>
    /// Pack an RGB byte triplet into the COLORREF (0x00BBGGRR) layout GDI expects.
    /// </summary>
    public static uint Rgb(byte r, byte g, byte b) => (uint) (r | (g << 8) | (b << 16));

    [LibraryImport("gdi32.dll", EntryPoint = "CreateSolidBrush")]
    public static partial IntPtr CreateSolidBrush(uint colorRef);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(IntPtr hObject);

    [LibraryImport("gdi32.dll", EntryPoint = "SelectObject")]
    public static partial IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [LibraryImport("gdi32.dll", EntryPoint = "SetTextColor")]
    public static partial uint SetTextColor(IntPtr hdc, uint colorRef);

    [LibraryImport("gdi32.dll", EntryPoint = "SetBkMode")]
    public static partial int SetBkMode(IntPtr hdc, int mode);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateFont(
        int cHeight,
        int cWidth,
        int cEscapement,
        int cOrientation,
        int cWeight,
        uint bItalic,
        uint bUnderline,
        uint bStrikeOut,
        uint iCharSet,
        uint iOutPrecision,
        uint iClipPrecision,
        uint iQuality,
        uint iPitchAndFamily,
        string pszFaceName
    );

    [LibraryImport("gdi32.dll", EntryPoint = "CreateRoundRectRgn")]
    public static partial IntPtr CreateRoundRectRgn(
        int nLeftRect,
        int nTopRect,
        int nRightRect,
        int nBottomRect,
        int nWidthEllipse,
        int nHeightEllipse
    );
}

/// <summary>
/// <see cref="SafeHandle"/> wrapper for a GDI object (brush, font, region, etc.).
/// Calls <see cref="Gdi32.DeleteObject"/> on release. AOT-compatible.
/// <para>
/// Critical finalizers (SafeHandle) run in a separate pass AFTER regular
/// finalizers, so they never back up the regular finalization queue. This is
/// what distinguishes them from a regular ~ destructor and makes them safe
/// to use on types whose finalization must not delay Thread object cleanup.
/// </para>
/// </summary>
internal sealed class SafeGdiObjectHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeGdiObjectHandle() : base(ownsHandle: true)
    {
    }

    public SafeGdiObjectHandle(IntPtr existingHandle) : base(ownsHandle: true)
    {
        SetHandle(existingHandle);
    }

    protected override bool ReleaseHandle()
    {
        return Gdi32.DeleteObject(handle);
    }
}