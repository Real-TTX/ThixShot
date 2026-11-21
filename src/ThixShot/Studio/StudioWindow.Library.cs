using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using ThixShot.Integration;
using ThixShot.Library;

namespace ThixShot.Studio;

internal sealed partial class StudioWindow
{
    private int pageNumber;

    private Control LibraryPage(IReadOnlyList<MediaItem> all)
    {
        var rows = Ui.Stack(0);
        var pager = Ui.Row(8);
        var filter = new TextBox { Watermark = "Aufnahmen durchsuchen", Text = search };
        void Display(bool reset)
        {
            if (reset) pageNumber = 0;
            rows.Children.Clear(); pager.Children.Clear(); ClearImages();
            var filtered = all.Where(item => (kind == 0 || (kind == 2) == item.IsVideo) &&
                item.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase));
            var items = (sort switch
            {
                1 => filtered.OrderBy(item => item.Created),
                2 => filtered.OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase),
                _ => filtered.OrderByDescending(item => item.Created)
            }).ToList();
            const int perPage = 10;
            int lastPage = Math.Max(0, (items.Count - 1) / perPage);
            pageNumber = Math.Clamp(pageNumber, 0, lastPage);
            if (items.Count == 0) rows.Children.Add(EmptyLibrary(all.Count > 0));
            foreach (var item in items.Skip(pageNumber * perPage).Take(perPage))
                rows.Children.Add(ItemRow(item));
            if (lastPage == 0) return;
            var previous = Ui.Button("Zurück", null, () => { pageNumber--; Display(false); });
            var next = Ui.Button("Weiter", null, () => { pageNumber++; Display(false); });
            previous.IsEnabled = pageNumber > 0; next.IsEnabled = pageNumber < lastPage;
            pager.Children.Add(previous); pager.Children.Add(Ui.Text($"{pageNumber+1} / {lastPage+1}", 12, Ui.Muted)); pager.Children.Add(next);
        }
        filter.TextChanged += (_, _) => { search = filter.Text ?? ""; Display(true); };
        var types = Ui.Choices(["Alle Formate","Screenshots","Videos"], kind, index => { kind = index; Display(true); });
        var order = Ui.Choices(["Neueste zuerst","Älteste zuerst","Name A–Z"], sort, index => { sort = index; Display(true); });
        Display(false);
        var toolbar = Ui.Columns("*,135,150", filter, types, order); toolbar.ColumnSpacing = 10;
        return Ui.Stack(15, Heading("Mediathek", "Aufnahmen öffnen, bearbeiten, exportieren und in ThixCloud sichern."),
            toolbar, Ui.Card(rows, 4), pager);
    }

    private Control ItemRow(MediaItem item)
    {
        Control thumb = Ui.Icon(item.IsVideo ? "play" : "image", 20, Ui.Accent);
        if (!item.IsVideo)
        {
            try
            {
                using var source = File.OpenRead(MediaLibrary.RequireItem(item.Path));
                var image = Bitmap.DecodeToWidth(source, 110);
                thumbnails.Add(image);
                thumb = new Image { Source = image, Stretch = Stretch.UniformToFill };
            }
            catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException) { }
        }
        var tile = new Border { Width = 58, Height = 42, CornerRadius = new CornerRadius(5), Background = Ui.Brush("#F6E9E1"),
            Child = thumb, ClipToBounds = true };
        var title = Ui.Text(item.Title, 14, weight: FontWeight.SemiBold);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var details = Ui.Stack(2, title, Ui.Text(item.Details, 12, Ui.Muted));
        var line = Ui.Columns("Auto,*,Auto", tile, details, Ui.Icon("right",15,Ui.Muted)); line.ColumnSpacing = 14;
        var button = Ui.Button("", null, () => Run(() => DetailsAsync(item)), "flat");
        button.Content = line; button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        return button;
    }

    private static Window Dialog(string title, Control content, double width = 530)
    {
        return new Window { Title = title, Icon = Ui.WindowIcon(), Content = new Border { Padding = new Thickness(22), Child = content },
            Width = width, CanResize = false, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
    }

    private async Task DetailsAsync(MediaItem item)
    {
        var path = MediaLibrary.RequireItem(item.Path);
        var name = new TextBox { Text = item.Title, MaxLength = 150 };
        var error = Ui.Text("", 12, Ui.Accent); error.TextWrapping = TextWrapping.Wrap; error.IsVisible = false;
        var body = Ui.Stack(10, Ui.Text(item.IsVideo ? "Video" : "Screenshot", 24, weight: FontWeight.SemiBold),
            Ui.Text(item.Details, 12, Ui.Muted), Ui.Text("Name", 12, Ui.Muted), name);
        var dialog = Dialog("Aufnahmedetails", body, 610);
        var open = Ui.Button(item.IsVideo ? "Abspielen" : "Bearbeiten", item.IsVideo ? "play" : "edit", () => dialog.Close("open"));
        var export = Ui.Button("Exportieren", "download", () => dialog.Close("export"));
        var cloud = Ui.Button("In ThixCloud sichern", "cloud", () => dialog.Close("cloud"));
        var tools = Ui.Row(6, open, export, cloud);
        if (!item.IsVideo) tools.Children.Add(Ui.Button("Kopieren", "copy", () => dialog.Close("copy")));
        body.Children.Add(tools);
        var remove = Ui.Button("Löschen", "trash", () => dialog.Close("delete")); remove.Margin = new Thickness(16,0,0,0);
        body.Children.Add(error);
        body.Children.Add(Ui.Row(6, Ui.Button("Speichern", "save", () =>
        {
            try { MediaLibrary.Rename(path,name.Text ?? ""); dialog.Close("saved"); }
            catch (Exception ex) { error.IsVisible = true; error.Text = ex.Message; }
        }, "primary"), Ui.Button("Zurück", null, dialog.Close), remove));
        var result = await dialog.ShowDialog<string?>(this);
        switch (result)
        {
            case "open" when item.IsVideo: MediaLibrary.OpenVideo(path); break;
            case "open":
                using (var source = File.OpenRead(MediaLibrary.RequireItem(path))) await EditAsync(source);
                break;
            case "export":
                var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Aufnahme exportieren",
                    SuggestedFileName = Path.GetFileName(path), FileTypeChoices = [new FilePickerFileType(item.IsVideo ? "MP4" : "PNG") { Patterns = [item.IsVideo ? "*.mp4" : "*.png"] }] });
                if (destination == null) break;
                var local = destination.TryGetLocalPath();
                if (local != null && string.Equals(Path.GetFullPath(local), path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    break;
                await using (var source = File.OpenRead(MediaLibrary.RequireItem(path)))
                await using (var target = await destination.OpenWriteAsync())
                {
                    if (target.CanSeek) target.SetLength(0);
                    await source.CopyToAsync(target);
                }
                ShowMessage("Aufnahme exportiert."); break;
            case "copy":
                if (Clipboard == null) throw new InvalidOperationException("Zwischenablage nicht verfügbar.");
                using (var image = new Bitmap(MediaLibrary.RequireItem(path))) await Clipboard.SetBitmapAsync(image);
                ShowMessage("Screenshot kopiert."); break;
            case "delete":
                var content = Ui.Stack(14, Ui.Text("Aufnahme endgültig löschen?", 20, weight: FontWeight.SemiBold), Ui.Text(item.Title));
                var confirm = Dialog("Löschen bestätigen", content);
                content.Children.Add(Ui.Row(18, Ui.Button("Behalten", null, () => confirm.Close(false)), Ui.Button("Löschen", "trash", () => confirm.Close(true))));
                if (await confirm.ShowDialog<bool>(this)) { MediaLibrary.Delete(path); ShowMessage("Aufnahme gelöscht."); }
                break;
            case "cloud":
                if (!CloudService.IsConnected) await ConnectCloudAsync();
                if (CloudService.IsConnected) await UploadAsync(path);
                break;
        }
    }
}
