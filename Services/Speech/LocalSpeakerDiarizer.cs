using LiveCaptioner.Services.Diagnostics;

namespace LiveCaptioner.Services.Speech;

public sealed class LocalSpeakerDiarizer
{
    private const int SampleRate = 24000;
    private const int WindowSamples = SampleRate * 3;
    private const int HopSamples = SampleRate;
    private const double MinimumRms = 0.012;
    private const double StrongMatchThreshold = 0.978;
    private const double ContinuityThreshold = 0.948;
    private const double NewSpeakerThreshold = 0.972;
    private const int StableSwitchWindows = 2;
    private const int StableNewSpeakerWindows = 2;
    private readonly List<short> _samples = new();
    private readonly List<SpeakerVoiceCluster> _clusters = new();
    private readonly List<double[]> _unknownVoiceprints = new();
    private string _currentSpeaker = "Speaker 1";
    private string? _pendingSpeaker;
    private int _pendingSpeakerWindows;

    public string CurrentSpeaker => _currentSpeaker;

    public void Reset()
    {
        _samples.Clear();
        _clusters.Clear();
        _unknownVoiceprints.Clear();
        _currentSpeaker = "Speaker 1";
        _pendingSpeaker = null;
        _pendingSpeakerWindows = 0;
    }

    public string AddPcm(byte[] pcmBytes)
    {
        for (var offset = 0; offset + 1 < pcmBytes.Length; offset += 2)
        {
            _samples.Add(BitConverter.ToInt16(pcmBytes, offset));
        }

        while (_samples.Count >= WindowSamples)
        {
            var window = _samples.Take(WindowSamples).ToArray();
            _samples.RemoveRange(0, HopSamples);
            var speaker = ResolveSpeaker(window);
            if (!string.Equals(speaker, _currentSpeaker, StringComparison.Ordinal))
            {
                AppLogger.Info($"OpenAI local voice split changed speaker: {_currentSpeaker} -> {speaker}.");
                _currentSpeaker = speaker;
            }
        }

        return _currentSpeaker;
    }

    private string ResolveSpeaker(short[] window)
    {
        var feature = BuildFeature(window);
        if (feature == null)
        {
            ClearPending();
            return _currentSpeaker;
        }

        if (_clusters.Count == 0)
        {
            _clusters.Add(new SpeakerVoiceCluster(_currentSpeaker, feature));
            AppLogger.Info("OpenAI local voice split initialized Speaker 1 voiceprint.");
            return _currentSpeaker;
        }

        var bestIndex = -1;
        var bestScore = double.NegativeInfinity;
        for (var i = 0; i < _clusters.Count; i++)
        {
            var score = CosineSimilarity(_clusters[i].Centroid, feature);
            if (score > bestScore)
            {
                bestIndex = i;
                bestScore = score;
            }
        }

        var currentCluster = _clusters.FirstOrDefault(cluster => cluster.Name == _currentSpeaker);
        var currentScore = currentCluster == null
            ? double.NegativeInfinity
            : CosineSimilarity(currentCluster.Centroid, feature);

        if (currentCluster != null &&
            currentScore >= ContinuityThreshold &&
            bestScore < StrongMatchThreshold &&
            PitchDistance(currentCluster.Centroid, feature) < 0.34)
        {
            currentCluster.Update(feature);
            ClearPending();
            return _currentSpeaker;
        }

        if (bestIndex >= 0 && bestScore >= StrongMatchThreshold)
        {
            var matchedSpeaker = _clusters[bestIndex].Name;
            if (string.Equals(matchedSpeaker, _currentSpeaker, StringComparison.Ordinal))
            {
                _clusters[bestIndex].Update(feature);
                ClearPending();
                return _currentSpeaker;
            }

            if (ConfirmPendingSpeaker(matchedSpeaker))
            {
                _clusters[bestIndex].Update(feature);
                ClearPending();
                AppLogger.Info($"OpenAI local voice split confirmed existing {matchedSpeaker}, score={bestScore:0.000}, previousScore={currentScore:0.000}.");
                return matchedSpeaker;
            }

            return _currentSpeaker;
        }

        if (bestScore < NewSpeakerThreshold && CollectUnknownVoiceprint(feature))
        {
            if (_clusters.Count >= 4)
            {
                ClearPending();
                return _currentSpeaker;
            }

            var speakerName = $"Speaker {_clusters.Count + 1}";
            var centroid = AverageVoiceprints(_unknownVoiceprints);
            _clusters.Add(new SpeakerVoiceCluster(speakerName, centroid));
            ClearPending();
            AppLogger.Info($"OpenAI local voice split created stable {speakerName}, bestScore={bestScore:0.000}, previousScore={currentScore:0.000}.");
            return speakerName;
        }

        return _currentSpeaker;
    }

    private bool ConfirmPendingSpeaker(string speakerName)
    {
        if (string.Equals(_pendingSpeaker, speakerName, StringComparison.Ordinal))
        {
            _pendingSpeakerWindows++;
        }
        else
        {
            _pendingSpeaker = speakerName;
            _pendingSpeakerWindows = 1;
        }

        _unknownVoiceprints.Clear();
        return _pendingSpeakerWindows >= StableSwitchWindows;
    }

    private bool CollectUnknownVoiceprint(double[] feature)
    {
        if (_unknownVoiceprints.Count > 0 &&
            _unknownVoiceprints.Average(voiceprint => CosineSimilarity(voiceprint, feature)) < StrongMatchThreshold - 0.010)
        {
            _unknownVoiceprints.Clear();
        }

        _pendingSpeaker = null;
        _pendingSpeakerWindows = 0;
        _unknownVoiceprints.Add(feature);
        return _unknownVoiceprints.Count >= StableNewSpeakerWindows;
    }

    private void ClearPending()
    {
        _pendingSpeaker = null;
        _pendingSpeakerWindows = 0;
        _unknownVoiceprints.Clear();
    }

    private static double[]? BuildFeature(short[] samples)
    {
        double sumSquares = 0;
        var zeroCrossings = 0;
        var previous = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var value = samples[i] / 32768.0;
            sumSquares += value * value;
            if (i > 0 && Math.Sign(samples[i]) != Math.Sign(previous))
            {
                zeroCrossings++;
            }

            previous = samples[i];
        }

        var rms = Math.Sqrt(sumSquares / samples.Length);
        if (rms < MinimumRms)
        {
            return null;
        }

        var pitch = EstimatePitch(samples);
        var low = BandEnergy(samples, 120, 260);
        var voiceBody = BandEnergy(samples, 260, 520);
        var midLow = BandEnergy(samples, 520, 1000);
        var mid = BandEnergy(samples, 1000, 2200);
        var high = BandEnergy(samples, 2200, 4200);
        var zcr = (double)zeroCrossings / samples.Length;

        return Normalize([
            Math.Clamp(pitch / 280.0, 0, 1) * 2.4,
            zcr * 11.0,
            low,
            voiceBody * 1.7,
            midLow * 1.4,
            mid,
            high,
            (low - high) * 1.7,
            (voiceBody - mid) * 1.8,
            (midLow - high) * 1.4
        ]);
    }

    private static double BandEnergy(short[] samples, int lowHz, int highHz)
    {
        var center = (lowHz + highHz) / 2.0;
        var k = (int)Math.Round(samples.Length * center / SampleRate);
        var omega = 2.0 * Math.PI * k / samples.Length;
        var cosine = Math.Cos(omega);
        var sine = Math.Sin(omega);
        var coeff = 2.0 * cosine;
        double q0 = 0;
        double q1 = 0;
        double q2 = 0;

        foreach (var sample in samples)
        {
            q0 = coeff * q1 - q2 + sample / 32768.0;
            q2 = q1;
            q1 = q0;
        }

        var real = q1 - q2 * cosine;
        var imaginary = q2 * sine;
        return Math.Log(real * real + imaginary * imaginary + 1e-9);
    }

    private static double EstimatePitch(short[] samples)
    {
        const int step = 4;
        var minLag = SampleRate / 320 / step;
        var maxLag = SampleRate / 85 / step;
        var reduced = new double[samples.Length / step];
        for (var i = 0; i < reduced.Length; i++)
        {
            reduced[i] = samples[i * step] / 32768.0;
        }

        var bestLag = minLag;
        var bestCorrelation = double.NegativeInfinity;
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            double correlation = 0;
            double energy = 0;
            for (var i = lag; i < reduced.Length; i++)
            {
                correlation += reduced[i] * reduced[i - lag];
                energy += reduced[i] * reduced[i];
            }

            if (energy <= 0)
            {
                continue;
            }

            correlation /= energy;
            if (correlation > bestCorrelation)
            {
                bestCorrelation = correlation;
                bestLag = lag;
            }
        }

        return bestCorrelation < 0.20
            ? 0
            : (double)SampleRate / (bestLag * step);
    }

    private static double[] AverageVoiceprints(IReadOnlyList<double[]> voiceprints)
    {
        var length = voiceprints[0].Length;
        var average = new double[length];
        foreach (var voiceprint in voiceprints)
        {
            for (var i = 0; i < length; i++)
            {
                average[i] += voiceprint[i];
            }
        }

        for (var i = 0; i < length; i++)
        {
            average[i] /= voiceprints.Count;
        }

        return Normalize(average);
    }

    private static double[] Normalize(double[] values)
    {
        var norm = Math.Sqrt(values.Sum(value => value * value));
        if (norm <= 0)
        {
            return values;
        }

        for (var i = 0; i < values.Length; i++)
        {
            values[i] /= norm;
        }

        return values;
    }

    private static double CosineSimilarity(double[] first, double[] second)
    {
        var length = Math.Min(first.Length, second.Length);
        double dot = 0;
        for (var i = 0; i < length; i++)
        {
            dot += first[i] * second[i];
        }

        return dot;
    }

    private static double PitchDistance(double[] first, double[] second)
    {
        if (first.Length == 0 || second.Length == 0)
        {
            return 0;
        }

        return Math.Abs(first[0] - second[0]);
    }

    private sealed class SpeakerVoiceCluster
    {
        private int _sampleCount = 1;

        public SpeakerVoiceCluster(string name, double[] centroid)
        {
            Name = name;
            Centroid = (double[])centroid.Clone();
        }

        public string Name { get; }
        public double[] Centroid { get; }

        public void Update(double[] feature)
        {
            for (var i = 0; i < Centroid.Length && i < feature.Length; i++)
            {
                Centroid[i] = (Centroid[i] * _sampleCount + feature[i]) / (_sampleCount + 1);
            }

            _sampleCount++;
            Normalize(Centroid);
        }
    }
}
