using NAudio.Dsp;
using NAudio.Wave;

namespace BetterXeneonWidget.Host.Audio;

/// <summary>
/// Captures the default audio output via WASAPI loopback, runs FFT on a
/// running buffer, and exposes a smoothed 64-bin log-spaced spectrum for the
/// widget's equalizer view.
///
/// Loopback = whatever's playing on the system mix. So when Spotify (or any
/// other source) is making sound, this picks it up — we don't need any
/// per-app capture, and we don't need cooperation from the player.
///
/// FFT size 1024 samples → ~21ms per frame at 48 kHz → ~46 spectrum frames
/// per second, plenty for visualization. NAudio's FFT wants log2(N) as the
/// "M" argument, so 1024 = 2^10.
/// </summary>
public sealed class AudioSpectrumService : IDisposable
{
    // FFT_SIZE = 2048 → 23.4Hz resolution at 48kHz. The previous 1024
    // produced 47Hz per FFT bin which left most sub-130Hz log-bands empty
    // (the lowest ~15 output bins had no FFT bin falling inside their
    // narrow ranges). 2048 puts at least one FFT bin in every output
    // band down to ~50Hz, so the bass bars actually move. Cost: ~43ms
    // capture-to-spectrum latency (was 21ms) — fine for visualization.
    private const int FFT_SIZE = 2048;
    private const int FFT_LOG = 11;
    private const int NUM_BINS = 64;
    private const float FREQ_LOW = 25f;       // lowest bin lower edge (Hz)
    private const float FREQ_HIGH = 16000f;   // highest bin upper edge (Hz)

    private readonly ILogger<AudioSpectrumService> _log;
    private WasapiLoopbackCapture? _capture;
    private readonly float[] _sampleBuffer = new float[FFT_SIZE];
    private int _sampleBufferPos;
    private readonly Complex[] _fftBuffer = new Complex[FFT_SIZE];
    private readonly float[] _hann = new float[FFT_SIZE];

    private readonly Lock _stateLock = new();
    private readonly float[] _raw = new float[NUM_BINS];
    private readonly float[] _smoothed = new float[NUM_BINS];
    private float _bass, _mid, _treble;
    // Auto-gain running peak — decays slowly, latches up to the loudest
    // recent raw band value. Effective gain = TARGET / _runningPeak so
    // the visual scale tracks the song's dynamic range automatically.
    private float _runningPeak = 0.01f;
    private DateTimeOffset _lastUpdate = DateTimeOffset.MinValue;
    private bool _captureFailed;

    public AudioSpectrumService(ILogger<AudioSpectrumService> log)
    {
        _log = log;
        // Precompute Hann window — reduces spectral leakage at the FFT
        // boundaries so the bins are crisp instead of smeared.
        for (int i = 0; i < FFT_SIZE; i++)
        {
            _hann[i] = 0.5f * (1f - (float)Math.Cos(2 * Math.PI * i / (FFT_SIZE - 1)));
        }
        TryStartCapture();
    }

    private void TryStartCapture()
    {
        try
        {
            _capture = new WasapiLoopbackCapture();
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();
            _log.LogInformation(
                "Audio spectrum: loopback capture started ({Channels}ch, {Bits}-bit, {Rate}Hz)",
                _capture.WaveFormat.Channels,
                _capture.WaveFormat.BitsPerSample,
                _capture.WaveFormat.SampleRate);
        }
        catch (Exception ex)
        {
            _captureFailed = true;
            _log.LogWarning(ex, "Audio spectrum: loopback capture failed to start. Spectrum will be empty.");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // Common cause: default device changed (headphones unplugged, etc.).
        // Try once to restart with the new default — fail silently after that.
        if (e.Exception != null)
        {
            _log.LogWarning(e.Exception, "Audio spectrum: capture stopped on error. Attempting restart.");
        }
        try
        {
            _capture?.Dispose();
            _capture = null;
            TryStartCapture();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Audio spectrum: restart after recording-stopped failed.");
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_capture == null) return;
        var fmt = _capture.WaveFormat;
        if (fmt.BitsPerSample != 32 || fmt.Encoding != WaveFormatEncoding.IeeeFloat)
        {
            // WasapiLoopbackCapture defaults to 32-bit float. If something
            // unusual lands here just bail rather than risk garbage data.
            return;
        }
        int bytesPerSample = 4;
        int channels = fmt.Channels;
        int stride = bytesPerSample * channels;
        int sampleCount = e.BytesRecorded / stride;

        for (int i = 0; i < sampleCount; i++)
        {
            // Mix down to mono. Most music is stereo and the spectrum is
            // visually identical between channels — averaging is cheap and
            // halves the FFT work.
            float mono = 0;
            int baseIdx = i * stride;
            for (int c = 0; c < channels; c++)
            {
                mono += BitConverter.ToSingle(e.Buffer, baseIdx + c * bytesPerSample);
            }
            mono /= channels;

            _sampleBuffer[_sampleBufferPos++] = mono;
            if (_sampleBufferPos >= FFT_SIZE)
            {
                ProcessFft(fmt.SampleRate);
                _sampleBufferPos = 0;
            }
        }
    }

    private void ProcessFft(int sampleRate)
    {
        // Window + load complex buffer.
        for (int i = 0; i < FFT_SIZE; i++)
        {
            _fftBuffer[i].X = _sampleBuffer[i] * _hann[i];
            _fftBuffer[i].Y = 0;
        }
        FastFourierTransform.FFT(true, FFT_LOG, _fftBuffer);

        // Magnitude for first half (the rest is mirror of negative freqs).
        // bin k corresponds to frequency k * sampleRate / FFT_SIZE.
        int half = FFT_SIZE / 2;
        float hzPerBin = sampleRate / (float)FFT_SIZE;

        // Pre-zero raw bins so accumulation works.
        for (int j = 0; j < NUM_BINS; j++) _raw[j] = 0;

        // Log-spaced edges from FREQ_LOW to FREQ_HIGH.
        // edges[j] is the lower frequency of output bin j; edges[j+1] is upper.
        Span<float> edges = stackalloc float[NUM_BINS + 1];
        float logLow = MathF.Log(FREQ_LOW);
        float logHigh = MathF.Log(FREQ_HIGH);
        for (int j = 0; j <= NUM_BINS; j++)
        {
            float t = j / (float)NUM_BINS;
            edges[j] = MathF.Exp(logLow + (logHigh - logLow) * t);
        }

        // Walk FFT bins once, accumulate into the output bin whose [lo, hi)
        // contains the bin's center frequency. Track counts so we can average.
        Span<int> counts = stackalloc int[NUM_BINS];
        for (int k = 1; k < half; k++)
        {
            float freq = k * hzPerBin;
            if (freq < edges[0]) continue;
            if (freq >= edges[NUM_BINS]) break;

            // Binary search for the output bin. With NUM_BINS=64 a linear
            // scan from the previous index would be fine too — keeping
            // binary for clarity.
            int lo = 0, hi = NUM_BINS;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (freq < edges[mid]) hi = mid; else lo = mid;
            }

            float mag = MathF.Sqrt(_fftBuffer[k].X * _fftBuffer[k].X + _fftBuffer[k].Y * _fftBuffer[k].Y);
            _raw[lo] += mag;
            counts[lo]++;
        }

        for (int j = 0; j < NUM_BINS; j++)
        {
            if (counts[j] > 0) _raw[j] /= counts[j];
        }

        // Fill empty bands with their nearest left neighbor (then nearest
        // right). Even after the FFT_SIZE bump there can be holes at the
        // very low end depending on sample rate (44.1kHz audio gives a
        // 21.5Hz/bin resolution — fine for 25Hz+ but the bottom 1-2 bands
        // can still be empty depending on where the FFT bins land). Holes
        // look weird as a dead-flat bar next to active ones; copying from
        // the neighbor produces a more continuous spectrum.
        for (int j = 1; j < NUM_BINS; j++)
        {
            if (counts[j] == 0) _raw[j] = _raw[j - 1] * 0.7f;
        }
        for (int j = NUM_BINS - 2; j >= 0; j--)
        {
            if (counts[j] == 0 && _raw[j] == 0) _raw[j] = _raw[j + 1] * 0.7f;
        }

        // Normalize. FFT magnitudes for windowed real input scale with
        // input amplitude; we map to a 0..1 visual range. Auto-gain
        // (running peak normalization) makes the bars hit ~85-95% of
        // canvas height regardless of source level — quiet recordings
        // still look full, loud ones use the headroom.
        //
        // TARGET 1.2: where we want the loudest band to land. CEILING 1.4
        // leaves a bit of overshoot room for transients without clipping.
        // PEAK_DECAY 0.99: ~3s half-life at ~23 FFTs/sec — fast enough to
        // adapt between songs / dynamic passages, slow enough not to
        // pump on every individual kick.
        const float CEILING = 1.4f;
        const float MIN_PEAK = 0.003f;     // floor so auto-gain doesn't divide by ~0
        const float TARGET = 1.2f;         // where we want recent peaks to land
        const float PEAK_DECAY = 0.99f;

        // Track the loudest current-frame raw value and let it decay so
        // the visual normalization adapts to the song's current dynamic
        // range. Without this, classical music with a wide dynamic range
        // would either be invisible in quiet passages or clipped in loud
        // ones.
        float framePeak = 0f;
        for (int j = 0; j < NUM_BINS; j++) if (_raw[j] > framePeak) framePeak = _raw[j];
        _runningPeak = MathF.Max(_runningPeak * PEAK_DECAY, framePeak);
        _runningPeak = MathF.Max(_runningPeak, MIN_PEAK);
        float effectiveGain = TARGET / _runningPeak;

        // Perceptual gamma — the human eye reads bar heights linearly, but
        // audio "loudness" is closer to logarithmic, so a target of 0.3
        // (a normal-volume note) looks much quieter than it sounds. Gamma
        // 0.65 stretches the bottom half of the range upward: 0.3 → 0.45,
        // 0.5 → 0.62, 1.0 → 1.0. Top end is untouched so we don't lose
        // dynamic range at the loud side.
        const float GAMMA = 0.65f;

        lock (_stateLock)
        {
            for (int j = 0; j < NUM_BINS; j++)
            {
                float linear = MathF.Min(CEILING, _raw[j] * effectiveGain);
                // Gamma curve. Only applied above ~3% of full scale so
                // genuine silence stays at 0 (gamma would otherwise lift
                // floor noise off the baseline).
                float target = linear < 0.03f ? linear : MathF.Pow(linear / CEILING, GAMMA) * CEILING;
                // Asymmetric smoothing — fast attack, slow release. Matches
                // the perceptual feel of a stereo EQ display.
                float a = target > _smoothed[j] ? 0.55f : 0.18f;
                _smoothed[j] = _smoothed[j] + (target - _smoothed[j]) * a;
            }

            // Aggregate envelopes (bass / mid / treble).
            float bs = 0, ms = 0, tr = 0;
            int bsN = 6, msN = 20, trN = NUM_BINS - 36;
            for (int i = 0; i < bsN; i++) bs += _smoothed[i];
            for (int i = 8; i < 8 + msN; i++) ms += _smoothed[i];
            for (int i = 36; i < NUM_BINS; i++) tr += _smoothed[i];
            _bass = bs / bsN;
            _mid = ms / msN;
            _treble = tr / trN;

            _lastUpdate = DateTimeOffset.UtcNow;
        }
    }

    public AudioSpectrumDto GetSnapshot()
    {
        lock (_stateLock)
        {
            var bins = new float[NUM_BINS];
            Array.Copy(_smoothed, bins, NUM_BINS);
            return new AudioSpectrumDto(
                Bins: bins,
                Bass: _bass,
                Mid: _mid,
                Treble: _treble,
                CaptureOk: !_captureFailed && _capture != null,
                UpdatedAtUtcMs: _lastUpdate.ToUnixTimeMilliseconds());
        }
    }

    public void Dispose()
    {
        try
        {
            if (_capture != null)
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
                _capture.StopRecording();
                _capture.Dispose();
            }
        }
        catch { /* shutting down */ }
    }
}

public sealed record AudioSpectrumDto(
    float[] Bins,
    float Bass,
    float Mid,
    float Treble,
    bool CaptureOk,
    long UpdatedAtUtcMs);
