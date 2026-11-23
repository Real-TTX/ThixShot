using Avalonia.Platform.Storage;

namespace ThixShot.Editor;

internal sealed partial class EditorWindow
{
    private void SaveLibrary()
    {
        if (outputBusy) return;
        try
        {
            CommitText();
            savedPath = ImageOutput.SaveToLibrary(document);
            canClose = true;
            Close(savedPath);
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
}
