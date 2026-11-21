using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using MatDesktop;
using ThixShot.Integration;
using ThixShot.Library;

namespace ThixShot.Studio;

internal sealed partial class StudioWindow
{
    private int pageNumber;

    private Control LibraryPage(IReadOnlyList<MediaItem> all)
    {
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
        var list = new ListBox().Themed("MatList").Use(ItemsControl.ItemContainerThemeProperty, "MatCardRow");
        AutomationProperties.SetName(list, "Aufnahmen");
        var previews = new Dictionary<string, Bitmap?>();
        foreach (var item in items.Skip(pageNumber * perPage).Take(perPage))
        {
            previews[item.Path] = LoadPreview(item);
            list.Items.Add(new ListBoxItem { Tag = item, Content = RowContent(item, previews[item.Path]) }.Themed("MatCardRow"));
        }

        var pane = new Border { Padding = new Thickness(20, 0, 0, 0), BorderThickness = new Thickness(1,0,0,0) }
            .Use(Border.BorderBrushProperty, "Mat.Border");
        var detailsButton = Ui.Button("Details öffnen", "info", () => OpenSelected());
        void OpenSelected()
        {
            if (list.SelectedItem is ListBoxItem { Tag: MediaItem item }) Run(() => DetailsAsync(item));
        }
        void ShowSelected()
        {
            var item = (list.SelectedItem as ListBoxItem)?.Tag as MediaItem;
            detailsButton.IsEnabled = item is not null;
            if (item is null)
            {
                pane.Child = Copy("Wähle eine Aufnahme aus der Liste.");
                return;
            }
            selectedPath = item.Path;
            var preview = Preview(item, previews[item.Path], true);
            var facts = new MatFacts { LabelWidth = 105, Facts = [new("Format", item.IsVideo ? "MP4" : "PNG"),
                new("Erstellt", item.Created.ToString("g")), new("Dateigröße", $"{item.Bytes:N0} Bytes")] };
            var title = new TextBlock { Text = item.Title, TextWrapping = TextWrapping.Wrap }.Themed("MatText.SectionTitle");
            pane.Child = Ui.Stack(14, title, preview, Ui.Row(8, Ui.Button(item.IsVideo ? "Abspielen" : "Bearbeiten",
                item.IsVideo ? "play" : "edit", () => Run(() => MediaActionAsync(item, "open")), "primary"),
                Ui.Button("Exportieren", "download", () => Run(() => MediaActionAsync(item, "export")))), facts);
        }
        list.SelectionChanged += (_, _) => ShowSelected();
        list.DoubleTapped += (_, _) => OpenSelected();
        list.KeyDown += (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; OpenSelected(); } };
        list.SelectedItem = list.Items.OfType<ListBoxItem>().FirstOrDefault(row => ((MediaItem)row.Tag!).Path == selectedPath)
            ?? list.Items.OfType<ListBoxItem>().FirstOrDefault();
        ShowSelected();

        var types = Ui.Choices(["Alle Formate", "Screenshots", "Videos"], kind,
            index => { kind = index; pageNumber = 0; RefreshPage(); });
        types.MinWidth = 128;
        AutomationProperties.SetName(types, "Format");
        var order = Ui.Choices(["Neueste zuerst", "Älteste zuerst", "Name A–Z"], sort,
            index => { sort = index; pageNumber = 0; RefreshPage(); });
        order.MinWidth = 150;
        AutomationProperties.SetName(order, "Sortierung");
        var header = Ui.Columns("*,Auto", PageTitle("Mediathek"), Ui.Row(8, types, order));
        var actions = Ui.Columns("*,Auto", Ui.Row(8, detailsButton), Copy(items.Count == 1 ? "1 Aufnahme" : $"{items.Count} Aufnahmen"));
        var left = Ui.Stack(14, header, items.Count == 0 ? EmptyLibrary(all.Count > 0) : list, actions);
        if (lastPage > 0)
        {
            var previous = Ui.Button("Zurück", null, () => { pageNumber--; RefreshPage(); });
            var next = Ui.Button("Weiter", null, () => { pageNumber++; RefreshPage(); });
            previous.IsEnabled = pageNumber > 0;
            next.IsEnabled = pageNumber < lastPage;
            left.Children.Add(Ui.Row(8, previous, Copy($"{pageNumber + 1} / {lastPage + 1}"), next));
        }

        var columns = Ui.Columns("*,330", left, pane);
        columns.SizeChanged += (_, size) =>
        {
            var available = size.NewSize.Width;
            var width = available < 820 ? 0 : available < 1040 ? 330 : 400;
            columns.ColumnDefinitions[1].Width = new GridLength(width);
            columns.ColumnSpacing = width == 0 ? 0 : 18;
            pane.IsVisible = width > 0;
        };
        return columns;
    }

    private Bitmap? LoadPreview(MediaItem item)
    {
        if (item.IsVideo) return null;
        try
        {
            using var source = File.OpenRead(MediaLibrary.RequireItem(item.Path));
            var image = Bitmap.DecodeToWidth(source, 480);
            thumbnails.Add(image);
            return image;
        }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException) { return null; }
    }

    private static Border Preview(MediaItem item, Bitmap? bitmap, bool large)
    {
        Control image = bitmap is null
            ? new MatIcon { Icon = item.IsVideo ? "video" : "image", Size = large ? 38 : 20, HorizontalAlignment = HorizontalAlignment.Center }
                .Use(MatIcon.ForegroundProperty, "Mat.TextMuted")
            : new Image { Source = bitmap, Stretch = large ? Stretch.Uniform : Stretch.UniformToFill };
        return new Border { Width = large ? double.NaN : 100, Height = large ? 185 : 58, ClipToBounds = true, Child = image }
            .Use(Border.CornerRadiusProperty, "Mat.Radius").Use(Border.BackgroundProperty, "Mat.Surface2");
    }

    private static Control RowContent(MediaItem item, Bitmap? preview)
    {
        var title = new TextBlock { Text = item.Title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var labels = Ui.Stack(4, title, Copy(item.Details));
        labels.VerticalAlignment = VerticalAlignment.Center;
        var row = Ui.Columns("Auto,*", Preview(item, preview, false), labels);
        row.ColumnSpacing = 14;
        return row;
    }

    private Control ItemRow(MediaItem item)
    {
        var button = Ui.Button("", null, () => Run(() => DetailsAsync(item)), "flat");
        button.Content = RowContent(item, LoadPreview(item));
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        return button;
    }

    private static Window Dialog(string title, Control content, double width = 530)
    {
        var dialog = new MatWindow { Title = title, Content = new Border { Padding = new Thickness(24, 22), Child = content },
            ShowInTaskbar = false, Width = width, CanResize = false, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner }.Use(Window.BackgroundProperty, "Mat.Surface");
        dialog.KeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; dialog.Close(); } };
        return dialog;
    }

    private async Task DetailsAsync(MediaItem item)
    {
        var path = MediaLibrary.RequireItem(item.Path);
        var name = new TextBox { Text = item.Title, MaxLength = 150 };
        var field = new MatFormField { Label = "Name", Content = name };
        var body = Ui.Stack(12, new TextBlock { Text = item.IsVideo ? "Video" : "Screenshot" }.Themed("MatText.SectionTitle"),
            Copy(item.Details), field);
        var dialog = Dialog("Aufnahmedetails", body, 610);
        var open = Ui.Button(item.IsVideo ? "Abspielen" : "Bearbeiten", item.IsVideo ? "play" : "edit", () => dialog.Close("open"));
        var export = Ui.Button("Exportieren", "download", () => dialog.Close("export"));
        var cloud = Ui.Button("In ThixCloud sichern", "cloud", () => dialog.Close("cloud"));
        var tools = Ui.Row(6, open, export, cloud);
        if (!item.IsVideo) tools.Children.Add(Ui.Button("Kopieren", "copy", () => dialog.Close("copy")));
        body.Children.Add(tools);
        var remove = Ui.Button("Löschen", "trash", () => dialog.Close("delete")).Themed("MatButton.Danger");
        remove.Margin = new Thickness(16,0,0,0);
        body.Children.Add(Ui.Row(6, Ui.Button("Speichern", "save", () =>
        {
            try { MediaLibrary.Rename(path,name.Text ?? ""); dialog.Close("saved"); }
            catch (Exception ex) { field.Error = ex.Message; }
        }, "primary"), Ui.Button("Zurück", null, dialog.Close), remove));
        var result = await dialog.ShowDialog<string?>(this);
        await MediaActionAsync(item, result);
    }

    private async Task MediaActionAsync(MediaItem item, string? result)
    {
        if (result is null or "saved") return;
        var path = MediaLibrary.RequireItem(item.Path);
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
                var content = Ui.Stack(14, new TextBlock { Text = "Aufnahme endgültig löschen?" }.Themed("MatText.SectionTitle"), Copy(item.Title));
                var confirm = Dialog("Löschen bestätigen", content);
                content.Children.Add(Ui.Row(18, Ui.Button("Behalten", null, () => confirm.Close(false)),
                    Ui.Button("Löschen", "trash", () => confirm.Close(true)).Themed("MatButton.Danger")));
                if (await confirm.ShowDialog<bool>(this)) { MediaLibrary.Delete(path); ShowMessage("Aufnahme gelöscht."); }
                break;
            case "cloud":
                if (!CloudService.IsConnected) await ConnectCloudAsync();
                if (CloudService.IsConnected) await UploadAsync(path);
                break;
        }
    }
}
