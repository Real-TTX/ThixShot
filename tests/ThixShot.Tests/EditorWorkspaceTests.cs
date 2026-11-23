using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using ThixShot.Editor;
using ThixShot.Studio;

namespace ThixShot.Tests;

public sealed class EditorWorkspaceTests
{
    [AvaloniaFact]
    public void An_arrow_previews_at_its_final_position_and_undo_redo_and_the_close_guard_work()
    {
        using var source = TestProfile.TestImage();
        var editor = new EditorWindow(source);
        editor.Show();
        var surface = Assert.Single(editor.GetVisualDescendants().OfType<ImageSurface>());
        StudioTests.Click(editor, "Pfeil");
        Point start = ImagePoint(editor, surface, .2, .5), end = ImagePoint(editor, surface, .8, .5);
        Point center = ImagePoint(editor, surface, .5, .5);
        Assert.Equal(SKColors.White, ReadPixel(editor, center));
        surface.Focus();
        editor.MouseDown(start, MouseButton.Left);
        editor.MouseMove(end, RawInputModifiers.LeftMouseButton);
        editor.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, "");
        editor.MouseUp(end, MouseButton.Left);
        Assert.True(editor.IsVisible);
        Assert.Equal(SKColors.White, ReadPixel(editor, center));
        editor.MouseDown(start, MouseButton.Left);
        editor.MouseMove(end, RawInputModifiers.LeftMouseButton);
        var preview = ReadPixel(editor, center);
        Assert.Equal(SKColor.Parse("#FFB729"), preview);
        editor.MouseUp(end, MouseButton.Left);
        Assert.Equal(preview, ReadPixel(editor, center));
        StudioTests.Click(editor, "Zurücknehmen");
        Assert.Equal(SKColors.White, ReadPixel(editor, center));
        surface.Focus();
        editor.KeyPress(Key.Z, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.Z, "Z");
        Assert.Equal(preview, ReadPixel(editor, center));
        editor.Close();
        Dispatcher.UIThread.RunJobs();
        var guard = Assert.Single(editor.OwnedWindows);
        StudioTests.Click(guard, "Weiter bearbeiten");
        Assert.True(editor.IsVisible);
        editor.Close();
        Dispatcher.UIThread.RunJobs();
        StudioTests.Click(Assert.Single(editor.OwnedWindows), "Verwerfen");
        Assert.False(editor.IsVisible);
    }

    [AvaloniaFact]
    public async Task Copy_exposes_the_rendered_image_to_the_headless_clipboard()
    {
        using var source = TestProfile.TestImage();
        var editor = new EditorWindow(source);
        try
        {
            editor.Show();
            StudioTests.Click(editor, "Kopieren");
            Assert.NotNull(editor.Clipboard);
            using var image = await editor.Clipboard.TryGetBitmapAsync();
            Assert.NotNull(image);
            using var encoded = new MemoryStream();
            image.Save(encoded);
            encoded.Position = 0;
            using var pixels = SKBitmap.Decode(encoded);
            Assert.Equal((64, 32), (pixels.Width, pixels.Height));
            Assert.Equal(SKColors.White, pixels.GetPixel(9, 9));
        }
        finally { editor.Close(); }
    }

    [AvaloniaFact]
    public void Zoom_and_pan_can_inspect_a_point_before_returning_to_fit()
    {
        using var source = TestProfile.TestImage();
        var editor = new EditorWindow(source);
        try
        {
            editor.Show();
            var surface = Assert.Single(editor.GetVisualDescendants().OfType<ImageSurface>());
            StudioTests.Click(editor, "100 %");
            Assert.False(surface.IsFitted);
            Assert.Equal(100, surface.ZoomPercent, 4);
            surface.ZoomTo(2400);
            var initial = surface.ImageRect;
            StudioTests.Click(editor, "Verschieben");
            var from = ImagePoint(editor, surface, .5, .5);
            var to = from + new Vector(54, 12);
            editor.MouseDown(from, MouseButton.Left);
            editor.MouseMove(to, RawInputModifiers.LeftMouseButton);
            editor.MouseUp(to, MouseButton.Left);
            Assert.InRange(surface.ImageRect.X - initial.X, 53, 55);
            StudioTests.Click(editor, "Einpassen");
            Assert.True(surface.IsFitted);
            Assert.True(surface.Bounds.Width > surface.ImageRect.Width);
        }
        finally { editor.Close(); }
    }

    [AvaloniaFact]
    public void Screenshot_can_be_annotated_with_freehand_and_text()
    {
        using var source = AssetLoader.Open(new Uri("avares://ThixShot/Assets/Studio.jpg"));
        var editor = new EditorWindow(source, true);
        editor.Show();
        var surface = Assert.Single(editor.GetVisualDescendants().OfType<ImageSurface>());
        StudioTests.Click(editor, "Freihand");
        var a = ImagePoint(editor, surface, .30, .69);
        var b = ImagePoint(editor, surface, .43, .67);
        var c = ImagePoint(editor, surface, .55, .69);
        editor.MouseDown(a, MouseButton.Left);
        editor.MouseMove(b, RawInputModifiers.LeftMouseButton);
        editor.MouseMove(c, RawInputModifiers.LeftMouseButton);
        editor.MouseUp(c, MouseButton.Left);
        Assert.NotEqual(SKColors.Transparent, ReadPixel(editor, b));
        StudioTests.Click(editor, "Text");
        var location = ImagePoint(editor, surface, .30, .60);
        editor.MouseDown(location, MouseButton.Left);
        editor.MouseUp(location, MouseButton.Left);
        var input = Assert.Single(editor.GetVisualDescendants().OfType<TextBox>(), t => t.Watermark == "Beschriftung");
        Assert.True(input.IsEnabled);
        input.Text = "Details im Fokus";
        Dispatcher.UIThread.RunJobs();
        editor.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("", input.Text);
        StudioTests.Click(editor, "Pfeil");
        var arrowStart = ImagePoint(editor, surface, .66, .50);
        var arrowEnd = ImagePoint(editor, surface, .58, .64);
        editor.MouseDown(arrowStart, MouseButton.Left);
        editor.MouseMove(arrowEnd, RawInputModifiers.LeftMouseButton);
        editor.MouseUp(arrowEnd, MouseButton.Left);
        using var frame = editor.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (Environment.GetEnvironmentVariable("THIXSHOT_QA_DIR") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            frame.Save(Path.Combine(output, "screenshot-editor.png"));
        }
        StudioTests.Click(editor, "Zurücknehmen");
        StudioTests.Click(editor, "Zurücknehmen");
        StudioTests.Click(editor, "Zurücknehmen");
        editor.Close();
        Assert.False(editor.IsVisible);
    }

    private static Point ImagePoint(Window window, ImageSurface surface, double x, double y)
    {
        window.UpdateLayout();
        var image = surface.ImageRect;
        return surface.TranslatePoint(new Point(image.X + x * image.Width, image.Y + y * image.Height), window)!.Value;
    }

    private static SKColor ReadPixel(Window window, Point point)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var encoded = new MemoryStream();
        frame.Save(encoded);
        encoded.Position = 0;
        using var pixels = SKBitmap.Decode(encoded);
        return pixels.GetPixel((int)(point.X * window.RenderScaling), (int)(point.Y * window.RenderScaling));
    }
}
