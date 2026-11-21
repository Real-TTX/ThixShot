using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MatDesktop;
using ThixShot.Library;
using ThixShot.Studio;

namespace ThixShot.Tests;

public sealed class PageTests
{
    [AvaloniaFact]
    public void Settings_uses_the_shared_appearance_and_cloud_has_a_labelled_server_field()
    {
        var studio = new StudioWindow();
        var original = StudioAppearance.Current;
        try
        {
            studio.Show();
            using var first = studio.CaptureRenderedFrame();
            StudioTests.Click(studio, "Einstellungen");
            var choice = Assert.Single(studio.GetVisualDescendants().OfType<ComboBox>(),
                control => AutomationProperties.GetName(control) == "Erscheinungsbild");
            choice.SelectedIndex = (int)MatAppearance.Dark;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(MatAppearance.Dark, StudioAppearance.Current);
            Assert.Equal(ThemeVariant.Dark, studio.ActualThemeVariant);
            Snapshot(studio, "settings-dark");
            StudioTests.Click(studio, "Mit Cloud verbinden");
            var server = Assert.Single(studio.GetVisualDescendants().OfType<MatFormField>());
            Assert.Equal("Adresse deiner ThixCloud", server.Label);
            Assert.IsType<TextBox>(server.Content);
        }
        finally { StudioAppearance.Change(original); studio.Close(); }
    }

    [AvaloniaFact]
    public void Library_filters_the_list_and_shows_the_selected_capture()
    {
        var studio = new StudioWindow();
        string? saved = null;
        try
        {
            studio.Show();
            using var first = studio.CaptureRenderedFrame();
            Assert.NotNull(first);
            var staging = MediaLibrary.TemporaryFile(".png");
            first.Save(staging);
            saved = MediaLibrary.Import(staging, "Aufnahmestudio");
            StudioTests.Click(studio, "Mediathek");
            var list = Assert.Single(studio.GetVisualDescendants().OfType<ListBox>(),
                control => AutomationProperties.GetName(control) == "Aufnahmen");
            Assert.Single(list.Items);
            var facts = Assert.Single(studio.GetVisualDescendants().OfType<MatFacts>());
            Assert.Equal("PNG", facts.ValueOf("Format"));
            Snapshot(studio, "library");
            var formats = Assert.Single(studio.GetVisualDescendants().OfType<ComboBox>(),
                control => AutomationProperties.GetName(control) == "Format");
            formats.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            studio.UpdateLayout();
            Assert.DoesNotContain(studio.GetVisualDescendants().OfType<ListBox>(),
                control => AutomationProperties.GetName(control) == "Aufnahmen");
        }
        finally { studio.Close(); if (saved is not null) MediaLibrary.Delete(saved); }
    }

    private static void Snapshot(Window window, string name)
    {
        var folder = Environment.GetEnvironmentVariable("THIXSHOT_QA_DIR");
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(folder, name + ".png"));
    }
}
