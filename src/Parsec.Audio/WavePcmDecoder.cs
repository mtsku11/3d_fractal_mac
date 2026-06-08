using System.Buffers.Binary;

namespace Parsec.Audio;

internal static class WavePcmDecoder
{
    private const ushort PcmFormat = 0x0001;
    private const ushort FloatFormat = 0x0003;

    public static WavePcmData Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        if (ReadFourCc(reader) != "RIFF")
            throw new NotSupportedException("Only RIFF/WAVE files are supported in Phase 1.");

        _ = reader.ReadInt32();
        if (ReadFourCc(reader) != "WAVE")
            throw new NotSupportedException("Only WAVE audio files are supported in Phase 1.");

        ushort? audioFormat = null;
        ushort channels = 0;
        int sampleRate = 0;
        ushort bitsPerSample = 0;
        byte[]? data = null;

        while (stream.Position + 8 <= stream.Length)
        {
            string chunkId = ReadFourCc(reader);
            int chunkSize = reader.ReadInt32();
            long chunkEnd = stream.Position + chunkSize;
            if (chunkEnd > stream.Length)
                throw new InvalidDataException("Wave file contains a truncated chunk.");

            switch (chunkId)
            {
                case "fmt ":
                    audioFormat = reader.ReadUInt16();
                    channels = reader.ReadUInt16();
                    sampleRate = reader.ReadInt32();
                    _ = reader.ReadInt32();
                    _ = reader.ReadUInt16();
                    bitsPerSample = reader.ReadUInt16();
                    stream.Position = chunkEnd;
                    break;

                case "data":
                    data = reader.ReadBytes(chunkSize);
                    stream.Position = chunkEnd;
                    break;

                default:
                    stream.Position = chunkEnd;
                    break;
            }

            if ((chunkSize & 1) != 0 && stream.Position < stream.Length)
                stream.Position += 1;
        }

        if (audioFormat == null || data == null)
            throw new InvalidDataException("Wave file is missing format or sample data.");

        if (channels is not (1 or 2))
            throw new NotSupportedException("Phase 1 supports mono or stereo wave files only.");

        short[] samples = ConvertSamples(data, audioFormat.Value, bitsPerSample);
        var format = channels == 1 ? OpenTK.Audio.OpenAL.ALFormat.Mono16 : OpenTK.Audio.OpenAL.ALFormat.Stereo16;
        int frames = samples.Length / channels;
        TimeSpan duration = TimeSpan.FromSeconds((double)frames / sampleRate);
        return new WavePcmData(samples, channels, format, sampleRate, duration);
    }

    private static short[] ConvertSamples(byte[] data, ushort audioFormat, ushort bitsPerSample)
    {
        return (audioFormat, bitsPerSample) switch
        {
            (PcmFormat, 8) => ConvertPcm8(data),
            (PcmFormat, 16) => ConvertPcm16(data),
            (FloatFormat, 32) => ConvertFloat32(data),
            _ => throw new NotSupportedException(
                $"Unsupported wave encoding. Expected PCM 8/16-bit or IEEE float 32-bit, got format {audioFormat} with {bitsPerSample} bits."),
        };
    }

    private static short[] ConvertPcm8(byte[] data)
    {
        var samples = new short[data.Length];
        for (int i = 0; i < data.Length; i++)
            samples[i] = (short)((data[i] - 128) << 8);
        return samples;
    }

    private static short[] ConvertPcm16(byte[] data)
    {
        if ((data.Length & 1) != 0)
            throw new InvalidDataException("PCM16 wave data has an invalid byte count.");

        var samples = new short[data.Length / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(
                new ReadOnlySpan<byte>(data, i * 2, 2));
        }

        return samples;
    }

    private static short[] ConvertFloat32(byte[] data)
    {
        if ((data.Length & 3) != 0)
            throw new InvalidDataException("Float32 wave data has an invalid byte count.");

        var samples = new short[data.Length / 4];
        for (int i = 0; i < samples.Length; i++)
        {
            int bits = BinaryPrimitives.ReadInt32LittleEndian(
                new ReadOnlySpan<byte>(data, i * 4, 4));
            float value = Math.Clamp(BitConverter.Int32BitsToSingle(bits), -1.0f, 1.0f);
            samples[i] = (short)Math.Round(value * short.MaxValue);
        }

        return samples;
    }

    private static string ReadFourCc(BinaryReader reader)
    {
        var value = reader.ReadChars(4);
        if (value.Length != 4)
            throw new EndOfStreamException("Unexpected end of wave file.");
        return new string(value);
    }
}
