using OpenTK.Audio.OpenAL;

namespace Parsec.Audio;

internal sealed record WavePcmData(
    short[] Samples,
    int Channels,
    ALFormat Format,
    int SampleRate,
    TimeSpan Duration)
{
    public int FrameCount => Channels == 0 ? 0 : Samples.Length / Channels;
}
