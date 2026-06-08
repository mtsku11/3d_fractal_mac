namespace Parsec.Audio;

public sealed record AudioFeatureFrame(
    TimeSpan Time,
    TimeSpan Window,
    double Rms,
    double Peak,
    double BassEnergy,
    double MidEnergy,
    double TrebleEnergy,
    double SpectrumCentroidHz,
    double OnsetStrength);
