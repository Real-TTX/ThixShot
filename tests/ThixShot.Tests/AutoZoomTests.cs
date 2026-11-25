using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Avalonia.VisualTree;
using SkiaSharp;
using ThixShot.Capture;
using ThixShot.Library;
using ThixShot.Studio;
using ThixShot.Video;

namespace ThixShot.Tests;

/// <summary>Zooms from clicks and typing (like a screen recorder for demos), the click rings, and editing both afterwards.</summary>
public sealed class AutoZoomTests
{
    private static InputEvent Click(double seconds, double x, double y) => new(seconds, InputKind.Click, x, y);
    private static InputEvent Key(double seconds, double x, double y) => new(seconds, InputKind.Typing, x, y);

    [Fact]
    public void Typing_keeps_the_zoom_of_the_click_before_it_until_the_typing_ends()
    {
        // a click into a text field, then 5 s of typing there
        var input = new List<InputEvent> { Click(1, .3, .4) };
        for (double t = 1.4; t <= 6.4; t += .2) input.Add(Key(t, .3, .4));

        var zoom = Assert.Single(AutoZoom.Build(input, 20));
        Assert.Equal(1 - AutoZoom.Lead, zoom.StartSeconds, 3);
        Assert.Equal(input[^1].Seconds + AutoZoom.Hold, zoom.StartSeconds + zoom.DurationSeconds, 3);
        foreach (double t in new[] { 1.5, 3.0, 5.0, 6.4 })
            Assert.Equal(1 / AutoZoom.Scale, VideoEditService.ViewportAt(t, [zoom]).Width, 6); // fully zoomed while typing
        Assert.Equal(1, VideoEditService.ViewportAt(9, [zoom]).Width);
    }

    [Fact]
    public void A_click_elsewhere_while_working_pans_there_without_zooming_out()
    {
        var input = new[] { Click(1, .2, .2), Key(1.5, .2, .2), Key(1.7, .2, .2), Click(3, .8, .8), Key(3.4, .8, .8) };
        var zooms = AutoZoom.Build(input, 20);

        Assert.Equal(2, zooms.Length);
        Assert.Equal(VideoEditService.Edge, zooms[0].StartSeconds + zooms[0].DurationSeconds - zooms[1].StartSeconds, 6);
        double from = zooms[1].StartSeconds, to = from + VideoEditService.Edge;
        var start = VideoEditService.ViewportAt(from + .01, zooms);
        var middle = VideoEditService.ViewportAt((from + to) / 2, zooms);
        var end = VideoEditService.ViewportAt(to - .01, zooms);
        foreach (var view in new[] { start, middle, end })
            Assert.Equal(1 / AutoZoom.Scale, view.Width, 6); // the size never changes during the pan
        Assert.True(start.X < middle.X && middle.X < end.X, "the picture pans from the left place to the right one");
    }

    [Fact]
    public void A_pause_zooms_out_and_activity_close_together_stays_one_zoom()
    {
        var input = new[] { Click(1, .5, .5), Click(2, .52, .5), Click(3.2, .48, .52), Click(10, .5, .5) };
        var zooms = AutoZoom.Build(input, 20);

        Assert.Equal(2, zooms.Length); // the first three are one zoom, the pause makes a second
        Assert.Equal(1, VideoEditService.ViewportAt(7, zooms).Width);
        Assert.Empty(AutoZoom.Build([], 20));
        Assert.Empty(AutoZoom.Build([Click(25, .5, .5)], 20)); // after the end of the video
    }

    [Fact]
    public void A_single_zoom_comes_in_and_goes_as_before_and_typing_becomes_bars()
    {
        var zoom = new ZoomPoint(2, 3, .5, .5, 2);
        Assert.Equal(1, VideoEditService.ViewportAt(1.9, [zoom]).Width);
        Assert.Equal(.5, VideoEditService.ViewportAt(3.5, [zoom]).Width, 6);
        var halfway = VideoEditService.ViewportAt(2 + VideoEditService.Edge / 2, [zoom]).Width;
        Assert.InRange(halfway, .5 + .01, 1 - .01);

        var bars = AutoZoom.Typing([Key(1, .5, .5), Key(1.2, .5, .5), Key(1.4, .5, .5), Key(5, .5, .5), Click(6, .5, .5)]);
        Assert.Equal(2, bars.Length);
        Assert.Equal(1, bars[0].StartSeconds);
        Assert.True(bars[0].EndSeconds > 1.4);
    }

    [Fact]
    public void A_click_ring_grows_and_fades_and_only_clicks_have_one()
    {
        var input = new[] { Click(1, .5, .5), Key(1, .5, .5) };
        Assert.Empty(ClickEffect.At(.99, input));
        var (_, _, small, strong) = Assert.Single(ClickEffect.At(1.05, input));
        var (_, _, large, weak) = Assert.Single(ClickEffect.At(1.5, input));
        Assert.True(large > small && weak < strong);
        Assert.Empty(ClickEffect.At(1 + ClickEffect.Duration, input));
    }

    private static string NewDemo()
    {
        var temp = MediaLibrary.TemporaryFile(".mp4");
        using var resource = AssetLoader.Open(new Uri("avares://ThixShot/Assets/StudioDemo.mp4"));
        using (var stream = File.Create(temp)) resource.CopyTo(stream);
        return MediaLibrary.Import(temp, "Klicktest");
    }

    [AvaloniaFact]
    public void The_project_keeps_clicks_typing_and_the_click_setting_and_reads_older_projects()
    {
        var path = NewDemo();
        try
        {
            var input = new[] { Click(1, .2, .3), Key(1.5, .2, .3) };
            var saved = VideoProjects.Save(path, VideoProjects.Read(path), new VideoEdit(AutoZoom.Build(input, 6), input, ShowClicks: false), true);
            var read = VideoProjects.Read(path);
            Assert.Equal(input, read.Activity);
            Assert.False(read.ShowClicks);
            Assert.Empty(read.VisibleClicks);
            Assert.Single(read.Zooms);

            // saving only zooms keeps the clicks
            read = VideoProjects.Save(path, read, []);
            Assert.Equal(input, read.Activity);
            Assert.Throws<ArgumentException>(() => VideoProjects.Save(path, read, new VideoEdit([], [Click(1, 2, .5)], true)));

            // a project of an older ThixShot has no clicks and shows clicks by default
            var older = JsonSerializer.Deserialize<VideoProject>("""{"Revision":"00000000-0000-0000-0000-000000000001","RecordedWithMaster":true,"SourceSize":1,"SourceTimestamp":2,"Zooms":[]}""")!;
            Assert.Empty(older.Activity);
            Assert.True(older.ShowClicks);
            Assert.DoesNotContain("VisibleClicks", JsonSerializer.Serialize(saved));
        }
        finally { MediaLibrary.Delete(path); }
    }

    [AvaloniaFact]
    public async Task The_editor_shows_the_clicks_makes_zooms_from_them_and_lets_them_be_hidden_or_removed()
    {
        var path = NewDemo();
        var owner = new Window(); owner.Show();
        var input = StudioWindow.DemoActivity;
        var project = VideoProjects.Save(path, VideoProjects.Read(path), new VideoEdit([], input, true), true);
        var editor = new VideoEditorWindow(path, new VideoInfo(6, 1280, 720), project);
        try
        {
            var open = editor.ShowDialog(owner);
            var timeline = Assert.Single(editor.GetVisualDescendants().OfType<ZoomTimeline>());
            var preview = Assert.Single(editor.GetVisualDescendants().OfType<VideoCanvas>());
            Assert.Equal(input.Length, timeline.Activity.Count);
            Assert.Equal(2, preview.Clicks.Count);

            StudioTests.Click(editor, "Automatisch zoomen");
            Assert.Equal(AutoZoom.Build(input, 6), timeline.Zooms);

            var show = editor.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Content as string == "Klicks im Video zeigen");
            show.IsChecked = false;
            Assert.Empty(preview.Clicks);
            Assert.False(timeline.ShowClicks);
            show.IsChecked = true;

            // select the second click on the timeline and remove it
            int second = Array.FindLastIndex(input, e => e.Kind == InputKind.Click);
            timeline.SelectActivity!(second);
            Assert.Equal(second, timeline.SelectedClick);
            StudioTests.Click(editor, "Klick entfernen");
            Assert.Single(preview.Clicks);

            StudioTests.Click(editor, "Projekt speichern");
            var saved = VideoProjects.Read(path);
            Assert.Equal(input.Length - 1, saved.Activity.Length);
            Assert.Equal(AutoZoom.Build(input, 6), saved.Zooms);
            Assert.True(saved.ShowClicks);

            var output = Environment.GetEnvironmentVariable("THIXSHOT_QA_DIR");
            if (!string.IsNullOrEmpty(output))
            {
                for (int attempt = 0; attempt < 100 && timeline.Frames.Any(frame => frame == null); attempt++)
                    await Task.Delay(80);
                using var frame = editor.CaptureRenderedFrame();
                frame!.Save(Path.Combine(output, "videoeditor-klicks.png"));
            }
            editor.Close();
            await open;
        }
        finally { owner.Close(); MediaLibrary.Delete(path); }
    }

    [AvaloniaFact]
    public async Task The_export_draws_the_ring_of_a_click_where_it_happened()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOSVersionAtLeast(14)) return;
        var path = NewDemo();
        var output = Path.Combine(Path.GetTempPath(), $"thixshot-click-{Guid.NewGuid():N}.mp4");
        try
        {
            var info = await VideoEditService.InspectAsync(path);
            var click = Click(1, .5, .5);
            await VideoEditService.ExportAsync(path, output, [], [click]);

            // 0.2 s after the click the ring is drawn at its radius right of the click
            double at = 1.2;
            var (_, _, radius, _) = Assert.Single(ClickEffect.At(at, [click]));
            int x = (int)(info.Width * .5 + radius * Math.Min(info.Width, info.Height)), y = info.Height / 2;
            using var before = SKBitmap.Decode(await VideoEditService.FrameAsync(path, at));
            using var after = SKBitmap.Decode(await VideoEditService.FrameAsync(output, at));
            var plain = before.GetPixel(x, y);
            var ring = after.GetPixel(x, y);
            Assert.True(ring.Blue - ring.Red > plain.Blue - plain.Red + 40, $"no ring at the click: {plain} -> {ring}");

            // far from the click the picture is unchanged
            var corner = after.GetPixel(20, 20);
            var original = before.GetPixel(20, 20);
            Assert.InRange(Math.Abs(corner.Blue - original.Blue), 0, 24);
        }
        finally { MediaLibrary.Delete(path); File.Delete(output); }
    }
}
