using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using ThixShot.Editor;
using ThixShot.Integration;
using ThixShot.Library;
using ThixShot.Studio;

namespace ThixShot.Tests;

public sealed class StudioTests
{
    [AvaloniaFact]
    public void Studio_switches_capture_mode_and_opens_the_about_page_from_the_version()
    {
        var studio = new StudioWindow();
        try
        {
            studio.Show();
            using var frame = studio.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.InRange(frame.PixelSize.Width, 900, 1600);
            var output = Environment.GetEnvironmentVariable("THIXSHOT_QA_DIR");
            if (!string.IsNullOrEmpty(output))
            {
                Directory.CreateDirectory(output);
                frame.Save(Path.Combine(output, "studio.png"));
            }
            Assert.NotNull(ButtonWithLabel(studio, "Screenshot erstellen"));
            Click(studio, "Video");
            Assert.NotNull(ButtonWithLabel(studio, "Aufnahme starten"));
            Assert.Contains(studio.GetVisualDescendants().OfType<CheckBox>(), box => box.Content as string == "Fokus-Zoom");
            Click(studio, UpdateService.DisplayVersion);
            Assert.Contains(studio.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Über ThixShot");
        }
        finally { studio.Close(); }
    }

    [AvaloniaFact]
    public async Task Editor_saves_a_pointer_selected_crop_as_a_new_png_in_the_library()
    {
        using var input = TestProfile.TestImage();
        var owner = new Window();
        owner.Show();
        var editor = new EditorWindow(input);
        string? saved = null;
        try
        {
            var result = editor.ShowDialog<string?>(owner);
            using var frame = editor.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Click(editor, "Ausschneiden");
            var surface = Assert.Single(editor.GetVisualDescendants().OfType<ImageSurface>());
            Assert.Equal(MarkTool.Crop, surface.Tool);
            var image = surface.ImageRect;
            Point ToWindow(double x, double y) => surface.TranslatePoint(new Point(image.X + x * image.Width,
                image.Y + y * image.Height), editor)!.Value;
            var start = ToWindow(.75, .75);
            var end = ToWindow(.25, .25);
            editor.MouseMove(start);
            editor.MouseDown(start, MouseButton.Left);
            editor.MouseMove(end, RawInputModifiers.LeftMouseButton);
            editor.MouseUp(end, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.True(ButtonWithLabel(editor, "Zurücknehmen").IsEnabled);
            Click(editor, "In Mediathek speichern");
            saved = await result.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(saved);
            Assert.Equal(MediaLibrary.Folder, Path.GetDirectoryName(saved));
            using var png = SKBitmap.Decode(saved);
            Assert.InRange(png.Width, 32, 34);
            Assert.InRange(png.Height, 16, 18);
            Assert.Equal(SKColors.White, png.GetPixel(3, 3));
        }
        finally
        {
            if (saved is not null) MediaLibrary.Delete(saved);
            if (editor.IsVisible) editor.Close();
            owner.Close();
        }
    }

    private static Button ButtonWithLabel(Window window, string label) => Assert.Single(
        window.GetVisualDescendants().OfType<Button>(), button => button.Content as string == label ||
            button.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == label));

    private static void Click(Window window, string label)
    {
        window.UpdateLayout();
        var button = ButtonWithLabel(window, label);
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
