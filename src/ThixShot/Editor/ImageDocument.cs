using SkiaSharp;

namespace ThixShot.Editor;

internal enum MarkTool { Crop, Highlight, Arrow, Rectangle }
internal readonly record struct ImageMark(MarkTool Tool, SKPoint Start, SKPoint End, SKColor Color);

internal sealed class ImageDocument : IDisposable
{
    internal SKBitmap Original { get; }
    internal List<ImageMark> History { get; } = [];
    internal SKRectI Visible { get; set; }

    public ImageDocument(Stream input)
    {
        Original = SKBitmap.Decode(input) ?? throw new InvalidDataException("Das Bild konnte nicht gelesen werden.");
        Visible = new SKRectI(0, 0, Original.Width, Original.Height);
    }

    public void Dispose() => Original.Dispose();
}

internal static class ImageEditor
{
    public static SKRectI Selection(SKPoint start, SKPoint end, SKRectI bounds)
    {
        return new SKRectI(Math.Clamp((int)Math.Floor(Math.Min(start.X, end.X)), bounds.Left, bounds.Right),
            Math.Clamp((int)Math.Floor(Math.Min(start.Y, end.Y)), bounds.Top, bounds.Bottom),
            Math.Clamp((int)Math.Ceiling(Math.Max(start.X, end.X)), bounds.Left, bounds.Right),
            Math.Clamp((int)Math.Ceiling(Math.Max(start.Y, end.Y)), bounds.Top, bounds.Bottom));
    }

    public static bool Add(ImageDocument document, ImageMark mark)
    {
        var area = Selection(mark.Start, mark.End, document.Visible);
        if (mark.Tool == MarkTool.Arrow ? (mark.End - mark.Start).Length < 3 : area.Width < 2 || area.Height < 2)
            return false;
        document.History.Add(mark);
        if (mark.Tool == MarkTool.Crop) document.Visible = area;
        return true;
    }

    public static void Undo(ImageDocument document)
    {
        if (document.History.Count == 0) return;
        document.History.RemoveAt(document.History.Count - 1);
        document.Visible = new SKRectI(0, 0, document.Original.Width, document.Original.Height);
        foreach (var mark in document.History.Where(mark => mark.Tool == MarkTool.Crop))
            document.Visible = Selection(mark.Start, mark.End, document.Visible);
    }

    public static SKData Render(ImageDocument document)
    {
        var area = document.Visible;
        using var target = new SKBitmap(area.Width, area.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(target);
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(-area.Left, -area.Top);
        canvas.DrawBitmap(document.Original, 0, 0);
        float stroke = Math.Clamp(Math.Min(document.Original.Width, document.Original.Height) / 175f, 3, 14);
        foreach (var mark in document.History)
        {
            if (mark.Tool == MarkTool.Crop) continue;
            using var paint = new SKPaint { Color = mark.Color, IsAntialias = true, StrokeWidth = stroke,
                StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, Style = SKPaintStyle.Stroke };
            var rect = new SKRect(Math.Min(mark.Start.X, mark.End.X), Math.Min(mark.Start.Y, mark.End.Y),
                Math.Max(mark.Start.X, mark.End.X), Math.Max(mark.Start.Y, mark.End.Y));
            if (mark.Tool == MarkTool.Highlight)
            {
                paint.Style = SKPaintStyle.Fill;
                paint.Color = mark.Color.WithAlpha(95);
                canvas.DrawRect(rect, paint);
            }
            else if (mark.Tool == MarkTool.Rectangle) canvas.DrawRect(rect, paint);
            else
            {
                var delta = mark.End - mark.Start;
                var unit = new SKPoint(delta.X / delta.Length, delta.Y / delta.Length);
                var head = Math.Min(stroke * 4.3f, delta.Length * .42f);
                var root = new SKPoint(mark.End.X - unit.X * head, mark.End.Y - unit.Y * head);
                var wing = new SKPoint(-unit.Y * head * .5f, unit.X * head * .5f);
                canvas.DrawLine(mark.Start, mark.End, paint);
                canvas.DrawLine(mark.End, root + wing, paint);
                canvas.DrawLine(mark.End, root - wing, paint);
            }
        }
        using var image = SKImage.FromBitmap(target);
        return image.Encode(SKEncodedImageFormat.Png, 100);
    }
}
