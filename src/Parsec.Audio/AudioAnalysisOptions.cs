namespace Parsec.Audio;

public sealed record AudioAnalysisOptions
{
    public static AudioAnalysisOptions Default { get; } = new();

    public int WindowSize { get; init; } = 2048;
    public int HopSize { get; init; } = 1024;
    public double BassMaxHz { get; init; } = 250.0;
    public double MidMaxHz { get; init; } = 2_000.0;

    internal void Validate()
    {
        if (WindowSize < 64 || !IsPowerOfTwo(WindowSize))
            throw new ArgumentOutOfRangeException(nameof(WindowSize), "WindowSize must be a power of two and at least 64.");
        if (HopSize <= 0 || HopSize > WindowSize)
            throw new ArgumentOutOfRangeException(nameof(HopSize), "HopSize must be between 1 and WindowSize.");
        if (BassMaxHz <= 0 || MidMaxHz <= BassMaxHz)
            throw new ArgumentOutOfRangeException(nameof(MidMaxHz), "Band edges must satisfy 0 < BassMaxHz < MidMaxHz.");
    }

    private static bool IsPowerOfTwo(int value) => (value & (value - 1)) == 0;
}
