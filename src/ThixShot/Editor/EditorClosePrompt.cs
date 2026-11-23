using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MatDesktop;
using ThixShot.Studio;

namespace ThixShot.Editor;

internal sealed class EditorClosePrompt
{
    private enum Choice { Continue, Save, Discard }

    private bool pending;
    private Window? dialog;

    public void Request(Window editor, string message, Action saveAndClose, Action discard)
    {
        if (pending)
        {
            dialog?.Activate();
            return;
        }

        pending = true;
        // Complete the native close event before showing its modal child. Otherwise Windows may re-activate the editor.
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (!editor.IsVisible) return;
                var prompt = new MatWindow
                {
                    Title = "Änderungen speichern?", Width = 490, SizeToContent = SizeToContent.Height,
                    CanResize = false, ShowInTaskbar = false, ShowActivated = true,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };
                dialog = prompt;
                var save = Ui.Button("Speichern", "save", () => prompt.Close(Choice.Save), "primary");
                var keep = Ui.Button("Weiter bearbeiten", null, () => prompt.Close(Choice.Continue));
                keep.IsCancel = true;
                var remove = Ui.Button("Verwerfen", null, () => prompt.Close(Choice.Discard));
                remove.Margin = new Thickness(8, 0, 0, 0);
                var details = Ui.Text(message, 14, Ui.Muted);
                details.TextWrapping = TextWrapping.Wrap;
                prompt.Content = new Border { Padding = new Thickness(22),
                    Child = Ui.Stack(14, Ui.Text("Änderungen speichern?", 19, weight: FontWeight.SemiBold),
                        details, Ui.Row(8, save, keep, remove)) }.Use(Border.BackgroundProperty, "Mat.Surface");
                var answer = prompt.ShowDialog<Choice>(editor);
                prompt.Activate();
                keep.Focus();
                switch (await answer)
                {
                    case Choice.Save: saveAndClose(); break;
                    case Choice.Discard: discard(); break;
                }
            }
            finally
            {
                pending = false;
                dialog = null;
                if (editor.IsVisible) editor.Activate();
            }
        });
    }
}
