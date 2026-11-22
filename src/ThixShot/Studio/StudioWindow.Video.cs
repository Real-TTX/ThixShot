using System.Diagnostics;
using Avalonia.Controls;
using MatDesktop;
using Avalonia.Platform;
using ThixShot.Capture;
using ThixShot.Library;
using ThixShot.Video;

namespace ThixShot.Studio;

internal sealed partial class StudioWindow
{
    private async Task SaveRecordingProjectAsync(string unzoomedPath, IReadOnlyList<ZoomPoint> initialZooms)
    {
        var path = MediaLibrary.Import(unzoomedPath, "Video");
        selectedPath = path;
        var zooms = initialZooms;
        if (initialZooms.Count > 0)
        {
            var media = await VideoEditService.InspectAsync(path, lifetime.Token);
            zooms = initialZooms.Where(z => media.DurationSeconds - z.StartSeconds > .01)
                .Select(z => z with { DurationSeconds = Math.Min(z.DurationSeconds, media.DurationSeconds - z.StartSeconds) }).ToArray();
        }
        VideoProjects.Save(path, VideoProjects.Read(path), zooms, newRecording: true);
        ShowMessage("Video gespeichert. Zooms lassen sich unter „Video bearbeiten“ in der Mediathek ändern.");
    }

    private async Task EditSampleVideoAsync()
    {
        string temp = MediaLibrary.TemporaryFile(".mp4");
        try
        {
            using var source = AssetLoader.Open(new Uri("avares://ThixShot/Assets/StudioDemo.mp4"));
            await using (var target = File.Create(temp)) await source.CopyToAsync(target, lifetime.Token);
            string path = MediaLibrary.Import(temp, "Studio-Demo");
            selectedPath = path;
            var demo = new[] { new ZoomPoint(1.05, 1.65, .773, .665, 1.7), new ZoomPoint(3.75, 1.65, .141, .191, 1.8) };
            VideoProjects.Save(path, VideoProjects.Read(path), demo, newRecording: true);
            var item = MediaLibrary.List().First(media => media.Path == path);
            await EditVideoAsync(item);
        }
        finally { File.Delete(temp); }
    }

    private async Task EditVideoAsync(MediaItem item)
    {
        var path = MediaLibrary.RequireItem(item.Path);
        var project = VideoProjects.Read(path);
        var info = await VideoEditService.InspectAsync(path, lifetime.Token);
        if (!double.IsFinite(info.DurationSeconds) || info.DurationSeconds <= 0 || info.Width <= 0 || info.Height <= 0)
            throw new InvalidDataException("Die Aufnahme enthält keine lesbare Videospur.");
        await new VideoEditorWindow(path, info, project).ShowDialog(this);
    }

    private async Task PlayVideoAsync(MediaItem item)
    {
        var path = MediaLibrary.RequireItem(item.Path);
        var file = await PreparePlaybackAsync(path);
        Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        ShowMessage("");
    }

    private async Task<string> PreparePlaybackAsync(string path)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var progressBar = new ProgressBar { Minimum = 0, Maximum = 1, IsIndeterminate = true, Height = 8 }.Themed("MatProgress");
        var panel = Ui.Stack(12, Ui.Text("Video wird vorbereitet", 18), Ui.Text(Path.GetFileName(path), 13, Ui.Muted), progressBar);
        var dialog = Dialog("Zooms rendern", panel);
        panel.Children.Add(Ui.Button("Abbrechen", null, dialog.Close));
        var work = VideoProjects.PlaybackPathAsync(path, new Progress<double>(value =>
        { progressBar.IsIndeterminate = false; progressBar.Value = value; }), cancel.Token);
        if (work.IsCompleted) return await work;
        var open = dialog.ShowDialog(this);
        dialog.Closed += (_, _) => cancel.Cancel();
        try { return await work; }
        finally { if (dialog.IsVisible) dialog.Close(); await open; }
    }
}
