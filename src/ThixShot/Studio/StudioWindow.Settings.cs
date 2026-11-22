using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MatDesktop;
using ThixShot.Integration;
using ThixShot.Library;

namespace ThixShot.Studio;

internal sealed partial class StudioWindow
{
    private static TextBlock Copy(string text) =>
        new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }.Themed("MatText.Muted");

    private static TextBlock PageTitle(string text) => new TextBlock { Text = text }.Themed("MatText.PageTitle");

    private static Border PageCard(Control content, double padding = 16) =>
        new Border { Child = content, Padding = new Thickness(padding) }.Themed("MatCard");

    private Control CloudPage()
    {
        var state = Ui.Stack(16, PageTitle("ThixCloud"));
        state.MaxWidth = 650;
        state.HorizontalAlignment = HorizontalAlignment.Left;
        if (CloudService.IsConnected)
        {
            state.Children.Add(PageCard(new MatFacts { Facts = [
                new("Cloud", CloudService.ServerUrl), new("Dieses Gerät", Environment.MachineName), new("Ablage", "ThixDrive / ThixShot")] }));
            state.Children.Add(Copy("Wähle in der Mediathek die Aufnahme, die du in ThixCloud sichern möchtest."));
            state.Children.Add(Ui.Row(8, Ui.Button("Gerät trennen", null, () => Run(async () =>
            { await CloudService.DisconnectAsync(); ShowMessage("Cloud-Verbindung getrennt."); }))));
            return state;
        }
        var address = new TextBox { Watermark = "https://cloud.example.com", MinWidth = 300 };
        state.Children.Add(new MatFormField { Label = "Adresse deiner ThixCloud", Content = address,
            Hint = "Verbinde dieses Gerät mit deinem Konto, um ausgewählte Aufnahmen in ThixDrive zu sichern." });
        state.Children.Add(Ui.Row(8, Ui.Button("Im Browser anmelden", "cloud", () => Run(() => ConnectCloudAsync(address.Text)), "primary")));
        return state;
    }

    private async Task ConnectCloudAsync(string? initialAddress = null)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var address = new TextBox { Text = initialAddress, Watermark = "https://cloud.example.com" };
        var field = new MatFormField { Label = "Adresse deiner ThixCloud", Content = address };
        var feedback = Copy("Gib die Adresse deiner ThixCloud-Instanz ein.");
        var code = new SelectableTextBlock { FontSize = MatTokens.Type.Fs2xl, FontWeight = FontWeight.SemiBold, LetterSpacing = 3 }
            .Use(TextBlock.FontFamilyProperty, "Mat.Mono").Use(TextBlock.ForegroundProperty, "Mat.AccentText");
        var codePanel = PageCard(Ui.Stack(8, Copy("Dein Code"), code));
        codePanel.IsVisible = false;
        var body = Ui.Stack(12, new TextBlock { Text = "Gerät verbinden" }.Themed("MatText.SectionTitle"), field, feedback, codePanel);
        var dialog = Dialog("ThixCloud · Gerät verbinden", body);
        var start = new Button { Content = "Im Browser anmelden" }.Themed("MatButton.Primary");
        var cancelButton = Ui.Button("Abbrechen", null, dialog.Close);
        body.Children.Add(Ui.Row(8, start, cancelButton));
        Task? connecting = null;
        async Task BeginPairing()
        {
            if (connecting is { IsCompleted: false }) return;
            start.IsEnabled = false; address.IsEnabled = false;
            connecting = CloudService.ConnectAsync(address.Text ?? "", (pairCode, uri) =>
                Dispatcher.UIThread.Post(() =>
                {
                    if (cancel.IsCancellationRequested) return;
                    code.Text = pairCode; codePanel.IsVisible = true;
                    feedback.Text = "Bestätige diesen Code in deinem Browser.";
                    try { OpenBrowser(uri); } catch { feedback.Text = $"Öffne {uri} im Browser und bestätige den Code."; }
                }), cancel.Token);
            try { await connecting; if (dialog.IsVisible) dialog.Close(); ShowMessage("Dieses Gerät ist mit ThixCloud verbunden."); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            catch (Exception ex) { feedback.Text = ex.Message; codePanel.IsVisible = false; }
            finally { start.IsEnabled = true; address.IsEnabled = true; }
        }
        start.Click += async (_, _) => await BeginPairing();
        if (!string.IsNullOrWhiteSpace(initialAddress)) dialog.Opened += async (_, _) => await BeginPairing();
        dialog.Closed += (_, _) => cancel.Cancel();
        await dialog.ShowDialog(this);
        if (connecting != null) { try { await connecting; } catch { /* Already reported in the dialog. */ } }
    }

    private async Task UploadAsync(string path)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var bar = new ProgressBar { Maximum = 1, Height = 8, IsIndeterminate = true };
        var label = Copy(Path.GetFileName(path));
        var body = Ui.Stack(13, new TextBlock { Text = "In ThixCloud sichern" }.Themed("MatText.SectionTitle"), label, bar);
        var dialog = Dialog("ThixCloud · Upload", body);
        body.Children.Add(Ui.Button("Abbrechen", null, dialog.Close));
        dialog.Closed += (_, _) => cancel.Cancel();
        var open = dialog.ShowDialog(this);
        try
        {
            var progress = new Progress<double>(value => { bar.IsIndeterminate = false; bar.Value = value; });
            await CloudService.UploadAsync(path, progress, cancel.Token);
            ShowMessage("In ThixDrive im Ordner ThixShot gesichert.");
        }
        finally { if (dialog.IsVisible) dialog.Close(); await open; }
    }

    private Control SettingsPage()
    {
        var automatic = SettingSwitch("Bei der Anmeldung starten", AutostartService.IsEnabled(), AutostartService.SetEnabled);
        var appearance = Ui.Choices(["Wie das System", "Hell", "Dunkel"], (int)StudioAppearance.Current,
            i => Run(() => { StudioAppearance.Change((MatAppearance)i); return Task.CompletedTask; }));
        appearance.MinWidth = 170;
        AutomationProperties.SetName(appearance, "Erscheinungsbild");
        var system = Ui.Stack(0, new MatSettingRow { Title = "Bei der Anmeldung starten",
            Description = "Öffnet das Aufnahmestudio bei deiner nächsten Benutzeranmeldung.", Content = automatic },
            new MatSettingRow { Title = "Erscheinungsbild", Content = appearance, BorderThickness = new Thickness(0) });
        var autoUpdate = SettingSwitch("Updates automatisch installieren", UpdateService.AutoUpdate, value => UpdateService.AutoUpdate = value);
        autoUpdate.IsEnabled = UpdateService.CanUpdate;
        var check = Ui.Button("Nach Updates suchen", "download", () => Run(async () =>
        {
            pendingVersion = await UpdateService.CheckAsync(lifetime.Token);
            ShowMessage(pendingVersion == null ? "ThixShot ist aktuell." : $"Update {pendingVersion} verfügbar.");
        }));
        check.IsEnabled = UpdateService.CanUpdate;
        var update = Ui.Stack(0, new MatSettingRow { Title = "Updates automatisch installieren", Content = autoUpdate,
            Description = "Lädt neue Versionen im Leerlauf. ThixShot startet nach der Installation neu." });
        var buttons = Ui.Row(8);
        if (pendingVersion != null) buttons.Children.Add(Ui.Button($"{pendingVersion} installieren", "download", () => Run(InstallUpdateAsync), "primary"));
        buttons.Children.Add(check);
        update.Children.Add(new MatSettingRow { Title = "Installierte Version", Description = UpdateService.CanUpdate ?
            UpdateService.DisplayVersion : $"{UpdateService.DisplayVersion} · Updates sind im Release- und Nightly-Kanal verfügbar.",
            Content = buttons, BorderThickness = new Thickness(0) });
        var tabs = new TabControl { ItemsSource = new[] { new TabItem { Header = "Allgemein", Content = system },
            new TabItem { Header = "Updates", Content = update } }, SelectedIndex = settingsTab };
        tabs.SelectionChanged += (_, _) => { if (tabs.SelectedIndex >= 0) settingsTab = tabs.SelectedIndex; };
        var settings = new Grid { ColumnDefinitions =
            { new ColumnDefinition(GridLength.Star) { MaxWidth = 820 }, new ColumnDefinition(GridLength.Auto) } };
        settings.Children.Add(Ui.Stack(12, PageTitle("Einstellungen"), tabs));
        return settings;
    }
    private ToggleSwitch SettingSwitch(string name, bool value, Action<bool> apply)
    {
        var toggle = new ToggleSwitch { OnContent = "", OffContent = "", IsChecked = value };
        AutomationProperties.SetName(toggle, name);
        toggle.IsCheckedChanged += (_, _) => Run(() => { apply(toggle.IsChecked == true); return Task.CompletedTask; });
        return toggle;
    }
    private int settingsTab;
    private async Task AutomaticUpdateAsync()
    {
        if (busy || !IsVisible || !UpdateService.ShouldCheckAutomatically) return;
        try
        {
            var version = await UpdateService.CheckAsync(lifetime.Token);
            if (version == null || busy || !IsVisible || !UpdateService.ShouldCheckAutomatically) return;
            pendingVersion = version;
            Run(InstallUpdateAsync);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowMessage(error.Message); }
    }
    private async Task InstallUpdateAsync()
    {
        ShowMessage("Update wird geladen und geprüft …");
        await UpdateService.InstallAsync(lifetime.Token);
        allowClose = true; Close();
    }
    private static void OpenBrowser(Uri uri) => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    private Control AboutPage()
    {
        var mark = new Image { Source = Ui.Asset("Mark.png"), Width = 64, Height = 64 };
        var name = PageTitle("");
        name.Inlines!.Add(new Avalonia.Controls.Documents.Run("Thix"));
        var suffix = new Avalonia.Controls.Documents.Run("Shot");
        suffix[!Avalonia.Controls.Documents.Run.ForegroundProperty] = name.GetResourceObservable("Mat.AccentText").ToBinding();
        name.Inlines.Add(suffix);
        var brand = Ui.Row(18, mark, Ui.Stack(4, name,
            Copy("Screenshot- und Aufnahmestudio für Windows und macOS."),
            new SelectableTextBlock { Text = MatVersion.Format(UpdateService.DisplayVersion, UpdateService.Channel) }));
        var updates = Ui.Button("Updates", "download", () => { settingsTab = 1; Navigate("Einstellungen"); });
        var updateStatus = new Border { Padding = new Thickness(14,12), Child = Ui.Columns("*,Auto",
            Ui.Stack(2, new TextBlock { Text = "Softwareupdates", FontWeight = FontWeight.SemiBold },
                Copy(pendingVersion is not null ? $"Version {pendingVersion} verfügbar." : UpdateService.CanUpdate ?
                    "Nach einer neuen Version suchen." : $"Build-Kanal {UpdateService.Channel}")), updates) }
            .Use(Border.BackgroundProperty, "Mat.Surface2").Use(Border.CornerRadiusProperty, "Mat.RadiusLg");
        var facts = new MatFacts { LabelWidth = 90, Facts = [new("Build", UpdateService.DisplayVersion, true),
            new("System", System.Runtime.InteropServices.RuntimeInformation.OSDescription), new("Daten", MediaLibrary.Folder)] };
        var info = PageCard(Ui.Stack(18, brand, updateStatus, facts, Copy("© Matthias Schmoldt. Gestaltung und Symbole von MatDesign.")), 24);
        var pageContent = Ui.Stack(18, PageTitle("Über ThixShot"), info,
            Ui.Row(8, Ui.Button("Projekt auf GitHub", "code", () => OpenBrowser(new Uri("https://github.com/Real-TTX/ThixShot"))),
                Ui.Button("ThixApps.de", "arrow", () => OpenBrowser(new Uri("https://thixapps.de")))));
        pageContent.MaxWidth = 640;
        pageContent.HorizontalAlignment = HorizontalAlignment.Left;
        return pageContent;
    }
}
