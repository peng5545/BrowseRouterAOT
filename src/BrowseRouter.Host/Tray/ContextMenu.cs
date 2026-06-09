using BrowseRouter.Host.Interop;
using System.Collections.Generic;

namespace BrowseRouter.Host.Tray;

/// <summary>
/// Builds and shows a Win32 popup menu using <c>TrackPopupMenu</c>. Items are
/// supplied as an ordered list; the chosen item's id is returned (0 = user
/// dismissed the menu without choosing anything).
/// </summary>
internal sealed class ContextMenu
{
    internal sealed record Item(int Id, string Label, bool Enabled = true);

    /// <summary>
    /// A separator pseudo-item: id is unused, label is "-".
    /// </summary>
    public static Item Separator => new(0, "-");

    /// <summary>
    /// Show the menu near <paramref name="windowHandle"/>'s cursor and block until
    /// the user picks something. Returns the chosen id, or 0 on dismiss.
    /// MUST be called on the same thread as the message loop owning the window.
    /// </summary>
    public static int Show(IntPtr windowHandle, IReadOnlyList<Item> items)
    {
        var menu = User32.CreatePopupMenu();
        try
        {
            foreach (var item in items)
            {
                if (ReferenceEquals(item, Separator) || item.Label == "-")
                {
                    User32.AppendMenu(menu, User32.MfSeparator, IntPtr.Zero, null);
                    continue;
                }

                uint flags = User32.MfString;
                if (!item.Enabled)
                    flags |= User32.MfDisabled | User32.MfGrayed;
                User32.AppendMenu(menu, flags, new IntPtr(item.Id), item.Label);
            }

            // The MSDN-recommended dance to ensure the menu closes when the user
            // clicks elsewhere: foreground the owner first.
            User32.SetForegroundWindow(windowHandle);
            User32.GetCursorPos(out var p);

            var picked = User32.TrackPopupMenu(menu,
                User32.TpmLeftalign | User32.TpmRightbutton | User32.TpmReturncmd | User32.TpmNonotify, p.X, p.Y, 0,
                windowHandle, IntPtr.Zero);

            return picked;
        }
        finally
        {
            User32.DestroyMenu(menu);
        }
    }
}