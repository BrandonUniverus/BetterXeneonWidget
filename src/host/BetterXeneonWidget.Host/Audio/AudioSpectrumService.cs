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
    private const int FFT_SIZE = 1024;
    private const int FFT_LOG = 10;
    private const int NUM_BINS = 64;
    private const float FREQ_LOW = 30f;       // lowest bin lower edge (Hz)
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

        // Normalize. FFT magnitudes for windowed real input scale roughly with
        // input amplitude; we map to a 0..1 visual range with a soft knee so
        // quiet music still moves the bars. The 18× factor is empirically
        // tuned against typical music — adjust if everything looks too low
        // or pinned to the top.
        const float GAIN = 18f;
        const float CEILING = 1.4f;

        lock (_stateLock)
        {
            for (int j = 0; j < NUM_BINS; j++)
            {
                float target = MathF.Min(CEILING, _raw[j] * GAIN);
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
