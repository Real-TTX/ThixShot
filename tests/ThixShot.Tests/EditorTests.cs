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
