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
    // The clicks and the typing stay with the recording (shown as rings and on the timeline, editable later); with Fokus-Zoom
    // they also become the first zooms.
    private async Task SaveRecordingProjectAsync(string unzoomedPath, IReadOnlyList<InputEvent> activity, bool autoZoom)
    {
        var path = MediaLibrary.Import(unzoomedPath, "Video");
        selectedPath = path;
        IReadOnlyList<ZoomPoint> zooms = [];
        if (activity.Count > 0)
        {
            var media = await VideoEditService.InspectAsync(path, lifetime.Token);
            activity = activity.Where(a => a.Seconds < media.DurationSeconds).ToArray();
            if (autoZoom) zooms = AutoZoom.Build(activity, media.DurationSeconds);
        }
        VideoProjects.Save(path, VideoProjects.Read(path), new VideoEdit(zooms, activity, ShowClicks: true), newRecording: true);
        ShowMessage("Video gespeichert. Zooms und Klicks lassen sich unter „Video bearbeiten“ in der Mediathek ändern.");
    }

    // What the demo clip shows: a click at the lower right, typing there, then a click at the upper left.
    internal static readonly InputEvent[] DemoActivity =
    [
        new(1.3, InputKind.Click, .773, .665),
        new(1.7, InputKind.Typing, .773, .665), new(1.9, InputKind.Typing, .773, .665), new(2.1, InputKind.Typing, .773, .665),
        new(2.5, InputKind.Typing, .773, .665),
        new(3.9, InputKind.Click, .141, .191)
    ];

    private async Task EditSampleVideoAsync()
    {
        string temp = MediaLibrary.TemporaryFile(".mp4");
        try
        {
            using var source = AssetLoader.Open(new Uri("avares://ThixShot/Assets/StudioDemo.mp4"));
            await using (var target = File.Create(temp)) await source.CopyToAsync(target, lifetime.Token);
            string path = MediaLibrary.Import(temp, "Studio-Demo");
            selectedPath = path;
            VideoProjects.Save(path, VideoProjects.Read(path), new VideoEdit(AutoZoom.Build(DemoActivity, 6), DemoActivity, ShowClicks: true), newRecording: true);
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
