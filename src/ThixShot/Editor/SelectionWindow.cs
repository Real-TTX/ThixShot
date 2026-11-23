using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;
using ThixShot.Capture;

namespace ThixShot.Editor;

internal sealed class SelectionWindow : Window
{
    private readonly SelectionGesture gesture;
    private readonly SelectionSurface surface;
    private readonly Border hint;
    private readonly Screen monitor;
    private IPointer? grabbed;

    private SelectionWindow(SelectionGesture gesture, Screen monitor, Bitmap bitmap, SKRectI source)
    {
        this.gesture = gesture;
        this.monitor = monitor;
        Title = "Aufnahmebereich auswählen · ThixShot";
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = monitor.IsPrimary;
        CanResize = CanMinimize = CanMaximize = false;
        SystemDecorations = SystemDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = monitor.Bounds.Position;
        Width = monitor.Bounds.Width / monitor.Scaling;
        Height = monitor.Bounds.Height / monitor.Scaling;
        Background = Brushes.Black;
        Cursor = new Cursor(StandardCursorType.Cross);
        surface = new SelectionSurface(bitmap, source, gesture) { Focusable = true };
        hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(225, 24, 28, 35)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 7), Margin = new Thickness(0, 20, 0, 0),
            VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center,
            IsHitTestVisible = false, IsVisible = monitor.IsPrimary,
            Child = new TextBlock { Text = "Bereich aufziehen  ·  Esc zum Abbrechen", Foreground = Brushes.White, FontSize = 13 }
        };
        Content = new Grid { Children = { surface, hint } };
        Opened += (_, _) => SelectionOverlayPlacement.Cover(this, monitor.Bounds);
        Closed += (_, _) =>
        {
            gesture.Result.TrySetResult(null);
            bitmap.Dispose();
        };
    }

    internal static async Task<SKRectI?> SelectAsync(Screens screens, string frozenPng, CaptureRegion? desktopBounds, CancellationToken token)
    {
        Dispatcher.UIThread.VerifyAccess();
        token.ThrowIfCancellationRequested();
        var primary = screens.Primary ?? throw new InvalidOperationException("Der Hauptbildschirm ist nicht verfügbar.");
        var monitors = desktopBounds.HasValue
            ? screens.All.OrderByDescending(screen => screen.IsPrimary).DistinctBy(screen => screen.Bounds).OrderBy(screen => screen.IsPrimary).ToArray()
            : [primary];
        if (desktopBounds is { } desktop && monitors.Select(monitor => monitor.Bounds).Aggregate((a, b) => a.Union(b)) !=
            new PixelRect(desktop.X, desktop.Y, desktop.Width, desktop.Height))
            throw new InvalidOperationException("Die Bildschirmanordnung hat sich geändert. Bitte den Bereich erneut auswählen.");

        var windows = new List<SelectionWindow>();
        var gesture = new SelectionGesture(windows);
        void DisplayChanged(object? sender, EventArgs args) => gesture.Result.TrySetResult(null);
        screens.Changed += DisplayChanged;
        using var cancellation = token.Register(() => gesture.Result.TrySetCanceled(token));
        try
        {
            using (var frozen = await Task.Run(() => SKBitmap.Decode(frozenPng) ??
                throw new InvalidDataException("Das Bildschirmfoto konnte nicht gelesen werden."), token))
            {
                gesture.Size = new PixelSize(frozen.Width, frozen.Height);
                foreach (var screen in monitors)
                {
                    var tile = SourceTile(screen.Bounds, desktopBounds, gesture.Size);
                    using var subset = new SKBitmap();
                    if (!frozen.ExtractSubset(subset, tile)) throw new InvalidDataException("Der Monitor fehlt im Bildschirmfoto.");
                    using var pixels = subset.Copy(SKColorType.Bgra8888);
                    var image = new Bitmap(PixelFormats.Bgra8888, AlphaFormat.Opaque, pixels.GetPixels(),
                        new PixelSize(tile.Width, tile.Height), new Vector(96, 96), pixels.RowBytes);
                    windows.Add(new SelectionWindow(gesture, screen, image, tile));
                }
            }

            if (!gesture.Result.Task.IsCompleted)
            {
                foreach (var window in windows) window.Show();
                windows[^1].Activate();
                windows[^1].surface.Focus();
            }
            return await gesture.Result.Task;
        }
        finally
        {
            screens.Changed -= DisplayChanged;
            foreach (var window in windows) window.Close();
        }
    }

    internal static SKRectI SourceTile(PixelRect screen, CaptureRegion? desktopBounds, PixelSize image)
    {
        if (desktopBounds is not { } desktop) return SKRectI.Create(image.Width, image.Height);
        var tile = SKRectI.Create(screen.X - desktop.X, screen.Y - desktop.Y, screen.Width, screen.Height);
        if (image.Width != desktop.Width || image.Height != desktop.Height || tile.Left < 0 || tile.Top < 0 ||
            tile.Right > image.Width || tile.Bottom > image.Height)
            throw new InvalidOperationException("Die Bildschirmanordnung stimmt nicht mit dem Bildschirmfoto überein.");
        return tile;
    }

    internal static Point SourcePoint(SKRectI tile, Size client, Point position) =>
        // A DPI conversion can otherwise put an exact pixel infinitesimally on the wrong side of Floor/Ceiling.
        new(tile.Left + Math.Round(position.X * tile.Width / client.Width, 7),
            tile.Top + Math.Round(position.Y * tile.Height / client.Height, 7));

    internal static SKRectI Selection(Point start, Point end, PixelSize image)
    {
        // Include the final column/row when the pointer reaches the desktop edge.
        double right = Math.Max(start.X, end.X), bottom = Math.Max(start.Y, end.Y);
        return new SKRectI(
            (int)Math.Clamp(Math.Floor(Math.Min(start.X, end.X)), 0, image.Width),
            (int)Math.Clamp(Math.Floor(Math.Min(start.Y, end.Y)), 0, image.Height),
            right >= image.Width - 1 ? image.Width : (int)Math.Clamp(Math.Ceiling(right), 0, image.Width),
            bottom >= image.Height - 1 ? image.Height : (int)Math.Clamp(Math.Ceiling(bottom), 0, image.Height));
    }

    private sealed class SelectionGesture(List<SelectionWindow> windows)
    {
        public readonly TaskCompletionSource<SKRectI?> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PixelSize Size;
        public Point? Start;
        public Point End;
        public SKRectI? Area => Start is { } start ? Selection(start, End, Size) : null;

        public void Redraw()
        {
            foreach (var window in windows)
            {
                window.hint.IsVisible = Start is null && window.monitor.IsPrimary;
                window.surface.InvalidateVisual();
            }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Escape) return;
        gesture.Result.TrySetResult(null);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (gesture.Start.HasValue || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        grabbed = e.Pointer;
        grabbed.Capture(this);
        gesture.Start = gesture.End = surface.ToImage(e.GetPosition(surface));
        gesture.Redraw();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Pointer != grabbed) return;
        // Captured coordinates continue past this window, including onto a monitor with a different DPI.
        gesture.End = surface.ToImage(e.GetPosition(surface));
        gesture.Redraw();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.Pointer != grabbed || e.InitialPressMouseButton != MouseButton.Left) return;
        gesture.End = surface.ToImage(e.GetPosition(surface));
        var selected = gesture.Area;
        grabbed = null;
        e.Pointer.Capture(null);
        gesture.Start = null;
        if (selected is { Width: >= 2, Height: >= 2 }) gesture.Result.TrySetResult(selected);
        gesture.Redraw();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (grabbed != e.Pointer) return;
        grabbed = null;
        gesture.Result.TrySetResult(null);
    }

    private sealed class SelectionSurface(Bitmap bitmap, SKRectI tile, SelectionGesture gesture) : Control
    {
        private static readonly IBrush Shade = new SolidColorBrush(Color.FromArgb(92, 0, 0, 0));
        private static readonly Pen Outline = new(Brushes.White, 1);
        private static readonly Pen Contrast = new(Brushes.Black, 3);

        public Point ToImage(Point position) => SourcePoint(tile, Bounds.Size, position);

        public override void Render(DrawingContext context)
        {
            var frame = new Rect(Bounds.Size);
            context.DrawImage(bitmap, frame);
            if (gesture.Area is not { Width: > 0, Height: > 0 } area) { context.FillRectangle(Shade, frame); return; }
            double x = frame.Width / tile.Width, y = frame.Height / tile.Height;
            var box = new Rect((area.Left - tile.Left) * x, (area.Top - tile.Top) * y, area.Width * x, area.Height * y);
            var inside = box.Intersect(frame);
            if (inside.Width <= 0 || inside.Height <= 0) { context.FillRectangle(Shade, frame); return; }
            context.FillRectangle(Shade, new Rect(0, 0, frame.Width, inside.Top));
            context.FillRectangle(Shade, new Rect(0, inside.Bottom, frame.Width, frame.Height - inside.Bottom));
            context.FillRectangle(Shade, new Rect(0, inside.Top, inside.Left, inside.Height));
            context.FillRectangle(Shade, new Rect(inside.Right, inside.Top, frame.Width - inside.Right, inside.Height));
            context.DrawRectangle(null, Contrast, box);
            context.DrawRectangle(null, Outline, box);
        }
    }
}
