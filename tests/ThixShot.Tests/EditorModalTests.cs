using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using ThixShot.Capture;
using ThixShot.Editor;
using ThixShot.Library;
using ThixShot.Studio;
using ThixShot.Video;

namespace ThixShot.Tests;

public sealed class EditorModalTests
{
    [AvaloniaTheory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Screenshot_close_confirmation_returns_to_the_originating_mode(bool ready, bool save)
    {
        var studio = new StudioWindow();
        studio.Show();
        if (ready) studio.EnterReadyMode();
        var palette = studio.ActiveReadyPalette;
        var before = MediaLibrary.List().Select(item => item.Path).ToHashSet();
        var opened = new TaskCompletionSource<EditorWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var observe = Window.WindowOpenedEvent.AddClassHandler<EditorWindow>((editor, _) => opened.TrySetResult(editor));
        try
        {
            var capture = studio.CaptureAsync((file, _) =>
            {
                Assert.False(studio.IsVisible);
                Assert.False(palette?.IsVisible == true);
                using var screenshot = TestProfile.TestImage();
                using var output = File.Create(file);
                screenshot.CopyTo(output);
                return Task.FromResult(true);
            });
            var editor = await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(editor.Topmost);
            Assert.False(palette?.IsVisible == true);
            Assert.Equal(!ready, studio.IsVisible);
            Assert.Equal(ready ? null : studio, editor.Owner);
            var surface = Assert.Single(editor.GetVisualDescendants().OfType<ImageSurface>());
            surface.Submitted!(new ImageMark(MarkTool.Highlight, new(2, 2), new(20, 12), SKColors.Yellow));

            var confirm = AskToClose(editor);
            Assert.False(palette?.IsVisible == true);
            Assert.False(capture.IsCompleted);
            confirm.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, "");
            Dispatcher.UIThread.RunJobs();
            Assert.True(editor.IsVisible);
            Assert.Empty(editor.OwnedWindows);

            confirm = AskToClose(editor);
            StudioTests.Click(confirm, save ? "Speichern" : "Verwerfen");
            await capture.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(editor.IsVisible);
            Assert.Equal(!ready, studio.IsVisible);
            Assert.Equal(ready, palette?.IsVisible == true);
            var added = MediaLibrary.List().Where(item => !before.Contains(item.Path)).ToArray();
            if (save)
            {
                using var result = SKBitmap.Decode(Assert.Single(added).Path);
                Assert.Equal((64, 32), (result.Width, result.Height));
                Assert.NotEqual(SKColors.White, result.GetPixel(5, 5));
            }
            else Assert.Empty(added);
        }
        finally
        {
            studio.Close();
            foreach (var item in MediaLibrary.List().Where(item => !before.Contains(item.Path))) MediaLibrary.Delete(item.Path);
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Video_close_confirmation_keeps_working_or_finishes_with_the_selected_action(bool save)
    {
        string temporary = MediaLibrary.TemporaryFile(".mp4");
        using (var resource = AssetLoader.Open(new Uri("avares://ThixShot/Assets/StudioDemo.mp4")))
        using (var output = File.Create(temporary)) resource.CopyTo(output);
        var path = MediaLibrary.Import(temporary, "Dialogtest");
        var owner = new Window(); owner.Show();
        try
        {
            var editor = new VideoEditorWindow(path, new VideoInfo(6, 1280, 720), VideoProjects.Read(path));
            var closed = editor.ShowDialog(owner);
            StudioTests.Click(editor, "Zoom hinzufügen");
            var confirm = AskToClose(editor);
            StudioTests.Click(confirm, "Weiter bearbeiten");
            Assert.True(editor.IsVisible);
            Assert.Empty(VideoProjects.Read(path).Zooms);

            confirm = AskToClose(editor);
            StudioTests.Click(confirm, save ? "Speichern" : "Verwerfen");
            await closed.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(save ? 1 : 0, VideoProjects.Read(path).Zooms.Length);
        }
        finally { owner.Close(); MediaLibrary.Delete(path); }
    }

    private static Window AskToClose(Window editor)
    {
        editor.Close();
        Dispatcher.UIThread.RunJobs();
        var confirm = Assert.Single(editor.OwnedWindows);
        editor.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(confirm, Assert.Single(editor.OwnedWindows));
        Assert.Same(editor, confirm.Owner);
        Assert.True(confirm.IsVisible);
        Assert.True(confirm.ShowActivated);
        Assert.False(confirm.Topmost);
        Assert.False(confirm.ShowInTaskbar);
        return confirm;
    }
}
