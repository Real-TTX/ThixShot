using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace ThixShot.Editor;

internal sealed class ImageSurface : Control, IDisposable
{
    private readonly ImageDocument document;
    private readonly Bitmap original;
    private readonly bool editorViewport;
    private readonly List<SKPoint> penPoints = [];
    private SKRectI visible;
    private ImageMark[] committed = [];
    private SKPoint? dragStart;
    private SKPoint dragEnd;
    private Point? panAnchor;
    private IPointer? activePointer;
    private Vector offset;
    private double? manualScale;
    private MarkTool tool = MarkTool.Highlight;
    private ImageMark? textPreview;

    public Action<ImageMark>? Submitted { get; set; }
    public Action<SKPoint>? TextRequested { get; set; }
    public Action? ViewChanged { get; set; }
    public SKColor MarkColor { get; set; } = SKColor.Parse("#FFB729");
    public float StrokeWidth { get; set; }
    public MarkTool Tool
    {
        get => tool;
        set
        {
            tool = value;
            Cursor = new Cursor(value switch { MarkTool.Pan => StandardCursorType.Hand, MarkTool.Text => StandardCursorType.Ibeam,
                _ => StandardCursorType.Cross });
        }
    }
    public ImageMark? TextPreview
    {
        set { textPreview = value; InvalidateVisual(); }
    }
    public bool IsFitted => manualScale is null;
    private double RenderScale => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
    public double ZoomPercent => Scale * RenderScale * 100;
    private double MarginSize => editorViewport ? 20 : 0;
    private double Scale => manualScale ?? Math.Max(.0001, Math.Min((Bounds.Width - 2 * MarginSize) / visible.Width,
        (Bounds.Height - 2 * MarginSize) / visible.Height));
    internal SKRectI? CropPreview => dragStart is { } start && Tool == MarkTool.Crop ? ImageEditor.Selection(start, dragEnd, visible) : null;

    public ImageSurface(ImageDocument document, bool editorViewport = true)
    {
        this.document = document;
        this.editorViewport = editorViewport;
        visible = document.Visible;
        StrokeWidth = document.DefaultStroke;
        Tool = MarkTool.Highlight;
        ClipToBounds = true;
        Focusable = editorViewport;
        // One copy of the source pixels. Marks remain vectors while drawing or changing the zoom.
        using var pixels = document.Original.Copy(SKColorType.Bgra8888);
        original = new Bitmap(PixelFormats.Bgra8888,
            pixels.AlphaType == SKAlphaType.Unpremul ? AlphaFormat.Unpremul : AlphaFormat.Premul, pixels.GetPixels(),
            new PixelSize(pixels.Width, pixels.Height), new Vector(96, 96), pixels.RowBytes);
        SizeChanged += (_, _) => { ClampOffset(); ViewChanged?.Invoke(); };
    }

    public void Refresh()
    {
        committed = document.History.Where(mark => mark.Tool != MarkTool.Crop).ToArray();
        if (visible != document.Visible)
        {
            visible = document.Visible;
            Fit();
        }
        InvalidateVisual();
    }

    internal Rect ImageRect
    {
        get
        {
            var size = new Size(visible.Width * Scale, visible.Height * Scale);
            return new Rect(new Point((Bounds.Width - size.Width) / 2 + offset.X,
                (Bounds.Height - size.Height) / 2 + offset.Y), size);
        }
    }

    private SKPoint ToImage(Point point)
    {
        var dst = ImageRect;
        return new SKPoint((float)Math.Clamp(visible.Left + (point.X - dst.Left) / Scale, visible.Left, visible.Right),
            (float)Math.Clamp(visible.Top + (point.Y - dst.Top) / Scale, visible.Top, visible.Bottom));
    }

    public void Fit()
    {
        manualScale = null;
        offset = default;
        Changed();
    }

    public void ZoomTo(double percent, Point? anchor = null)
    {
        if (!editorViewport) return;
        var point = anchor ?? new Point(Bounds.Width / 2, Bounds.Height / 2);
        var source = ToImage(point);
        manualScale = Math.Clamp(percent, 2, 3200) / 100 / RenderScale;
        offset = new Vector(point.X - (Bounds.Width - visible.Width * Scale) / 2 - (source.X - visible.Left) * Scale,
            point.Y - (Bounds.Height - visible.Height * Scale) / 2 - (source.Y - visible.Top) * Scale);
        ClampOffset();
        Changed();
    }

    private void ClampOffset()
    {
        var limitX = Math.Max(0, (visible.Width * Scale - Bounds.Width + MarginSize * 2) / 2);
        var limitY = Math.Max(0, (visible.Height * Scale - Bounds.Height + MarginSize * 2) / 2);
        offset = new Vector(Math.Clamp(offset.X, -limitX, limitX), Math.Clamp(offset.Y, -limitY, limitY));
    }

    private void Changed()
    {
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    public bool CancelGesture()
    {
        if (activePointer is not { } pointer) return false;
        pointer.Capture(null);
        return true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (!editorViewport || dragStart.HasValue || panAnchor.HasValue) return;
        ZoomTo(ZoomPercent * Math.Pow(1.18, e.Delta.Y), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (activePointer is not null) return;
        var point = e.GetPosition(this);
        var buttons = e.GetCurrentPoint(this).Properties;
        if (editorViewport && (buttons.IsMiddleButtonPressed || (buttons.IsLeftButtonPressed && Tool == MarkTool.Pan)))
        {
            panAnchor = point;
            activePointer = e.Pointer;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (!buttons.IsLeftButtonPressed || !ImageRect.Contains(point)) return;
        if (Tool == MarkTool.Text)
        {
            TextRequested?.Invoke(ToImage(point));
            e.Handled = true;
            return;
        }
        dragStart = dragEnd = ToImage(point);
        penPoints.Clear();
        if (Tool == MarkTool.Pen) penPoints.Add(dragEnd);
        activePointer = e.Pointer;
        e.Pointer.Capture(this);
        Changed();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (activePointer != e.Pointer) return;
        var point = e.GetPosition(this);
        if (panAnchor is { } anchor)
        {
            offset += point - anchor;
            panAnchor = point;
            ClampOffset();
            Changed();
            return;
        }
        if (!dragStart.HasValue) return;
        dragEnd = ToImage(point);
        if (Tool == MarkTool.Pen && (penPoints[^1] - dragEnd).Length >= .4f) penPoints.Add(dragEnd);
        Changed();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (activePointer != e.Pointer) return;
        if (panAnchor.HasValue) { panAnchor = null; e.Pointer.Capture(null); return; }
        if (!dragStart.HasValue) return;
        dragEnd = ToImage(e.GetPosition(this));
        if (Tool == MarkTool.Pen && penPoints[^1] != dragEnd) penPoints.Add(dragEnd);
        var mark = DragMark();
        dragStart = null;
        e.Pointer.Capture(null);
        Submitted?.Invoke(mark);
        Changed();
        e.Handled = true;
    }

    private ImageMark DragMark() => new(Tool, dragStart!.Value, dragEnd, MarkColor, StrokeWidth,
        Points: Tool == MarkTool.Pen ? penPoints.ToArray() : null);

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        activePointer = null;
        dragStart = null;
        panAnchor = null;
        Changed();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var image = ImageRect;
        var source = new Rect(visible.Left, visible.Top, visible.Width, visible.Height);
        context.DrawImage(original, source, image);
        var transient = dragStart.HasValue && Tool != MarkTool.Crop ? DragMark() : textPreview;
        if (committed.Length > 0 || transient.HasValue)
            context.Custom(new MarkOperation(image, visible, (float)Scale, committed, transient));
        if (CropPreview is not { } crop) return;
        var x = image.X + (crop.Left-visible.Left) * Scale;
        var y = image.Y + (crop.Top-visible.Top) * Scale;
        var box = new Rect(x, y, crop.Width * Scale, crop.Height * Scale);
        var shade = new SolidColorBrush(Color.FromArgb(115, 0, 0, 0));
        context.FillRectangle(shade, new Rect(image.X, image.Y, image.Width, Math.Max(0, box.Y - image.Y)));
        context.FillRectangle(shade, new Rect(image.X, box.Bottom, image.Width, Math.Max(0, image.Bottom - box.Bottom)));
        context.FillRectangle(shade, new Rect(image.X, box.Y, Math.Max(0, box.X - image.X), box.Height));
        context.FillRectangle(shade, new Rect(box.Right, box.Y, Math.Max(0, image.Right - box.Right), box.Height));
        context.DrawRectangle(null, new Pen(Brushes.Black, 2), box);
        context.DrawRectangle(null, new Pen(Brushes.White, 1, DashStyle.Dash), box);
    }

    // The compositor may render after the next input event. Each frame owns immutable mark snapshots.
    private sealed class MarkOperation(Rect destination, SKRectI source, float scale, ImageMark[] marks, ImageMark? preview) : ICustomDrawOperation
    {
        public Rect Bounds => destination;
        public bool HitTest(Point p) => Bounds.Contains(p);
        public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);
        public void Dispose() { }
        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature) return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            canvas.Save();
            try
            {
                canvas.Translate((float)destination.X, (float)destination.Y);
                canvas.Scale(scale);
                canvas.Translate(-source.Left, -source.Top);
                canvas.ClipRect(source);
                foreach (var mark in marks) ImageEditor.DrawMark(canvas, mark);
                if (preview.HasValue) ImageEditor.DrawMark(canvas, preview.Value);
            }
            finally { canvas.Restore(); }
        }
    }

    public void Dispose() => original.Dispose();
}
