using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpGen.Runtime;
using SkiaSharp;
using Vortice.MediaFoundation;

namespace ThixShot.Capture;

[SupportedOSPlatform("windows")]
internal static class WindowsVideoEditor
{
    public static VideoInfo Inspect(string path)
    {
        using var source = new VideoSource(path);
        return source.Info;
    }

    public static byte[] Frame(string path, double seconds, CancellationToken token)
    {
        using var source = new VideoSource(path);
        long target = Math.Min(TimeSpan.FromSeconds(source.Info.DurationSeconds).Ticks - 1,
            (long)(Math.Min(seconds, source.Info.DurationSeconds) * TimeSpan.TicksPerSecond));
        using var image = SKImage.FromBitmap(source.SeekFrame(target, token));
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    public static void Export(string path, string output, ZoomPoint[] points, IProgress<double>? progress, CancellationToken token)
    {
        using var source = new VideoSource(path);
        long duration = TimeSpan.FromSeconds(source.Info.DurationSeconds).Ticks;
        using var writer = MediaFactory.MFCreateSinkWriterFromURL(output, null, null);
        int width = Math.Max(2, source.Info.Width / 2 * 2), height = Math.Max(2, source.Info.Height / 2 * 2);
        int videoStream = WindowsRecording.AddVideo(writer, width, height);
        using var audio = AudioStream.Open(path, writer);
        using var rendered = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(rendered);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium, IsAntialias = false };
        writer.BeginWriting();
        for (long number = 0; ; number++)
        {
            token.ThrowIfCancellationRequested();
            long time = number * TimeSpan.TicksPerSecond / 30;
            if (time >= duration) break;
            var frame = source.CurrentAt(time, token);
            var rect = VideoEditService.ViewportAtSorted(time / (double)TimeSpan.TicksPerSecond, points);
            var crop = SKRect.Create((float)(rect.X * frame.Width), (float)(rect.Y * frame.Height),
                (float)(rect.Width * frame.Width), (float)(rect.Height * frame.Height));
            canvas.DrawBitmap(frame, crop, SKRect.Create(width, height), paint);
            using var buffer = MediaFactory.MFCreateMemoryBuffer(rendered.ByteCount);
            buffer.Lock(out var ptr, out _, out _);
            try { CopyMemory(ptr, rendered.GetPixels(), (nuint)rendered.ByteCount); }
            finally { buffer.Unlock(); }
            buffer.CurrentLength = rendered.ByteCount;
            long until = Math.Min(duration, (number + 1) * TimeSpan.TicksPerSecond / 30);
            WindowsRecording.WriteSample(writer, videoStream, buffer, time, until - time);
            audio?.CopyThrough(writer, until, token);
            if (number % 6 == 0) progress?.Report(time / (double)duration);
        }
        audio?.CopyThrough(writer, long.MaxValue, token);
        writer.Finalize();
    }

    private sealed class VideoSource : IDisposable
    {
        private IMFSourceReader reader = null!;
        private SKBitmap? current, next;
        private long nextTime;
        private bool end;
        private int stride;
        public VideoInfo Info { get; }

        public VideoSource(string path)
        {
            MediaFactory.MFStartup(true).CheckError();
            try
            {
                using var attributes = MediaFactory.MFCreateAttributes(1);
                // The legacy RGB converter can expose an aligned codec surface as a flat
                // buffer while advertising the visible width. The video processor exposes
                // its actual row pitch through IMF2DBuffer.
                attributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, 1u);
                reader = MediaFactory.MFCreateSourceReaderFromURL(path, attributes);
                reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
                reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
                using var native = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0);
                ulong nativeDimensions = native.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                using var requested = MediaFactory.MFCreateMediaType();
                requested.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                requested.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
                requested.Set(MediaTypeAttributeKeys.FrameSize, nativeDimensions);
                requested.Set(MediaTypeAttributeKeys.DefaultStride, checked((uint)(nativeDimensions >> 32) * 4));
                reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, requested);
                using var format = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
                ulong dimensions = format.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                long duration = Convert.ToInt64(reader.GetPresentationAttribute(SourceReaderIndex.MediaSource, PresentationDescriptionAttributeKeys.Duration).Value);
                Info = new VideoInfo(duration / (double)TimeSpan.TicksPerSecond, (int)(dimensions >> 32), (int)(uint)dimensions);
                if (duration <= 0 || Info.Width <= 0 || Info.Height <= 0) throw new InvalidDataException("Ungültiges Videoformat.");
                stride = format.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out uint pitch).Success ? unchecked((int)pitch) : -Info.Width * 4;
            }
            catch { Dispose(); throw; }
        }

        public SKBitmap SeekFrame(long time, CancellationToken token)
        {
            if (time <= 0) return CurrentAt(0, token);
            reader.SetCurrentPosition(time);
            try { return CurrentAt(time, token); }
            catch (InvalidDataException) when (end && current == null)
            {
                // Some variable-frame-rate files have a long hold after the final sample.
                reader.SetCurrentPosition(0);
                end = false;
                return CurrentAt(time, token);
            }
        }
        public SKBitmap CurrentAt(long time, CancellationToken token)
        {
            while (!end)
            {
                token.ThrowIfCancellationRequested();
                if (next == null)
                {
                    using var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, 0, out _, out var flags, out nextTime);
                    if (sample != null) next = Decode(sample);
                    else if (flags.HasFlag(SourceReaderFlag.EndOfStream)) { end = true; break; }
                    else continue;
                }
                if (current != null && nextTime > time) break;
                current?.Dispose();
                current = next;
                next = null;
            }
            return current ?? throw new InvalidDataException("Das Video enthält kein lesbares Bild.");
        }

        private SKBitmap Decode(IMFSample sample)
        {
            using var buffer = sample.ConvertToContiguousBuffer();
            using var twoD = buffer.QueryInterfaceOrNull<IMF2DBuffer>();
            if (twoD != null)
            {
                twoD.Lock2D(out var firstRow, out var actualPitch);
                try { return CopyVisibleRows(firstRow, actualPitch); }
                finally { twoD.Unlock2D(); }
            }

            buffer.Lock(out var start, out _, out int length);
            try
            {
                int rowBytes = checked(Info.Width * 4);
                int pitch = stride;
                if (length == (long)rowBytes * Info.Height) pitch = stride < 0 ? -rowBytes : rowBytes;
                long spacing = Math.Abs((long)pitch);
                long lastRow = spacing * (Info.Height - 1);
                if (spacing < rowBytes || length < lastRow + rowBytes || length > lastRow + spacing)
                    throw new InvalidDataException("Das Zeilenlayout des dekodierten Videobildes ist nicht eindeutig.");
                return CopyVisibleRows(pitch < 0 ? start + checked((nint)lastRow) : start, pitch);
            }
            finally { buffer.Unlock(); }
        }

        private SKBitmap CopyVisibleRows(nint firstRow, int pitch)
        {
            int rowBytes = checked(Info.Width * 4);
            if (Math.Abs((long)pitch) < rowBytes) throw new InvalidDataException("Die dekodierte Bildzeile ist zu kurz.");
            var bitmap = new SKBitmap(new SKImageInfo(Info.Width, Info.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
            nint output = bitmap.GetPixels();
            for (int y = 0; y < Info.Height; y++)
                CopyMemory(output + (nint)y * bitmap.RowBytes, firstRow + (nint)y * pitch, (nuint)rowBytes);
            return bitmap;
        }
        public void Dispose()
        {
            current?.Dispose(); next?.Dispose(); reader?.Dispose();
            MediaFactory.MFShutdown();
        }
    }

    // Keep the original compressed AAC samples and timestamps, including leading silence.
    private sealed class AudioStream(IMFSourceReader reader, int stream) : IDisposable
    {
        private IMFSample? pending;
        private long timestamp;
        private bool end;
        public static AudioStream? Open(string path, IMFSinkWriter writer)
        {
            var source = MediaFactory.MFCreateSourceReaderFromURL(path, null);
            try
            {
                source.SetStreamSelection(SourceReaderIndex.AllStreams, false);
                source.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true);
                using var format = source.GetNativeMediaType(SourceReaderIndex.FirstAudioStream, 0);
                source.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, format);
                int stream = writer.AddStream(format);
                writer.SetInputMediaType(stream, format, null);
                return new AudioStream(source, stream);
            }
            catch (SharpGenException error) when (error.ResultCode.Code == unchecked((int)0xC00D36B3))
            {
                source.Dispose(); return null; // MF_E_INVALIDSTREAMNUMBER on silent clips
            }
            catch { source.Dispose(); throw; }
        }

        public void CopyThrough(IMFSinkWriter writer, long until, CancellationToken token)
        {
            while (!end)
            {
                token.ThrowIfCancellationRequested();
                if (pending == null)
                {
                    pending = reader.ReadSample(SourceReaderIndex.FirstAudioStream, 0, out _, out var flags, out timestamp);
                    if (pending == null)
                    {
                        end = flags.HasFlag(SourceReaderFlag.EndOfStream);
                        continue;
                    }
                }
                if (timestamp >= until) return;
                writer.WriteSample(stream, pending);
                pending.Dispose(); pending = null;
            }
        }
        public void Dispose() { pending?.Dispose(); reader.Dispose(); }
    }

    [DllImport("kernel32", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyMemory(nint destination, nint source, nuint length);
}
