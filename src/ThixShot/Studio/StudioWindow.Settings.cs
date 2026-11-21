using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ThixShot.Integration;

namespace ThixShot.Studio;

internal sealed partial class StudioWindow
{
    private static TextBlock Copy(string text)
    {
        var label = Ui.Text(text, 14, Ui.Muted);
        label.TextWrapping = TextWrapping.Wrap;
        return label;
    }

    private Control CloudPage()
    {
        bool connected = CloudService.IsConnected;
        var state = Ui.Stack(9, Ui.Icon("cloud", 32, Ui.Accent), Ui.Text(connected ? "Gerät verbunden" : "ThixCloud verbinden", 22, weight: FontWeight.SemiBold),
            Copy(connected ? CloudService.ServerUrl : "Verbinde dieses Gerät über deinen Browser mit deinem ThixCloud-Konto."));
        state.Children.Add(Copy("Wähle in der Mediathek eine Aufnahme und anschließend „In ThixCloud sichern“. Sie wird im Ordner ThixShot deines ThixDrive gespeichert."));
        var action = connected ? Ui.Button("Gerät trennen", null, () => Run(async () =>
        { await CloudService.DisconnectAsync(); ShowMessage("Cloud-Verbindung getrennt."); }))
            : Ui.Button("Gerät verbinden", "cloud", () => Run(ConnectCloudAsync), "primary");
        action.HorizontalAlignment = HorizontalAlignment.Left; state.Children.Add(action);
        return Ui.Stack(20, Heading("ThixCloud", "Deine Aufnahmen in der eigenen Cloud speichern."), Ui.Card(state));
    }

    private async Task ConnectCloudAsync()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var address = new TextBox { Watermark = "https://cloud.example.com" };
        var feedback = Copy("Gib die Adresse deiner ThixCloud-Instanz ein.");
        var code = Ui.Text("", 24, Ui.Accent, FontWeight.Bold);
        var body = Ui.Stack(12, Ui.Text("Gerät verbinden", 22, weight: FontWeight.SemiBold), feedback, address, code);
        var dialog = Dialog("ThixCloud · Gerät verbinden", body);
        var start = new Button { Content = "Im Browser anmelden" }; start.Classes.Add("primary");
        var cancelButton = Ui.Button("Abbrechen", null, dialog.Close);
        body.Children.Add(Ui.Row(8, start, cancelButton));
        Task? connecting = null;
        start.Click += async (_, _) =>
        {
            if (connecting is { IsCompleted: false }) return;
            start.IsEnabled = false; address.IsEnabled = false;
            connecting = CloudService.ConnectAsync(address.Text ?? "", (pairCode, uri) =>
                Dispatcher.UIThread.Post(() =>
                {
                    if (cancel.IsCancellationRequested) return;
                    code.Text = pairCode;
                    feedback.Text = "Bestätige diesen Code in deinem Browser.";
                    try { OpenBrowser(uri); } catch { feedback.Text = $"Öffne {uri} im Browser und bestätige den Code."; }
                }), cancel.Token);
            try { await connecting; if (dialog.IsVisible) dialog.Close(); ShowMessage("Dieses Gerät ist mit ThixCloud verbunden."); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            catch (Exception ex) { feedback.Text = ex.Message; code.Text = ""; }
            finally { start.IsEnabled = true; address.IsEnabled = true; }
        };
        dialog.Closed += (_, _) => cancel.Cancel();
        await dialog.ShowDialog(this);
        if (connecting != null) { try { await connecting; } catch { /* Already reported in the dialog. */ } }
    }

    private async Task UploadAsync(string path)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var bar = new ProgressBar { Maximum = 1, Height = 8, IsIndeterminate = true };
        var label = Copy(Path.GetFileName(path));
        var body = Ui.Stack(13, Ui.Text("In ThixCloud sichern", 21, weight: FontWeight.SemiBold), label, bar);
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
        var automatic = Ui.Check("ThixShot bei der Anmeldung starten", AutostartService.IsEnabled(), v =>
            Run(() => { AutostartService.SetEnabled(v); return Task.CompletedTask; }));
        var system = Ui.Card(Ui.Stack(12, Ui.Text("Benutzeranmeldung", 18, weight: FontWeight.SemiBold), automatic));
        var autoUpdate = Ui.Check("Updates automatisch laden und installieren", UpdateService.AutoUpdate,
            v => Run(() => { UpdateService.AutoUpdate = v; return Task.CompletedTask; }));
        autoUpdate.IsEnabled = UpdateService.CanUpdate;
        var check = Ui.Button("Nach Updates suchen", "download", () => Run(async () =>
        {
            pendingVersion = await UpdateService.CheckAsync(lifetime.Token);
            ShowMessage(pendingVersion == null ? "ThixShot ist aktuell." : $"Update {pendingVersion} verfügbar.");
        }));
        check.IsEnabled = UpdateService.CanUpdate;
        var update = Ui.Stack(12, Ui.Text("Updates", 18, weight: FontWeight.SemiBold), Copy($"Installierte Version {UpdateService.DisplayVersion}"),
            autoUpdate, Copy(UpdateService.CanUpdate ? "Automatische Updates werden im Leerlauf installiert. ThixShot startet danach neu."
                : "Updates stehen bei Installation aus dem Release- oder Nightly-Kanal zur Verfügung."));
        var buttons = Ui.Row(8);
        if (pendingVersion != null) buttons.Children.Add(Ui.Button($"{pendingVersion} installieren", "download", () => Run(InstallUpdateAsync), "primary"));
        buttons.Children.Add(check); update.Children.Add(buttons);
        var tabs = new TabControl { ItemsSource = new[] { new TabItem { Header = "Allgemein", Content = system },
            new TabItem { Header = "Updates", Content = Ui.Card(update) } }, SelectedIndex = settingsTab };
        tabs.SelectionChanged += (_, _) => { if (tabs.SelectedIndex >= 0) settingsTab = tabs.SelectedIndex; };
        return Ui.Stack(18, Heading("Einstellungen", "Startverhalten und Updates für dieses Gerät."), tabs);
    }
    private int settingsTab;
    private async Task AutomaticUpdateAsync()
    {
        if (busy || !UpdateService.ShouldCheckAutomatically) return;
        try
        {
            var version = await UpdateService.CheckAsync(lifetime.Token);
            if (version == null || busy || !UpdateService.ShouldCheckAutomatically) return;
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
        var mark = new Image { Source = Ui.Asset("Mark.png"), Width = 62, Height = 62, HorizontalAlignment = HorizontalAlignment.Left };
        return Ui.Stack(20, Heading("Über ThixShot", "Screenshot- und Aufnahmestudio für Windows und macOS."),
            Ui.Card(Ui.Stack(12, mark, Ui.Text("ThixShot", 26, weight: FontWeight.Bold), Ui.Text(UpdateService.DisplayVersion, 15, Ui.Accent),
                Copy("Eine Anwendung von Matthias Schmoldt für ThixApps.de."),
                Ui.Row(8, Ui.Button("ThixApps.de", "arrow", () => OpenBrowser(new Uri("https://thixapps.de"))),
                    Ui.Button("GitHub", "code", () => OpenBrowser(new Uri("https://github.com/Real-TTX/ThixShot")))))));
    }
}
