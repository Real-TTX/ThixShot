using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Vortice.MediaFoundation;
using static Vortice.MediaFoundation.MediaTypeAttributeKeys;

namespace ThixShot.Capture;

[SupportedOSPlatform("windows")]
internal static class WindowsRecording
{
    private const int FramesPerSecond = 30;
    internal const long TicksPerSecond = TimeSpan.TicksPerSecond;

    public static IReadOnlyList<ZoomPoint> Record(string path, RecordingOptions options, Task stop, TaskCompletionSource ready)
    {
        using var desktop = new WindowsDesktop(options.Region, true);
        try { MediaFactory.MFStartup(true).CheckError(); }
        catch (DllNotFoundException error)
        {
            throw new PlatformNotSupportedException("Die Windows-Medienkomponenten fehlen. Installiere das Media Feature Pack über die optionalen Windows-Features.", error);
        }
        try
        {
            using var writer = MediaFactory.MFCreateSinkWriterFromURL(path, null, null);
            int videoStream = AddVideo(writer, desktop.Width, desktop.Height);
            var elapsed = new Stopwatch();
            using var audio = options.SystemAudio ? new LoopbackTrack(writer, elapsed) : null;
            writer.BeginWriting();
            elapsed.Start();
            audio?.Start();
            var clicks = new List<ZoomPoint>();
            bool wasPressed = options.FocusZoom && WindowsDesktop.GetAsyncKeyState(1) < 0;
            long frameStart = 0;
            do
            {
                if (options.FocusZoom) CollectClick(desktop.Region, elapsed.Elapsed.TotalSeconds, clicks, ref wasPressed);
                desktop.CopyFrame(desktop.Region, options.ShowCursor);
                int length = checked(desktop.Width * desktop.Height * 4);
                using var buffer = MediaFactory.MFCreateMemoryBuffer(length);
                buffer.Lock(out var destination, out _, out _);
                try { CopyMemory(destination, desktop.Pixels, (nuint)length); }
                finally { buffer.Unlock(); }
                buffer.CurrentLength = desktop.Width * desktop.Height * 4;
                WriteSample(writer, videoStream, buffer, frameStart, TicksPerSecond / FramesPerSecond);
                ready.TrySetResult();
                audio?.Drain(elapsed.Elapsed.Ticks, false);
                var untilNextFrame = TimeSpan.FromTicks(frameStart + TicksPerSecond / FramesPerSecond - elapsed.Elapsed.Ticks);
                if (untilNextFrame > TimeSpan.Zero) stop.Wait(untilNextFrame);
                frameStart = elapsed.Elapsed.Ticks;
            } while (!stop.IsCompleted);
            audio?.Finish(elapsed.Elapsed.Ticks);
            writer.Finalize();
            return clicks.AsReadOnly();
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    private static void CollectClick(CaptureRegion region, double seconds, List<ZoomPoint> points, ref bool wasPressed)
    {
        bool pressed = WindowsDesktop.GetAsyncKeyState(1) < 0;
        if (pressed && !wasPressed && WindowsDesktop.GetCursorPos(out var pointer) && !WindowsDesktop.IsThixShotWindowAt(pointer))
        {
            double x = (pointer.X - region.X) / (double)region.Width, y = (pointer.Y - region.Y) / (double)region.Height;
            if (x is >= 0 and < 1 && y is >= 0 and < 1)
                points.Add(new ZoomPoint(seconds, 2, x, y, 1.55));
        }
        wasPressed = pressed;
    }

    internal static int AddVideo(IMFSinkWriter writer, int width, int height)
    {
        using var output = MediaFactory.MFCreateMediaType();
        SetVideoLayout(output, new Guid("34363248-0000-0010-8000-00AA00389B71"), width, height);
        output.Set(AvgBitrate, (uint)Math.Clamp(width * height * 4, 750_000, 12_000_000));
        var stream = writer.AddStream(output);
        using var input = MediaFactory.MFCreateMediaType();
        SetVideoLayout(input, VideoFormatGuids.Rgb32, width, height);
        input.Set(DefaultStride, (uint)(width * 4)); // positive stride = top-down image in Media Foundation
        writer.SetInputMediaType(stream, input, null);
        return stream;
    }

    private static void SetVideoLayout(IMFMediaType type, Guid subtype, int width, int height)
    {
        type.Set(MajorType, MediaTypeGuids.Video);
        type.Set(Subtype, subtype);
        type.Set(FrameSize, ((ulong)(uint)width << 32) | (uint)height);
        type.Set(FrameRate, ((ulong)FramesPerSecond << 32) | 1);
        type.Set(PixelAspectRatio, (1ul << 32) | 1);
        type.Set(InterlaceMode, 2u);
    }

    internal static void WriteSample(IMFSinkWriter writer, int stream, IMFMediaBuffer buffer, long time, long duration)
    {
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = time;
        sample.SampleDuration = duration;
        writer.WriteSample(stream, sample);
    }

    [DllImport("kernel32", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyMemory(nint destination, nint source, nuint length);
}

[SupportedOSPlatform("windows")]
internal sealed class LoopbackTrack : IDisposable
{
    private const int SampleRate = 48_000, BlockSize = 4;
    private readonly WasapiRecorder capture;
    private readonly ConcurrentQueue<(long StartFrame, byte[] Data)> packets = new();
    private readonly IMFSinkWriter writer;
    private readonly int stream;
    private readonly byte[] silence = new byte[BlockSize * SampleRate / 10];
    private long nextFrame;
    private volatile Exception? deviceFailure;

    public LoopbackTrack(IMFSinkWriter writer, Stopwatch elapsed)
    {
        this.writer = writer;
        stream = ConfigureAudio(writer);
        try { capture = new WasapiRecorderBuilder().WithLoopbackCapture().WithFormat(new WaveFormat(SampleRate, 16, 2)).Build(); }
        catch (COMException error) { throw AudioUnavailable(error); }
        capture.DataAvailable += (data, flags, devicePosition, qpcTime) =>
        {
            if (data.IsEmpty || deviceFailure != null) return;
            if (packets.Count > 500)
            {
                deviceFailure = new IOException("Systemaudio konnte nicht rechtzeitig verarbeitet werden.");
                return;
            }
            long now = Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp()).Ticks;
            long packetStart = qpcTime > 0 && !flags.HasFlag(AudioClientBufferFlags.TimestampError)
                ? FramesAt(elapsed.Elapsed.Ticks - (now - qpcTime))
                : FramesAt(elapsed.Elapsed.Ticks) - data.Length / BlockSize;
            packets.Enqueue((packetStart, data.ToArray()));
        };
        capture.RecordingStopped += (_, args) =>
        {
            if (args.Exception != null) deviceFailure = args.Exception;
        };
    }

    internal static int ConfigureAudio(IMFSinkWriter writer)
    {
        using var output = MediaFactory.MFCreateMediaType();
        output.Set(MajorType, MediaTypeGuids.Audio);
        output.Set(Subtype, AudioFormatGuids.Aac);
        output.Set(AudioSamplesPerSecond, (uint)SampleRate);
        output.Set(AudioNumChannels, 2u);
        output.Set(AudioBitsPerSample, 16u);
        output.Set(AudioAvgBytesPerSecond, 16_000u);
        int track = writer.AddStream(output);
        using var input = MediaFactory.MFCreateMediaType();
        input.Set(MajorType, MediaTypeGuids.Audio);
        input.Set(Subtype, AudioFormatGuids.Pcm);
        input.Set(AudioSamplesPerSecond, (uint)SampleRate);
        input.Set(AudioNumChannels, 2u);
        input.Set(AudioBitsPerSample, 16u);
        input.Set(AudioBlockAlignment, (uint)BlockSize);
        input.Set(AudioAvgBytesPerSecond, (uint)(SampleRate * BlockSize));
        writer.SetInputMediaType(track, input, null);
        return track;
    }

    public void Start()
    {
        try { capture.StartRecording(); }
        catch (COMException error) { throw AudioUnavailable(error); }
    }

    private static IOException AudioUnavailable(Exception error) =>
        new("Das Windows-Wiedergabegerät ist nicht verfügbar. Wähle ein aktives Ausgabegerät oder schalte den Systemton in ThixShot aus.", error);

    public void Drain(long ticks, bool finishing)
    {
        if (deviceFailure != null) throw new IOException("Windows hat die Audioaufnahme unterbrochen.", deviceFailure);
        long limit = FramesAt(ticks);
        while (packets.TryDequeue(out var packet))
        {
            // The loopback device sleeps in silence. Keep long gaps on the video timeline.
            long offset = packet.StartFrame - nextFrame;
            if (Math.Abs(offset) < SampleRate / 50) offset = 0;
            if (offset > 0) WriteSilence(Math.Min(packet.StartFrame, limit));
            int skip = (int)Math.Min(Math.Max(0, -offset), packet.Data.Length / BlockSize) * BlockSize;
            int remaining = packet.Data.Length - skip;
            if (finishing) remaining = (int)Math.Min(remaining, Math.Max(0, limit - nextFrame) * BlockSize);
            if (remaining > 0) Write(packet.Data, skip, remaining);
        }
        WriteSilence(finishing ? limit : limit - SampleRate / 4);
    }

    public void Finish(long ticks)
    {
        capture.StopRecording();
        Drain(ticks, true);
    }

    private static long FramesAt(long ticks) => (long)(ticks * (double)SampleRate / WindowsRecording.TicksPerSecond);
    private void WriteSilence(long until)
    {
        while (nextFrame < until)
            Write(silence, 0, (int)Math.Min(until - nextFrame, silence.Length / BlockSize) * BlockSize);
    }

    private void Write(byte[] bytes, int offset, int length)
    {
        using var buffer = MediaFactory.MFCreateMemoryBuffer(length);
        buffer.Lock(out var destination, out _, out _);
        try { Marshal.Copy(bytes, offset, destination, length); }
        finally { buffer.Unlock(); }
        buffer.CurrentLength = length;
        long time = nextFrame * WindowsRecording.TicksPerSecond / SampleRate;
        nextFrame += length / BlockSize;
        WindowsRecording.WriteSample(writer, stream, buffer, time, nextFrame * WindowsRecording.TicksPerSecond / SampleRate - time);
    }
    public void Dispose() => capture.Dispose();
}
