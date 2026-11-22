using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ThixShot.Capture;

namespace ThixShot.Video;

internal sealed class ZoomTimeline : Control
{
    private enum DragMode { None, Seek, Move, Start, End }
    private DragMode drag;
    private ZoomPoint? original;
    private Point down;
    private bool changed;
    private const double MarginX = 9, FilmTop = 24, FilmHeight = 41, TrackTop = 73, TrackHeight = 24;
    private static readonly IBrush TextColor = new SolidColorBrush(Color.Parse("#BBC8DF"));
    private static readonly IBrush Track = new SolidColorBrush(Color.Parse("#273348"));
    private static readonly IBrush Selected = new SolidColorBrush(Color.Parse("#4E83EE"));
    private static readonly IBrush Normal = new SolidColorBrush(Color.Parse("#374E75"));
    public IReadOnlyList<ZoomPoint> Zooms { get; set; } = [];
    public int SelectedIndex { get; set; } = -1;
    public double Duration { get; init; }
    public double Seconds { get; set; }
    public Bitmap?[] Frames { get; } = new Bitmap?[8];
    public Action<double>? Seek { get; set; }
    public Action<int>? SelectZoom { get; set; }
    public Action? BeginChange { get; set; }
    public Action<int, ZoomPoint>? MoveZoom { get; set; }
    private double TrackWidth => Math.Max(1, Bounds.Width - 2 * MarginX);
    private double X(double time) => MarginX + Math.Clamp(time / Duration, 0, 1) * TrackWidth;
    private double Time(double x) => Math.Clamp((x - MarginX) / TrackWidth, 0, 1) * Duration;
    internal static string Timecode(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss\.f" : @"m\:ss\.f", CultureInfo.InvariantCulture);
    private static FormattedText Label(string text, IBrush color) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, color);

    public override void Render(DrawingContext context)
    {
        if (Duration <= 0) return;
        context.FillRectangle(new SolidColorBrush(Color.Parse("#192231")), new Rect(Bounds.Size));
        var basePen = new Pen(Track, 1);
        for (int i = 0; i <= 4; i++)
        {
            double at = Duration * i / 4;
            var tick = Label(Timecode(at), TextColor);
            context.DrawText(tick, new Point(Math.Clamp(X(at) - tick.Width / 2, MarginX, Math.Max(MarginX, Bounds.Width - tick.Width - MarginX)), 3));
            context.DrawLine(basePen, new Point(X(at), 18), new Point(X(at), FilmTop));
        }
        var film = new Rect(MarginX, FilmTop, TrackWidth, FilmHeight);
        context.DrawRectangle(Track, null, film, 4, 4);
        using (context.PushClip(film))
        {
            double tile = TrackWidth / Frames.Length;
            for (int i = 0; i < Frames.Length; i++)
            {
                if (Frames[i] is not { } image) continue;
                var rect = new Rect(MarginX + i * tile, FilmTop, tile - 1, FilmHeight);
                var scale = Math.Max(rect.Width / image.Size.Width, rect.Height / image.Size.Height);
                var w = rect.Width / scale; var h = rect.Height / scale;
                context.DrawImage(image, new Rect((image.Size.Width - w) / 2, (image.Size.Height - h) / 2, w, h), rect);
            }
        }
        context.DrawRectangle(Track, null, new Rect(MarginX, TrackTop, TrackWidth, TrackHeight), 4, 4);
        for (int i = 0; i < Zooms.Count; i++)
            if (i != SelectedIndex) DrawZoom(context, i);
        if (SelectedIndex >= 0 && SelectedIndex < Zooms.Count) DrawZoom(context, SelectedIndex);
        var head = X(Seconds);
        context.DrawLine(new Pen(Brushes.White, 1.5), new Point(head, 19), new Point(head, TrackTop + TrackHeight + 3));
        context.DrawRectangle(Brushes.White, null, new Rect(head - 3, 17, 6, 7), 2, 2);
    }
    private Rect Box(ZoomPoint z)
    {
        double left = X(z.StartSeconds);
        return new Rect(left, TrackTop, Math.Max(5, X(z.StartSeconds + z.DurationSeconds) - left), TrackHeight);
    }
    private void DrawZoom(DrawingContext context, int index)
    {
        var point = Zooms[index];
        var rect = Box(point);
        using var clip = context.PushClip(new Rect(MarginX, TrackTop, TrackWidth, TrackHeight));
        context.DrawRectangle(index == SelectedIndex ? Selected : Normal, null, rect, 4, 4);
        if (rect.Width > 36) context.DrawText(Label($"{point.Scale:0.#}×", Brushes.White), new Point(rect.X + 8, TrackTop + 4));
        if (rect.Width > 20 && index == SelectedIndex)
        {
            var pen = new Pen(Brushes.White, 1.5);
            foreach (double x in new[] { rect.X + 3, rect.Right - 3 }) context.DrawLine(pen, new Point(x, TrackTop + 7), new Point(x, TrackTop + 17));
        }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        down = e.GetPosition(this); changed = false; original = null;
        drag = DragMode.Seek;
        int selected = SelectedIndex >= 0 && SelectedIndex < Zooms.Count && Box(Zooms[SelectedIndex]).Contains(down)
            ? SelectedIndex : Enumerable.Range(0, Zooms.Count).Reverse().FirstOrDefault(i => Box(Zooms[i]).Contains(down), -1);
        if (selected >= 0)
        {
            SelectZoom?.Invoke(selected);
            original = Zooms[selected];
            var rect = Box(original);
            drag = Math.Abs(down.X - rect.Right) < 5 ? DragMode.End : Math.Abs(down.X - rect.X) < 5 ? DragMode.Start : DragMode.Move;
        }
        else Seek?.Invoke(Time(down.X));
        e.Pointer.Capture(this); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        if (drag == DragMode.Seek) { Seek?.Invoke(Time(p.X)); return; }
        if (drag == DragMode.None || original is not { } z || Math.Abs(p.X - down.X) < 2 && !changed) return;
        if (!changed) { BeginChange?.Invoke(); changed = true; }
        double delta = (p.X - down.X) * Duration / TrackWidth;
        var end = Math.Min(Duration, z.StartSeconds + z.DurationSeconds);
        double min = Math.Min(.1, Math.Max(.001, end - z.StartSeconds));
        var start = drag == DragMode.Start ? Math.Clamp(z.StartSeconds + delta, 0, Math.Max(0, end - min)) :
            Math.Clamp(z.StartSeconds + delta, 0, Math.Max(0, Duration - z.DurationSeconds));
        var next = drag switch
        {
            DragMode.End => z with { DurationSeconds = Math.Clamp(z.DurationSeconds + delta, min, Math.Max(min, Duration - z.StartSeconds)) },
            DragMode.Start => z with { StartSeconds = start, DurationSeconds = end - start },
            _ => z with { StartSeconds = start }
        };
        MoveZoom?.Invoke(SelectedIndex, next);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { drag = DragMode.None; e.Pointer.Capture(null); }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { drag = DragMode.None; original = null; }
}
