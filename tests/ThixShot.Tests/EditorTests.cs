using SkiaSharp;
using ThixShot.Editor;

namespace ThixShot.Tests;

public sealed class EditorTests
{
    [Fact]
    public void Selection_clamps_a_reverse_drag_and_rounds_out_to_whole_pixels()
    {
        var bounds = new SKRectI(10, 20, 90, 65);
        var selected = ImageEditor.Selection(new SKPoint(75.1f, 70), new SKPoint(-5, 24.5f), bounds);
        Assert.Equal(new SKRectI(10, 24, 76, 65), selected);
    }

    [Fact]
    public void Nested_crop_and_undo_restore_the_pixels_and_annotations()
    {
        using var input = TestProfile.TestImage();
        using var document = new ImageDocument(input);
        Assert.True(Add(MarkTool.Highlight, 8, 4, 30, 20));
        Assert.True(Add(MarkTool.Crop, 42, 23, 4, 2));
        Assert.True(Add(MarkTool.Crop, 31, 18, 8, 4));
        using (var crop = Render())
        {
            Assert.Equal((23, 14), (crop.Width, crop.Height));
            var marked = crop.GetPixel(1, 1);
            Assert.Equal(255, marked.Red);
            Assert.InRange((int)marked.Green, 145, 175);
        }
        ImageEditor.Undo(document);
        Assert.Equal(new SKRectI(4, 2, 42, 23), document.Visible);
        ImageEditor.Undo(document);
        Assert.Equal(new SKRectI(0, 0, 64, 32), document.Visible);
        ImageEditor.Undo(document);
        ImageEditor.Undo(document);
        Assert.Empty(document.History);
        using var original = Render();
        Assert.Equal(SKColors.White, original.GetPixel(9, 5));
        Assert.Equal(SKColors.White, document.Original.GetPixel(9, 5));

        bool Add(MarkTool tool, int x1, int y1, int x2, int y2) =>
            ImageEditor.Add(document, new ImageMark(tool, new(x1, y1), new(x2, y2), SKColors.Red));
        SKBitmap Render()
        {
            using var data = ImageEditor.Render(document);
            return SKBitmap.Decode(data);
        }
    }

    [Fact]
    public void Redo_recovers_the_cropped_pen_and_text_and_a_new_mark_starts_a_new_history()
    {
        using var input = TestProfile.TestImage();
        using var document = new ImageDocument(input);
        SKPoint[] points = [new(3, 25), new(16, 17), new(26, 21)];
        Assert.True(ImageEditor.Add(document, new(MarkTool.Pen, points[0], points[^1], SKColors.Blue, 3, Points: points)));
        Assert.True(ImageEditor.Add(document, new(MarkTool.Text, new(27, 1), default, SKColors.Red, 14, "Ab")));
        Assert.True(ImageEditor.Add(document, new(MarkTool.Crop, new(2, 1), new(59, 30), default)));
        using var expected = ImageEditor.Render(document);
        using var pixels = SKBitmap.Decode(expected);
        Assert.Equal(SKColors.Blue, pixels.GetPixel(14, 16));
        Assert.Contains(pixels.Pixels, color => color.Red > 230 && color.Green < 30 && color.Blue < 30);
        ImageEditor.Undo(document);
        ImageEditor.Undo(document);
        ImageEditor.Redo(document);
        ImageEditor.Redo(document);
        using var restored = ImageEditor.Render(document);
        Assert.Equal(expected.ToArray(), restored.ToArray());
        ImageEditor.Undo(document);
        Assert.True(ImageEditor.Add(document, new(MarkTool.Rectangle, new(9, 8), new(15, 12), SKColors.Red)));
        Assert.Empty(document.RedoStack);
        Assert.Equal(new SKRectI(0, 0, 64, 32), document.Visible);
    }

    [Fact]
    public void Png_export_replaces_only_the_selected_file_and_leaves_no_staging_files()
    {
        using var input = TestProfile.TestImage();
        using var document = new ImageDocument(input);
        ImageEditor.Add(document, new(MarkTool.Crop, new(4, 4), new(28, 14), default));
        using var encoded = ImageEditor.Render(document);
        var folder = Directory.CreateTempSubdirectory("thixshot-export-test-");
        try
        {
            var path = Path.Combine(folder.FullName, "Ausgabe.png");
            File.WriteAllBytes(path, new byte[8192]);
            ImageOutput.WriteLocalFile(encoded, path);
            Assert.Equal(encoded.ToArray(), File.ReadAllBytes(path));
            Assert.Single(folder.GetFiles());
            using var saved = SKBitmap.Decode(path);
            Assert.Equal((24, 10), (saved.Width, saved.Height));
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public void Snip_copies_only_the_selected_pixels_at_their_original_resolution()
    {
        var folder = Directory.CreateTempSubdirectory("thixshot-snip-");
        try
        {
            string source = Path.Combine(folder.FullName, "Desktop.png");
            string destination = Path.Combine(folder.FullName, "Ausschnitt.png");
            using var desktop = new SKBitmap(16, 12);
            for (int y = 0; y < desktop.Height; y++)
                for (int x = 0; x < desktop.Width; x++)
                    desktop.SetPixel(x, y, new SKColor((byte)(13 * x), (byte)(17 * y), 101));
            using (var png = desktop.Encode(SKEncodedImageFormat.Png, 100))
            using (var output = File.Create(source)) png.SaveTo(output);

            Assert.Throws<InvalidDataException>(() => ImageOutput.SaveCrop(source, new SKRectI(-1, 2, 8, 6), destination));
            Assert.False(File.Exists(destination));
            ImageOutput.SaveCrop(source, new SKRectI(3, 2, 10, 8), destination);
            using var result = SKBitmap.Decode(destination);
            Assert.Equal((7, 6), (result.Width, result.Height));
            for (int y = 0; y < result.Height; y++)
                for (int x = 0; x < result.Width; x++)
                    Assert.Equal(desktop.GetPixel(x + 3, y + 2), result.GetPixel(x, y));
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public void Stroke_tools_draw_the_selected_color_and_reject_an_accidental_click()
    {
        using var input = TestProfile.TestImage();
        using var document = new ImageDocument(input);
        var click = new ImageMark(MarkTool.Crop, new(12, 9), new(12, 9), SKColors.Red);
        Assert.False(ImageEditor.Add(document, click));
        Assert.False(ImageEditor.Add(document, click with { Tool = MarkTool.Arrow }));
        Assert.True(ImageEditor.Add(document, new(MarkTool.Arrow, new(4, 7), new(44, 7), SKColors.Blue)));
        Assert.True(ImageEditor.Add(document, new(MarkTool.Rectangle, new(12, 14), new(34, 27), SKColors.Red)));
        using var png = ImageEditor.Render(document);
        using var actual = SKBitmap.Decode(png);
        Assert.Equal(SKColors.Blue, actual.GetPixel(24, 7));
        Assert.Equal(SKColors.Red, actual.GetPixel(12, 18));
        Assert.Equal(SKColors.White, actual.GetPixel(22, 18));
    }
}
