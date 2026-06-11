namespace Parsec.Audio.Sonification;

/// <summary>Writes 16-bit PCM samples to a WAV file (mono or stereo interleaved).</summary>
public static class WavEncoder
{
    public static void Write(string path, short[] samples, int sampleRate, int channels = 1)
    {
        const int bitsPerSample = 16;
        int byteRate   = sampleRate * channels * (bitsPerSample / 8);
        int blockAlign = channels * (bitsPerSample / 8);
        int dataSize   = samples.Length * (bitsPerSample / 8);

        using var fs = File.Create(path);
        using var w  = new BinaryWriter(fs);

        // RIFF header
        w.Write((byte)'R'); w.Write((byte)'I'); w.Write((byte)'F'); w.Write((byte)'F');
        w.Write(36 + dataSize);
        w.Write((byte)'W'); w.Write((byte)'A'); w.Write((byte)'V'); w.Write((byte)'E');

        // fmt  chunk
        w.Write((byte)'f'); w.Write((byte)'m'); w.Write((byte)'t'); w.Write((byte)' ');
        w.Write(16);
        w.Write((ushort)1);                // PCM
        w.Write((ushort)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((ushort)blockAlign);
        w.Write((ushort)bitsPerSample);

        // data chunk
        w.Write((byte)'d'); w.Write((byte)'a'); w.Write((byte)'t'); w.Write((byte)'a');
        w.Write(dataSize);
        foreach (var s in samples)
            w.Write(s);
    }
}
