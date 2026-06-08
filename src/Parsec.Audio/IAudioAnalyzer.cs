namespace Parsec.Audio;

public interface IAudioAnalyzer
{
    ValueTask<AudioFeatureTrack> AnalyzeAsync(
        Uri source,
        AudioAnalysisOptions? options = null,
        CancellationToken cancellationToken = default);
}
