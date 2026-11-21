using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.Wave;
using ThixShot.Capture;
using Vortice.MediaFoundation;

namespace ThixShot.CaptureSmoke;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private const int Width = 320, Height = 180, FrameRate = 30, SampleRate = 48_000;

    private static void Main()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This test uses Windows Media Foundation.");
        string output = Path.Combine(Path.GetTempPath(), $"thixshot-smoke-{Guid.NewGuid():N}.mp4");
        MediaFactory.MFStartup(true).CheckError();
        try
        {
            EncodeTestPattern(output);
            VerifyDecodedVideo(output);
            VerifyDecodedAudio(output);
            Console.WriteLine("Decoded H.264 test pattern and stereo AAC tone successfully.");
        }
        finally
        {
            MediaFactory.MFShutdown();
            File.Delete(output);
        }
    }

    private static void EncodeTestPattern(string output)
    {
        using var writer = MediaFactory.MFCreateSinkWriterFromURL(output, null, null);
        int videoTrack = WindowsRecording.AddVideo(writer, Width, Height);
        int audioTrack = LoopbackTrack.ConfigureAudio(writer);
        writer.BeginWriting();
        var pixels = new byte[Width * Height * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            bool top = offset < pixels.Length / 2;
            pixels[offset] = top ? (byte)238 : (byte)16;
            pixels[offset + 2] = top ? (byte)32 : (byte)226;
        }
        var stereo = new byte[SampleRate / FrameRate * 4];
        for (int frame = 0; frame < FrameRate; frame++)
        {
            for (int i = 0; i < stereo.Length / 4; i++)
            {
                long sample = frame * (SampleRate / FrameRate) + i;
                short tone = (short)(Math.Sin(2 * Math.PI * 440 * sample / SampleRate) * 16_000);
                BinaryPrimitives.WriteInt16LittleEndian(stereo.AsSpan(i * 4), tone);
                BinaryPrimitives.WriteInt16LittleEndian(stereo.AsSpan(i * 4 + 2), tone);
            }
            long time = frame * TimeSpan.TicksPerSecond / FrameRate;
            Write(writer, videoTrack, pixels, time);
            Write(writer, audioTrack, stereo, time);
        }
        writer.Finalize();
    }

    private static void Write(IMFSinkWriter writer, int track, byte[] data, long timestamp)
    {
        using var buffer = MediaFactory.MFCreateMemoryBuffer(data.Length);
        buffer.Lock(out var pointer, out _, out _);
        try { Marshal.Copy(data, 0, pointer, data.Length); }
        finally { buffer.Unlock(); }
        buffer.CurrentLength = data.Length;
        WindowsRecording.WriteSample(writer, track, buffer, timestamp, TimeSpan.TicksPerSecond / FrameRate);
    }

    private static void VerifyDecodedVideo(string path)
    {
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null);
        using var raw = MediaFactory.MFCreateMediaType();
        raw.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        raw.Set(MediaTypeAttributeKeys.Subtype, new Guid("3231564E-0000-0010-8000-00AA00389B71")); // NV12
        reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, raw);
        using var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, 0, out _, out _, out _);
        using var frame = sample.ConvertToContiguousBuffer();
        frame.Lock(out var pixels, out _, out var length);
        try
        {
            if (length < Width * Height) throw new InvalidDataException("The decoded video frame is incomplete.");
            int topLuma = Marshal.ReadByte(pixels, 20 * Width + Width / 2);
            int bottomLuma = Marshal.ReadByte(pixels, 160 * Width + Width / 2);
            if (bottomLuma - topLuma < 15) throw new InvalidDataException("Unexpected video colors or orientation.");
        }
        finally { frame.Unlock(); }
    }

    private static void VerifyDecodedAudio(string path)
    {
        using var reader = new MediaFoundationReader(path);
        if (reader.WaveFormat.SampleRate != SampleRate || reader.WaveFormat.Channels != 2)
            throw new InvalidDataException("Unexpected AAC format.");
        var data = new byte[reader.WaveFormat.AverageBytesPerSecond];
        int count = reader.Read(data, 0, data.Length);
        if (count < data.Length * 0.9 || !MemoryMarshal.Cast<byte, short>(data.AsSpan(0, count)).ContainsAnyInRange((short)8000, short.MaxValue))
            throw new InvalidDataException("The decoded audio is incomplete or silent.");
    }
}
