using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace ThixShot.Editor;

internal static class SelectionOverlayPlacement
{
    public static void Cover(Window window, PixelRect bounds)
    {
        var handle = window.TryGetPlatformHandle();
        if (handle is { HandleDescriptor: "HWND" } && OperatingSystem.IsWindows())
        {
            // Set the final physical bounds after Avalonia has assigned the destination monitor's DPI.
            // HWND_TOPMOST also covers a taskbar configured to stay on top.
            if (!SetWindowPos(handle.Handle, -1, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        else if (handle is { HandleDescriptor: "NSWindow" } && OperatingSystem.IsMacOS())
        {
            // Only the primary video display uses this picker on macOS. AppKit uses its bottom-left corner as (0,0).
            // Borderless normal-size windows skip the macOS full-screen animation and its separate Space.
            var nsWindow = handle.Handle;
            SetInteger(nsWindow, Selector("setCollectionBehavior:"), (1 << 0) | (1 << 8)); // all Spaces, full-screen auxiliary
            SetInteger(nsWindow, Selector("setLevel:"), CGWindowLevelForKey(13)); // screen-saver level, above the menu bar and Dock
            SetFrame(nsWindow, Selector("setFrame:display:"), new MacRect(0, 0, bounds.Width, bounds.Height), true);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct MacRect(double X, double Y, double Width, double Height);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern int CGWindowLevelForKey(int key);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName", CharSet = CharSet.Ansi)]
    private static extern nint Selector(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SetInteger(nint receiver, nint selector, nint value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SetFrame(nint receiver, nint selector, MacRect frame, [MarshalAs(UnmanagedType.I1)] bool display);
}
