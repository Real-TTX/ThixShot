using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;

namespace ThixShot.Capture;

internal sealed class WindowsDesktop : IDisposable
{
    private const int SourceCopyWithLayers = 0x00CC0020 | 0x40000000;
    private const int VirtualLeft = 76, VirtualTop = 77, VirtualWidth = 78, VirtualHeight = 79;
    private readonly nint previousDpiContext;
    private nint screen, memory, bitmap, previousBitmap;
    private nint previousCursor;
    private IconInfo cursorIcon;
    private int cursorWidth, cursorHeight;
    public CaptureRegion Region { get; }
    public int Width { get; }
    public int Height { get; }
    public nint Pixels { get; }

    public WindowsDesktop(CaptureRegion? selectedRegion, bool video) : this(selectedRegion, video, virtualDesktop: false) { }

    private WindowsDesktop(CaptureRegion? selectedRegion, bool video, bool virtualDesktop)
    {
        // GetSystemMetrics and GDI must both see the same physical coordinate space, including
        // monitors to the left or above the primary display, whatever their DPI scale.
        previousDpiContext = SetThreadDpiAwarenessContext(-4);
        if (previousDpiContext == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Die Bildschirmauflösung konnte nicht ermittelt werden.");
        try
        {
            var bounds = virtualDesktop
                ? new CaptureRegion(GetSystemMetrics(VirtualLeft), GetSystemMetrics(VirtualTop), GetSystemMetrics(VirtualWidth), GetSystemMetrics(VirtualHeight))
                : new CaptureRegion(0, 0, GetSystemMetrics(0), GetSystemMetrics(1));
            Region = selectedRegion ?? bounds;
            if (!bounds.Contains(Region)) throw new ArgumentException("Der Aufnahmebereich liegt außerhalb des verfügbaren Bildschirms.");
            double scale = Math.Min(1, Math.Min(1920d / Region.Width, 1080d / Region.Height));
            Width = video ? Math.Max(2, (int)(Region.Width * scale) / 2 * 2) : Region.Width;
            Height = video ? Math.Max(2, (int)(Region.Height * scale) / 2 * 2) : Region.Height;
            screen = GetDC(0);
            memory = CreateCompatibleDC(screen);
            var header = new BitmapHeader { Size = 40, Width = Width, Height = -Height, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(screen, ref header, 0, out var pixels, 0, 0);
            if (screen == 0 || memory == 0 || bitmap == 0 || pixels == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Der Bildschirm konnte nicht geöffnet werden.");
            previousBitmap = SelectObject(memory, bitmap);
            if (previousBitmap is 0 or -1)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Pixels = pixels;
            SetStretchBltMode(memory, 4); // HALFTONE for downscaling a high-DPI desktop
            SetBrushOrgEx(memory, 0, 0, 0);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static void CheckAccess()
    {
        nint desktop = OpenInputDesktop(0, false, 0x0001); // DESKTOP_READOBJECTS
        if (desktop == 0)
            throw new UnauthorizedAccessException("Der angemeldete Desktop ist gesperrt oder für die Aufnahme nicht zugänglich.");
        try
        {
            var activeName = DesktopName(desktop);
            var threadName = DesktopName(GetThreadDesktop(GetCurrentThreadId()));
            if (!string.Equals(activeName, threadName, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Windows hat auf einen anderen Desktop gewechselt.");
        }
        finally { CloseDesktop(desktop); }
    }

    private static string DesktopName(nint handle)
    {
        var name = new StringBuilder(256);
        if (handle == 0 || !GetUserObjectInformationW(handle, 2, name, name.Capacity * 2, out _))
            throw new UnauthorizedAccessException("Windows verweigert den Zugriff auf den angemeldeten Desktop.");
        return name.ToString();
    }

    public static CaptureRegion SaveScreenshot(string path, bool virtualDesktop)
    {
        using var desktop = new WindowsDesktop(null, video: false, virtualDesktop);
        CheckAccess();
        if (!BitBlt(desktop.memory, 0, 0, desktop.Width, desktop.Height, desktop.screen, desktop.Region.X, desktop.Region.Y, SourceCopyWithLayers))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows konnte das Bildschirmbild nicht kopieren.");
        GdiFlush();
        var info = new SKImageInfo(desktop.Width, desktop.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var pixels = new SKPixmap(info, desktop.Pixels, info.RowBytes);
        using var image = SKImage.FromPixels(pixels);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100) ?? throw new IOException("Das PNG konnte nicht kodiert werden.");
        using var output = File.Create(path);
        data.SaveTo(output);
        return desktop.Region;
    }

    public void CopyFrame(CaptureRegion viewport, bool showCursor)
    {
        CheckAccess();
        if (!StretchBlt(memory, 0, 0, Width, Height, screen, viewport.X, viewport.Y, viewport.Width,
                viewport.Height, SourceCopyWithLayers))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows hat die Bildschirmaufnahme unterbrochen.");
        if (showCursor)
            DrawCursor(viewport);
        GdiFlush();
    }

    private void DrawCursor(CaptureRegion viewport)
    {
        var cursor = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref cursor) || (cursor.Flags & 1) == 0 || cursor.Handle == 0) return;
        if (previousCursor != cursor.Handle)
        {
            if (!GetIconInfo(cursor.Handle, out cursorIcon)) return;
            try
            {
                cursorWidth = GetSystemMetrics(13);
                cursorHeight = GetSystemMetrics(14);
                var source = cursorIcon.Color == 0 ? cursorIcon.Mask : cursorIcon.Color;
                if (GetObjectW(source, Marshal.SizeOf<NativeBitmap>(), out var dimensions) != 0)
                {
                    cursorWidth = dimensions.Width;
                    cursorHeight = cursorIcon.Color == 0 ? dimensions.Height / 2 : dimensions.Height;
                }
                previousCursor = cursor.Handle;
            }
            finally
            {
                if (cursorIcon.Color != 0) DeleteObject(cursorIcon.Color);
                if (cursorIcon.Mask != 0) DeleteObject(cursorIcon.Mask);
            }
        }
        double sx = Width / (double)viewport.Width, sy = Height / (double)viewport.Height;
        DrawIconEx(memory, (int)((cursor.X - cursorIcon.HotX - viewport.X) * sx),
            (int)((cursor.Y - cursorIcon.HotY - viewport.Y) * sy), cursor.Handle,
            Math.Max(1, (int)(cursorWidth * sx)), Math.Max(1, (int)(cursorHeight * sy)), 0, 0, 3);
    }

    public void Dispose()
    {
        if (memory != 0 && previousBitmap != 0 && previousBitmap != -1) SelectObject(memory, previousBitmap);
        if (bitmap != 0) DeleteObject(bitmap);
        if (memory != 0) DeleteDC(memory);
        if (screen != 0) ReleaseDC(0, screen);
        if (previousDpiContext != 0) SetThreadDpiAwarenessContext(previousDpiContext);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPerMeter, YPerMeter;
        public uint ColorsUsed, ImportantColors;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPerPixel; public nint Bits; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo { public int Size, Flags; public nint Handle; public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo { public int IsIcon, HotX, HotY; public nint Mask, Color; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct PointerPosition { public int X, Y; }

    [DllImport("user32", SetLastError = true)] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32", SetLastError = true)] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("kernel32")] private static extern uint GetCurrentThreadId();
    [DllImport("user32")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool GetUserObjectInformationW(nint obj, int index, StringBuilder value, int bytes, out int required);
    [DllImport("user32")] private static extern int GetSystemMetrics(int metric);
    [DllImport("user32")] private static extern nint GetDC(nint window);
    [DllImport("user32")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32")] internal static extern bool GetCursorPos(out PointerPosition position);
    internal static bool IsThixShotWindowAt(PointerPosition position)
    {
        nint handle = WindowFromPoint(position);
        if (handle == 0) return false;
        GetWindowThreadProcessId(handle, out var pid);
        return pid == Environment.ProcessId;
    }
    [DllImport("user32")] private static extern nint WindowFromPoint(PointerPosition point);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(nint handle, out uint process);
    [DllImport("user32")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32")] private static extern bool GetCursorInfo(ref CursorInfo cursor);
    [DllImport("user32")] private static extern bool GetIconInfo(nint icon, out IconInfo info);
    [DllImport("user32")] private static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);
    [DllImport("gdi32")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32")] private static extern int GetObjectW(nint obj, int bytes, out NativeBitmap data);
    [DllImport("gdi32", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapHeader header, uint usage, out nint pointer, nint section, uint offset);
    [DllImport("gdi32", SetLastError = true)] private static extern bool StretchBlt(nint to, int x, int y, int w, int h, nint from, int sx, int sy, int sw, int sh, int operation);
    [DllImport("gdi32", SetLastError = true)] private static extern bool BitBlt(nint to, int x, int y, int w, int h, nint from, int sx, int sy, int operation);
    [DllImport("gdi32")] private static extern int SetStretchBltMode(nint dc, int mode);
    [DllImport("gdi32")] private static extern bool SetBrushOrgEx(nint dc, int x, int y, nint previous);
    [DllImport("gdi32")] private static extern bool GdiFlush();
}
