using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MatDesktop;
using ThixShot.Studio;

namespace ThixShot.Editor;

internal sealed partial class EditorWindow
{
    private void SaveLibrary()
    {
        if (outputBusy) return;
        try
        {
            CommitText();
            var destination = ImageOutput.SaveToLibrary(document);
            canClose = true;
            Close(destination);
        }
        catch (Exception error) { hint.Text = error.Message; }
    }

    private async Task CopyAsync()
    {
        if (outputBusy) return;
        outputBusy = true;
        try
        {
            if (Clipboard is null) throw new InvalidOperationException("Die Zwischenablage ist nicht verfügbar.");
            CommitText();
            using var encoded = ImageEditor.Render(document);
            await ImageOutput.CopyAsync(Clipboard, encoded.ToArray());
            hint.Text = "Bild in die Zwischenablage kopiert.";
        }
        catch (Exception error) { hint.Text = error.Message; }
        finally { outputBusy = false; }
    }

    private async Task ExportAsync()
    {
        if (outputBusy) return;
        outputBusy = true;
        try
        {
            CommitText();
            var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Screenshot als PNG exportieren", DefaultExtension = "png",
                SuggestedFileName = $"Screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png",
                FileTypeChoices = [new FilePickerFileType("PNG-Bild") { Patterns = ["*.png"], MimeTypes = ["image/png"], AppleUniformTypeIdentifiers = ["public.png"] }]
            });
            if (destination is null) return;
            using var encoded = ImageEditor.Render(document);
            var exportedState = document.History.ToArray();
            await ImageOutput.ExportAsync(encoded, destination);
            savedState = exportedState;
            hint.Text = $"{destination.Name} exportiert.";
        }
        catch (Exception error) { hint.Text = error.Message; }
        finally { outputBusy = false; }
    }

    private async Task ConfirmCloseAsync()
    {
        confirming = true;
        try
        {
            var dialog = new MatWindow { Title = "Änderungen speichern?", Width = 490, SizeToContent = SizeToContent.Height,
                CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var heading = Ui.Text("Änderungen speichern?", 19, weight: FontWeight.SemiBold);
            var body = Ui.Text("Der Screenshot enthält ungespeicherte Bearbeitungen.", 14, Ui.Muted);
            body.TextWrapping = TextWrapping.Wrap;
            var actions = Ui.Row(8, Ui.Button("Speichern", "save", () => dialog.Close("save"), "primary"),
                Ui.Button("Weiter bearbeiten", null, () => dialog.Close("continue")));
            var discard = Ui.Button("Verwerfen", null, () => dialog.Close("discard"));
            discard.Margin = new Thickness(8, 0, 0, 0);
            actions.Children.Add(discard);
            dialog.Content = new Border { Padding = new Thickness(22), Child = Ui.Stack(14, heading, body, actions) }
                .Use(Border.BackgroundProperty, "Mat.Surface");
            var answer = await dialog.ShowDialog<string?>(this);
            if (answer == "save") SaveLibrary();
            if (answer == "discard") { canClose = true; Close(); }
        }
        finally { confirming = false; }
    }
}
