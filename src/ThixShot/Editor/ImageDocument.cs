using SkiaSharp;

namespace ThixShot.Editor;

internal enum MarkTool { Highlight, Arrow, Rectangle, Pen, Text, Crop, Pan }

internal readonly record struct ImageMark(MarkTool Tool, SKPoint Start, SKPoint End, SKColor Color,
    float Width = 0, string? Text = null, SKPoint[]? Points = null);

internal sealed class ImageDocument : IDisposable
{
    internal SKBitmap Original { get; }
    internal List<ImageMark> History { get; } = [];
    internal Stack<ImageMark> RedoStack { get; } = [];
    internal SKRectI Visible { get; set; }
    internal float DefaultStroke => Math.Clamp(Math.Min(Original.Width, Original.Height) / 175f, 3, 14);

    public ImageDocument(Stream input)
    {
        Original = SKBitmap.Decode(input) ?? throw new InvalidDataException("Das Bild konnte nicht gelesen werden.");
        Visible = new SKRectI(0, 0, Original.Width, Original.Height);
    }

    public void Dispose() => Original.Dispose();
}

internal static class ImageEditor
{
    public static SKRectI Selection(SKPoint start, SKPoint end, SKRectI bounds) =>
        new(Math.Clamp((int)Math.Floor(Math.Min(start.X, end.X)), bounds.Left, bounds.Right),
            Math.Clamp((int)Math.Floor(Math.Min(start.Y, end.Y)), bounds.Top, bounds.Bottom),
            Math.Clamp((int)Math.Ceiling(Math.Max(start.X, end.X)), bounds.Left, bounds.Right),
            Math.Clamp((int)Math.Ceiling(Math.Max(start.Y, end.Y)), bounds.Top, bounds.Bottom));

    public static bool Add(ImageDocument document, ImageMark mark)
    {
        var area = Selection(mark.Start, mark.End, document.Visible);
        var valid = mark.Tool switch
        {
            MarkTool.Arrow => (mark.End - mark.Start).Length >= 3,
            MarkTool.Pen => mark.Points is { Length: > 0 },
            MarkTool.Text => !string.IsNullOrWhiteSpace(mark.Text),
            MarkTool.Pan => false,
            MarkTool.Crop => area.Width >= 2 && area.Height >= 2 && area != document.Visible,
            _ => area.Width >= 2 && area.Height >= 2
        };
        if (!valid) return false;
        if (mark.Width <= 0) mark = mark with { Width = mark.Tool == MarkTool.Text ? 24 : document.DefaultStroke };
        document.RedoStack.Clear();
        Append(document, mark);
        return true;
    }

    private static void Append(ImageDocument document, ImageMark mark)
    {
        document.History.Add(mark);
        if (mark.Tool == MarkTool.Crop)
            document.Visible = Selection(mark.Start, mark.End, document.Visible);
    }

    public static void Undo(ImageDocument document)
    {
        if (document.History.Count == 0) return;
        var mark = document.History[^1];
        document.RedoStack.Push(mark);
        document.History.RemoveAt(document.History.Count - 1);
        if (mark.Tool != MarkTool.Crop) return;
        document.Visible = new SKRectI(0, 0, document.Original.Width, document.Original.Height);
        foreach (var crop in document.History.Where(item => item.Tool == MarkTool.Crop))
            document.Visible = Selection(crop.Start, crop.End, document.Visible);
    }

    public static void Redo(ImageDocument document)
    {
        if (document.RedoStack.TryPop(out var mark)) Append(document, mark);
    }

    public static SKData Render(ImageDocument document)
    {
        var area = document.Visible;
        using var target = new SKBitmap(area.Width, area.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(target);
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(-area.Left, -area.Top);
        canvas.DrawBitmap(document.Original, 0, 0);
        foreach (var mark in document.History) DrawMark(canvas, mark);
        using var image = SKImage.FromBitmap(target);
        return image.Encode(SKEncodedImageFormat.Png, 100);
    }

    // The canvas preview and the exported PNG use the same geometry in original image pixels.
    internal static void DrawMark(SKCanvas canvas, ImageMark mark)
    {
        if (mark.Tool is MarkTool.Crop or MarkTool.Pan) return;
        using var paint = new SKPaint { Color = mark.Color, IsAntialias = true, StrokeWidth = Math.Max(1, mark.Width),
            StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, Style = SKPaintStyle.Stroke };
        var rect = new SKRect(Math.Min(mark.Start.X, mark.End.X), Math.Min(mark.Start.Y, mark.End.Y),
            Math.Max(mark.Start.X, mark.End.X), Math.Max(mark.Start.Y, mark.End.Y));
        switch (mark.Tool)
        {
            case MarkTool.Highlight:
                paint.Style = SKPaintStyle.Fill;
                paint.Color = mark.Color.WithAlpha(95);
                canvas.DrawRect(rect, paint);
                break;
            case MarkTool.Rectangle:
                canvas.DrawRect(rect, paint);
                break;
            case MarkTool.Arrow:
                DrawArrow(canvas, mark, paint);
                break;
            case MarkTool.Pen when mark.Points is { Length: > 0 } points:
                if (points.Length == 1) { canvas.DrawPoint(points[0], paint); break; }
                using (var path = new SKPath())
                {
                    path.MoveTo(points[0]);
                    for (var i = 1; i < points.Length; i++) path.LineTo(points[i]);
                    canvas.DrawPath(path, paint);
                }
                break;
            case MarkTool.Text when !string.IsNullOrWhiteSpace(mark.Text):
                using (var font = new SKFont(SKTypeface.Default, Math.Max(6, mark.Width)))
                {
                    paint.Style = SKPaintStyle.Fill;
                    var y = mark.Start.Y - font.Metrics.Ascent;
                    foreach (var line in mark.Text.Replace("\r\n", "\n").Split('\n'))
                    {
                        canvas.DrawText(line, mark.Start.X, y, font, paint);
                        y += font.Spacing;
                    }
                }
                break;
        }
    }

    private static void DrawArrow(SKCanvas canvas, ImageMark mark, SKPaint paint)
    {
        var delta = mark.End - mark.Start;
        if (delta.Length < .01f) return;
        var unit = new SKPoint(delta.X / delta.Length, delta.Y / delta.Length);
        var head = Math.Min(paint.StrokeWidth * 4.3f, delta.Length * .42f);
        var root = new SKPoint(mark.End.X - unit.X * head, mark.End.Y - unit.Y * head);
        var wing = new SKPoint(-unit.Y * head * .5f, unit.X * head * .5f);
        canvas.DrawLine(mark.Start, mark.End, paint);
        canvas.DrawLine(mark.End, root + wing, paint);
        canvas.DrawLine(mark.End, root - wing, paint);
    }
}
