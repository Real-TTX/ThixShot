using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace ThixShot.Editor;

internal sealed class ImageSurface : Control, IDisposable
{
    private readonly ImageDocument document;
    private Bitmap preview;
    private Point? dragStart;
    private Point dragEnd;
    public MarkTool Tool { get; set; } = MarkTool.Highlight;
    public SKColor MarkColor { get; set; } = SKColor.Parse("#FFB729");
    public Action<ImageMark>? Submitted { get; set; }

    public ImageSurface(ImageDocument document)
    {
        this.document = document;
        Cursor = new Cursor(StandardCursorType.Cross);
        ClipToBounds = true;
        preview = MakePreview();
    }

    private Bitmap MakePreview()
    {
        using var data = ImageEditor.Render(document);
        using var stream = data.AsStream();
        return new Bitmap(stream);
    }

    public void Refresh()
    {
        var next = MakePreview();
        preview.Dispose();
        preview = next;
        InvalidateVisual();
    }

    internal Rect ImageRect
    {
        get
        {
            var area = document.Visible;
            var scale = Math.Min(Bounds.Width / area.Width, Bounds.Height / area.Height);
            var w = area.Width * scale;
            var h = area.Height * scale;
            return new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);
        }
    }

    private SKPoint ToImage(Point point)
    {
        var dst = ImageRect;
        var area = document.Visible;
        return new SKPoint((float)(area.Left + (point.X - dst.X) * area.Width / dst.Width),
            (float)(area.Top + (point.Y - dst.Y) * area.Height / dst.Height));
    }

    private Point Clamp(Point point) => new(Math.Clamp(point.X, ImageRect.X, ImageRect.Right),
        Math.Clamp(point.Y, ImageRect.Y, ImageRect.Bottom));

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetPosition(this);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !ImageRect.Contains(point)) return;
        dragStart = dragEnd = point;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (!dragStart.HasValue) return;
        dragEnd = Clamp(e.GetPosition(this));
        InvalidateVisual();
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (dragStart is not { } start) return;
        var mark = new ImageMark(Tool, ToImage(start), ToImage(Clamp(e.GetPosition(this))), MarkColor);
        dragStart = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
        Submitted?.Invoke(mark);
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        dragStart = null;
        InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        context.DrawImage(preview, ImageRect);
        if (dragStart is not { } start) return;
        var color = Color.FromArgb(255, MarkColor.Red, MarkColor.Green, MarkColor.Blue);
        var pen = new Pen(Tool == MarkTool.Crop ? Brushes.White : new SolidColorBrush(color), 2,
            Tool == MarkTool.Crop ? DashStyle.Dash : null);
        if (Tool == MarkTool.Arrow) context.DrawLine(pen, start, dragEnd);
        else context.DrawRectangle(Tool == MarkTool.Highlight ? new SolidColorBrush(color, .35) : null,
            pen, new Rect(start, dragEnd).Normalize());
    }
    public void Dispose() => preview.Dispose();
}
