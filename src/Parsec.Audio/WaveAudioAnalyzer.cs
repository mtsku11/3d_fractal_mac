using System.Numerics;

namespace Parsec.Audio;

public sealed class WaveAudioAnalyzer : IAudioAnalyzer
{
    public ValueTask<AudioFeatureTrack> AnalyzeAsync(
        Uri source,
        AudioAnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!source.IsFile || string.IsNullOrWhiteSpace(source.LocalPath))
            throw new NotSupportedException("Only local WAVE audio files are supported in Phase 2.");

        AudioAnalysisOptions settings = options ?? AudioAnalysisOptions.Default;
        settings.Validate();

        return new ValueTask<AudioFeatureTrack>(
            Task.Run(() => AnalyzeCore(source.LocalPath, settings, cancellationToken), cancellationToken));
    }

    private static AudioFeatureTrack AnalyzeCore(
        string path,
        AudioAnalysisOptions options,
        CancellationToken cancellationToken)
    {
        var audio = WavePcmDecoder.Load(path);
        if (audio.FrameCount == 0)
        {
            return new AudioFeatureTrack(
                audio.Duration,
                new[] { new AudioFeatureFrame(TimeSpan.Zero, TimeSpan.Zero, 0, 0, 0, 0, 0, 0, 0) });
        }

        double[] mono = BuildMonoSamples(audio);
        double[] window = BuildHannWindow(options.WindowSize);
        double[]? previousSpectrum = null;
        var frames = new List<AudioFeatureFrame>();
        var fftBuffer = new Complex[options.WindowSize];
        var magnitudes = new double[(options.WindowSize / 2) + 1];

        for (int startFrame = 0; startFrame < audio.FrameCount; startFrame += options.HopSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AnalyzeWindow(
                mono,
                audio.SampleRate,
                startFrame,
                options,
                window,
                fftBuffer,
                magnitudes,
                previousSpectrum,
                out AudioFeatureFrame frame,
                out double[] currentSpectrum);

            frames.Add(frame);
            previousSpectrum = currentSpectrum;
        }

        return new AudioFeatureTrack(audio.Duration, frames);
    }

    private static double[] BuildMonoSamples(WavePcmData audio)
    {
        var mono = new double[audio.FrameCount];
        for (int frame = 0; frame < audio.FrameCount; frame++)
        {
            double sum = 0;
            int sampleOffset = frame * audio.Channels;
            for (int channel = 0; channel < audio.Channels; channel++)
                sum += audio.Samples[sampleOffset + channel] / (double)short.MaxValue;

            mono[frame] = sum / audio.Channels;
        }

        return mono;
    }

    private static void AnalyzeWindow(
        double[] mono,
        int sampleRate,
        int startFrame,
        AudioAnalysisOptions options,
        double[] window,
        Complex[] fftBuffer,
        double[] magnitudes,
        double[]? previousSpectrum,
        out AudioFeatureFrame frame,
        out double[] currentSpectrum)
    {
        double rmsSum = 0;
        double peak = 0;

        Array.Clear(fftBuffer, 0, fftBuffer.Length);

        for (int i = 0; i < options.WindowSize; i++)
        {
            int index = startFrame + i;
            double sample = index < mono.Length ? mono[index] : 0;
            double abs = Math.Abs(sample);
            peak = Math.Max(peak, abs);
            rmsSum += sample * sample;
            fftBuffer[i] = new Complex(sample * window[i], 0);
        }

        FftInPlace(fftBuffer);

        double bassEnergy = 0;
        double midEnergy = 0;
        double trebleEnergy = 0;
        double totalEnergy = 0;
        double magnitudeSum = 0;
        double centroidWeightedHz = 0;

        Array.Clear(magnitudes, 0, magnitudes.Length);
        int nyquistBin = options.WindowSize / 2;
        for (int bin = 1; bin <= nyquistBin; bin++)
        {
            double real = fftBuffer[bin].Real;
            double imaginary = fftBuffer[bin].Imaginary;
            double magnitude = Math.Sqrt((real * real) + (imaginary * imaginary));
            double energy = magnitude * magnitude;
            double frequency = (double)bin * sampleRate / options.WindowSize;

            magnitudes[bin] = magnitude;
            totalEnergy += energy;
            magnitudeSum += magnitude;
            centroidWeightedHz += frequency * magnitude;

            if (frequency <= options.BassMaxHz)
                bassEnergy += energy;
            else if (frequency <= options.MidMaxHz)
                midEnergy += energy;
            else
                trebleEnergy += energy;
        }

        double onsetStrength = 0;
        if (previousSpectrum != null)
        {
            double positiveFlux = 0;
            for (int bin = 1; bin < magnitudes.Length; bin++)
            {
                double delta = magnitudes[bin] - previousSpectrum[bin];
                if (delta > 0)
                    positiveFlux += delta;
            }

            onsetStrength = magnitudeSum > 0 ? positiveFlux / magnitudeSum : 0;
        }

        if (totalEnergy > 0)
        {
            bassEnergy /= totalEnergy;
            midEnergy /= totalEnergy;
            trebleEnergy /= totalEnergy;
        }

        double centroidHz = magnitudeSum > 0 ? centroidWeightedHz / magnitudeSum : 0;
        double rms = Math.Sqrt(rmsSum / options.WindowSize);
        TimeSpan time = TimeSpan.FromSeconds((double)startFrame / sampleRate);
        TimeSpan windowDuration = TimeSpan.FromSeconds((double)options.WindowSize / sampleRate);

        currentSpectrum = (double[])magnitudes.Clone();
        frame = new AudioFeatureFrame(
            Time: time,
            Window: windowDuration,
            Rms: rms,
            Peak: peak,
            BassEnergy: bassEnergy,
            MidEnergy: midEnergy,
            TrebleEnergy: trebleEnergy,
            SpectrumCentroidHz: centroidHz,
            OnsetStrength: onsetStrength);
    }

    private static double[] BuildHannWindow(int size)
    {
        var window = new double[size];
        if (size == 1)
        {
            window[0] = 1;
            return window;
        }

        for (int i = 0; i < size; i++)
            window[i] = 0.5 * (1.0 - Math.Cos((2.0 * Math.PI * i) / (size - 1)));
        return window;
    }

    private static void FftInPlace(Complex[] buffer)
    {
        int n = buffer.Length;
        int j = 0;
        for (int i = 1; i < n; i++)
        {
            int bit = n >> 1;
            while ((j & bit) != 0)
            {
                j &= ~bit;
                bit >>= 1;
            }

            j |= bit;
            if (i < j)
                (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2.0 * Math.PI / len;
            Complex wLen = new(Math.Cos(angle), Math.Sin(angle));
            for (int i = 0; i < n; i += len)
            {
                Complex w = Complex.One;
                int halfLen = len >> 1;
                for (int k = 0; k < halfLen; k++)
                {
                    Complex even = buffer[i + k];
                    Complex odd = buffer[i + k + halfLen] * w;
                    buffer[i + k] = even + odd;
                    buffer[i + k + halfLen] = even - odd;
                    w *= wLen;
                }
            }
        }
    }
}
