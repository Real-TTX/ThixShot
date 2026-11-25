using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Avalonia.VisualTree;
using ThixShot.Capture;
using ThixShot.Library;
using ThixShot.Video;

namespace ThixShot.Tests;

public sealed class VideoProjectTests
{
    private static string NewDemo()
    {
        var temp = MediaLibrary.TemporaryFile(".mp4");
        using var resource = AssetLoader.Open(new Uri("avares://ThixShot/Assets/StudioDemo.mp4"));
        using (var stream = File.Create(temp)) resource.CopyTo(stream);
        return MediaLibrary.Import(temp,"Projekttest");
    }

    [AvaloniaFact]
    public async Task Zooms_survive_rename_and_stale_edits_are_rejected_without_changing_the_master()
    {
        var path = NewDemo();
        try
        {
            var bytes = File.ReadAllBytes(path);
            var baseline = VideoProjects.Read(path);
            var point = new ZoomPoint(1,2,.8,.35,2);
            var edited = VideoProjects.Save(path,baseline,[point],true);
            Assert.Throws<IOException>(() => VideoProjects.Save(path,baseline,[]));
            string renamed = "Projekt " + Guid.NewGuid().ToString("N");
            MediaLibrary.Rename(path,renamed);
            path = Path.Combine(MediaLibrary.Folder,renamed+".mp4");
            Assert.Equal(point,Assert.Single(VideoProjects.Read(path).Zooms));
            Assert.Equal(bytes,File.ReadAllBytes(path));
            VideoProjects.Save(path,edited,[]);
            await using var export = await MediaLibrary.OpenExportAsync(path);
            Assert.Equal(path,export.Name);
        }
        finally { MediaLibrary.Delete(path); }
    }

    [AvaloniaFact]
    public async Task Playback_uses_the_current_renderer_and_preserves_the_master_and_zoom_revision()
    {
        var path = NewDemo();
        try
        {
            var original = File.ReadAllBytes(path);
            var point = new ZoomPoint(1, 2, .5, .5, 1.55);
            var project = VideoProjects.Save(path, VideoProjects.Read(path), [point], true);
            var directory = Path.Combine(MediaLibrary.Folder, ".edits");
            // Renders of earlier versions (zooms without panning, no click rings) are never shown again.
            var oldRenders = new[] { $"{project.Revision:N}.mp4", $"{project.Revision:N}.win-mf-v2.mp4" }.Select(name => Path.Combine(directory, name)).ToArray();
            foreach (var old in oldRenders) File.WriteAllBytes(old, [1, 2, 3]);

            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    VideoProjects.PlaybackPathAsync(path, null, canceled.Token));
            }
            Assert.All(oldRenders, old => Assert.True(File.Exists(old)));
            var current = Path.Combine(directory, $"{project.Revision:N}.v3.mp4");
            File.WriteAllBytes(current, [4, 5, 6]);
            Assert.Equal(current, await VideoProjects.PlaybackPathAsync(path, null, default));
            Assert.All(oldRenders, old => Assert.False(File.Exists(old)));
            // Older builds may regenerate their cache after the upgrade.
            File.WriteAllBytes(oldRenders[0], [7, 8, 9]);
            Assert.Equal(current, await VideoProjects.PlaybackPathAsync(path, null, default));
            Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(current));
            Assert.False(File.Exists(oldRenders[0]));
            var after = VideoProjects.Read(path);
            Assert.Equal(project.Revision, after.Revision);
            Assert.Equal(point, Assert.Single(after.Zooms));
            Assert.Equal(original, File.ReadAllBytes(path));
            VideoProjects.Save(path, after, []);
            Assert.Empty(Directory.EnumerateFiles(directory, $"{project.Revision:N}*.mp4"));
        }
        finally { MediaLibrary.Delete(path); }
    }

    [AvaloniaFact]
    public async Task Editor_adds_a_zoom_at_the_playhead_and_saves_the_new_project()
    {
        var path = NewDemo();
        var owner = new Window(); owner.Show();
        var project = VideoProjects.Save(path,VideoProjects.Read(path),[],true);
        var editor = new VideoEditorWindow(path,new VideoInfo(6,1280,720),project);
        try
        {
            var open = editor.ShowDialog(owner);
            using var initial = editor.CaptureRenderedFrame();
            StudioTests.Click(editor,"Zoom hinzufügen");
            var surface = Assert.Single(editor.GetVisualDescendants().OfType<VideoCanvas>());
            Assert.NotNull(surface.Target);
            surface.PlaceTarget!(.77,.31);
            StudioTests.Click(editor,"Projekt speichern");
            var zoom = Assert.Single(VideoProjects.Read(path).Zooms);
            Assert.Equal(.77,zoom.X); Assert.Equal(.31,zoom.Y);
            var output = Environment.GetEnvironmentVariable("THIXSHOT_QA_DIR");
            if (!string.IsNullOrEmpty(output))
            {
                var timeline = Assert.Single(editor.GetVisualDescendants().OfType<ZoomTimeline>());
                for (int attempt=0; attempt<100 && timeline.Frames.Any(frame => frame==null); attempt++)
                    await Task.Delay(80);
                using var frame = editor.CaptureRenderedFrame();
                frame!.Save(Path.Combine(output,"videoeditor.png"));
            }
            editor.Close();
            await open;
        }
        finally { owner.Close(); MediaLibrary.Delete(path); }
    }
}
