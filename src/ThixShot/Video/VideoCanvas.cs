using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ThixShot.Capture;

namespace ThixShot.Video;

internal sealed class VideoCanvas : Control
{
    private Bitmap? frame;
    public IReadOnlyList<ZoomPoint> Zooms { get; set; } = [];

    /// <summary>The clicks whose rings are drawn (empty when the clicks are hidden).</summary>
    public IReadOnlyList<InputEvent> Clicks { get; set; } = [];
    public double Seconds { get; set; }
    public ZoomPoint? Target { get; set; }
    public Action<double, double>? PlaceTarget { get; set; }

    public void SetFrame(Bitmap? next)
    {
        var old = frame;
        frame = next;
        InvalidateVisual();
        old?.Dispose();
    }

    private Rect DisplayRect()
    {
        if (frame == null) return default;
        double ratio = Math.Min(Bounds.Width / frame.Size.Width, Bounds.Height / frame.Size.Height);
        var size = new Size(frame.Size.Width * ratio, frame.Size.Height * ratio);
        return new Rect((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2, size.Width, size.Height);
    }

    public override void Render(DrawingContext context)
    {
        var dest = DisplayRect();
        if (frame == null || dest.Width < 1 || dest.Height < 1) return;
        if (Target is not { } point)
        {
            var viewport = VideoEditService.ViewportAt(Seconds, Zooms);
            var size = frame.Size;
            context.DrawImage(frame, new Rect(viewport.X * size.Width, viewport.Y * size.Height,
                viewport.Width * size.Width, viewport.Height * size.Height), dest);
            using (context.PushClip(dest)) DrawClicks(context, dest, viewport);
            return;
        }
        context.DrawImage(frame, dest);
        var w = dest.Width / point.Scale;
        var h = dest.Height / point.Scale;
        var focus = new Point(dest.X + point.X * dest.Width, dest.Y + point.Y * dest.Height);
        var box = new Rect(Math.Clamp(focus.X - w / 2, dest.X, dest.Right - w), Math.Clamp(focus.Y - h / 2, dest.Y, dest.Bottom - h), w, h);
        using (context.PushClip(dest))
        {
            var dim = new SolidColorBrush(Colors.Black, .36);
            context.FillRectangle(dim, new Rect(dest.X, dest.Y, dest.Width, box.Y - dest.Y));
            context.FillRectangle(dim, new Rect(dest.X, box.Bottom, dest.Width, dest.Bottom - box.Bottom));
            context.FillRectangle(dim, new Rect(dest.X, box.Y, box.X - dest.X, h));
            context.FillRectangle(dim, new Rect(box.Right, box.Y, dest.Right - box.Right, h));
            var pen = new Pen(Brushes.White, 2);
            context.DrawRectangle(null, pen, box);
            context.DrawEllipse(new SolidColorBrush(Colors.Black, .45), pen, focus, 8, 8);
            context.DrawLine(pen, focus - new Vector(12, 0), focus + new Vector(12, 0));
            context.DrawLine(pen, focus - new Vector(0, 12), focus + new Vector(0, 12));
        }
    }

    // The rings of ClickEffect as the export draws them: in the zoomed picture, growing with the zoom.
    private void DrawClicks(DrawingContext context, Rect dest, (double X, double Y, double Width, double Height) viewport)
    {
        if (Clicks.Count == 0) return;
        var color = Color.FromRgb(ClickEffect.Red, ClickEffect.Green, ClickEffect.Blue);
        foreach (var (x, y, radius, opacity) in ClickEffect.At(Seconds, Clicks))
        {
            double r = radius * Math.Min(dest.Width, dest.Height) / viewport.Width;
            var center = new Point(dest.X + (x - viewport.X) / viewport.Width * dest.Width, dest.Y + (y - viewport.Y) / viewport.Height * dest.Height);
            double stroke = Math.Max(1.5, r * ClickEffect.RingWidth), halo = Math.Max(1, r * ClickEffect.HaloWidth);
            context.DrawEllipse(new SolidColorBrush(color, ClickEffect.FillAlpha / 255 * opacity), null, center, r, r);
            context.DrawEllipse(null, new Pen(new SolidColorBrush(color, ClickEffect.RingAlpha / 255 * opacity), stroke), center, r, r);
            double outer = r + stroke / 2 + halo / 2;
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Colors.White, ClickEffect.HaloAlpha / 255 * opacity), halo), center, outer, outer);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetPosition(this);
        var area = DisplayRect();
        if (Target == null || !area.Contains(point) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        PlaceTarget?.Invoke((point.X - area.X) / area.Width, (point.Y - area.Y) / area.Height);
        e.Handled = true;
    }
}
