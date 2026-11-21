using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ThixShot.Studio;

internal sealed class StudioPreview : Control
{
    private static readonly Lazy<Bitmap> Wallpaper = new(() => Ui.Asset("Studio.jpg"));
    public bool Region { get; init; }
    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        var full = new Rect(size);
        var source = Wallpaper.Value;
        double scale = Math.Max(size.Width / source.Size.Width, size.Height / source.Size.Height);
        var sample = new Rect((source.Size.Width - size.Width / scale) / 2, (source.Size.Height - size.Height / scale) / 2,
            size.Width / scale, size.Height / scale);
        context.DrawImage(source, sample, full);
        double fraction = Region ? .56 : .74;
        var frame = new Rect(size.Width * (1 - fraction) / 2, size.Height * .22, size.Width * fraction, size.Height * .54);
        var shade = new SolidColorBrush(Colors.Black, .17);
        context.FillRectangle(shade, new Rect(0, 0, size.Width, frame.Y));
        context.FillRectangle(shade, new Rect(0, frame.Bottom, size.Width, size.Height - frame.Bottom));
        context.FillRectangle(shade, new Rect(0, frame.Y, frame.X, frame.Height));
        context.FillRectangle(shade, new Rect(frame.Right, frame.Y, frame.X, frame.Height));
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Colors.White, .68), 1, DashStyle.Dash), frame);
        foreach (var (point, dx, dy) in new[] { (frame.TopLeft,1,1), (frame.TopRight,-1,1), (frame.BottomLeft,1,-1), (frame.BottomRight,-1,-1) })
        {
            var pen = new Pen(Brushes.White, 2.5);
            context.DrawLine(pen, point, point + new Vector(dx * 13, 0));
            context.DrawLine(pen, point, point + new Vector(0, dy * 13));
        }
        var center = frame.Center;
        context.DrawLine(new Pen(Brushes.White, 1), center - new Vector(6, 0), center + new Vector(6, 0));
        context.DrawLine(new Pen(Brushes.White, 1), center - new Vector(0, 6), center + new Vector(0, 6));
    }
}
