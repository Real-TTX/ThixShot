using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using SkiaSharp;
using ThixShot.Library;

namespace ThixShot.Editor;

internal static class ImageOutput
{
    public static async Task CopyAsync(IClipboard clipboard, byte[] png)
    {
        // Clipboard contents can be requested after the editor has closed.
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(DataFormat.Bitmap, () =>
        {
            using var stream = new MemoryStream(png, writable: false);
            return new Bitmap(stream);
        }));
        await clipboard.SetDataAsync(data);
        await clipboard.FlushAsync();
    }

    public static string SaveToLibrary(ImageDocument document)
    {
        var temporary = MediaLibrary.TemporaryFile(".png");
        try
        {
            using var encoded = ImageEditor.Render(document);
            using (var stream = File.Create(temporary)) encoded.SaveTo(stream);
            return MediaLibrary.Import(temporary, "Screenshot");
        }
        finally { File.Delete(temporary); }
    }

    public static async Task ExportAsync(SKData encoded, IStorageFile destination)
    {
        if (destination.TryGetLocalPath() is { } path)
        {
            WriteLocalFile(encoded, path);
            return;
        }
        await using var output = await destination.OpenWriteAsync();
        if (output.CanSeek) output.SetLength(0);
        using var input = encoded.AsStream();
        await input.CopyToAsync(output);
    }

    internal static void WriteLocalFile(SKData encoded, string path)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".thixshot-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                encoded.SaveTo(output);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
