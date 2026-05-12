using BetterXeneonWidget.Host.Config;
using NAudio.CoreAudioApi;
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
/// FFT size 2048 samples with a 50% hop → ~47 spectrum frames/sec at 48 kHz
/// and ~94/sec at 96 kHz. NAudio's FFT wants log2(N) as the "M" argument,
/// so 2048 = 2^11.
/// </summary>
public sealed class AudioSpectrumService : IDisposable
{
    // FFT_SIZE = 2048 → 23.4Hz resolution at 48kHz, 46.9Hz at 96kHz.
    // Starting the visual range near that practical floor avoids fake
    // sub-bass bands that are below the capture device's FFT resolution.
    private const int FFT_SIZE = 2048;
    private const int FFT_LOG = 11;
    private const int FFT_HOP = FFT_SIZE / 2;
    private const int NUM_BINS = 64;
    private const float FREQ_LOW = 45f;       // lowest bin lower edge (Hz)
    private const float FREQ_HIGH = 16000f;   // highest bin upper edge (Hz)

    private readonly ILogger<AudioSpectrumService> _log;
    private readonly ConfigService _config;
    private readonly MMDeviceEnumerator _enumerator;
    private WasapiLoopbackCapture? _capture;
    private string? _activeDeviceId;
    private string _activeDeviceName = "";
    private readonly float[] _sampleBuffer = new float[FFT_SIZE];
    private int _sampleBufferPos;
    private readonly Complex[] _fftBuffer = new Complex[FFT_SIZE];
    private readonly float[] _hann = new float[FFT_SIZE];
    private readonly object _captureLock = new();

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

    public AudioSpectrumService(
        ILogger<AudioSpectrumService> log,
        ConfigService config,
        MMDeviceEnumerator enumerator)
    {
        _log = log;
        _config = config;
        _enumerator = enumerator;
        // Precompute Hann window — reduces spectral leakage at the FFT
        // boundaries so the bins are crisp instead of smeared.
        for (int i = 0; i < FFT_SIZE; i++)
        {
            _hann[i] = 0.5f * (1f - (float)Math.Cos(2 * Math.PI * i / (FFT_SIZE - 1)));
        }
        // Start with whatever device the user configured last time. Null /
        // missing = default render device. We don't error if the saved
        // device is gone — fall back to default silently.
        TryStartCapture(_config.ReadAudioCaptureDeviceId());
    }

    /// <summary>
    /// Resolves a device by ID (or default if null/unknown) and (re)starts
    /// the capture pipeline against it. Safe to call repeatedly; tears
    /// down the previous capture first.
    /// </summary>
    public void SetCaptureDevice(string? deviceId, bool persist)
    {
        lock (_captureLock)
        {
            StopAndDisposeCapture();
            TryStartCapture(deviceId);
        }
        if (persist) _config.WriteAudioCaptureDeviceId(deviceId);
    }

    private MMDevice? ResolveDevice(string? deviceId)
    {
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            try
            {
                var d = _enumerator.GetDevice(deviceId);
                if (d != null && d.State == DeviceState.Active && d.DataFlow == DataFlow.Render)
                    return d;
                _log.LogWarning("Audio spectrum: configured device {Id} is not an active render endpoint; falling back to default.", deviceId);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Audio spectrum: configured device {Id} not found; falling back to default.", deviceId);
            }
        }
        try { return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
        catch { return null; }
    }

    private void TryStartCapture(string? requestedDeviceId)
    {
        try
        {
            var device = ResolveDevice(requestedDeviceId);
            if (device == null)
            {
                _captureFailed = true;
                _log.LogWarning("Audio spectrum: no active render device found.");
                return;
            }
            // Construct the capture. When the resolved device is the
            // current default, WasapiLoopbackCapture() (no args) is
            // equivalent — but always passing the device keeps the
            // start/stop/restart code path uniform.
            _capture = new WasapiLoopbackCapture(device);
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();
            _activeDeviceId = device.ID;
            _activeDeviceName = device.FriendlyName;
            _captureFailed = false;
            _log.LogInformation(
                "Audio spectrum: loopback capture started on '{Name}' ({Channels}ch, {Bits}-bit, {Rate}Hz)",
                _activeDeviceName,
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

    private void StopAndDisposeCapture()
    {
        var cap = _capture;
        _capture = null;
        if (cap == null) return;
        try
        {
            cap.DataAvailable -= OnDataAvailable;
            cap.RecordingStopped -= OnRecordingStopped;
            cap.StopRecording();
            cap.Dispose();
        }
        catch { /* shutting capture down — swallow */ }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // Common cause: default device changed (headphones unplugged, etc.).
        // Try once to restart with the configured device (or default if
        // none) — fail silently after that.
        if (e.Exception != null)
        {
            _log.LogWarning(e.Exception, "Audio spectrum: capture stopped on error. Attempting restart.");
        }
        try
        {
            lock (_captureLock)
            {
                StopAndDisposeCapture();
                TryStartCapture(_config.ReadAudioCaptureDeviceId());
            }
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
                Array.Copy(_sampleBuffer, FFT_HOP, _sampleBuffer, 0, FFT_SIZE - FFT_HOP);
                _sampleBufferPos = FFT_SIZE - FFT_HOP;
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

        // Precompute magnitudes once, then sample each visual log-band.
        // High-rate render devices (96kHz etc.) make the lowest bands
        // narrower than one FFT bin. Interpolating those unresolved bands
        // keeps the left edge smooth instead of manufacturing an artificial
        // stepped "intro section" from empty-bin backfill.
        Span<float> magnitudes = stackalloc float[half];
        for (int k = 1; k < half; k++)
        {
            magnitudes[k] = MathF.Sqrt(_fftBuffer[k].X * _fftBuffer[k].X + _fftBuffer[k].Y * _fftBuffer[k].Y);
        }

        for (int j = 0; j < NUM_BINS; j++)
        {
            float bandLo = edges[j];
            float bandHi = edges[j + 1];
            int loBin = Math.Max(1, (int)MathF.Ceiling(bandLo / hzPerBin));
            int hiBin = Math.Min(half - 1, (int)MathF.Floor(bandHi / hzPerBin));

            if (loBin <= hiBin)
            {
                float sum = 0f;
                int count = 0;
                for (int k = loBin; k <= hiBin; k++)
                {
                    sum += magnitudes[k];
                    count++;
                }
                _raw[j] = count > 0 ? sum / count : 0f;
            }
            else
            {
                float centerHz = MathF.Sqrt(bandLo * bandHi);
                float exactBin = centerHz / hzPerBin;
                int leftBin = Math.Clamp((int)MathF.Floor(exactBin), 1, half - 2);
                float frac = Math.Clamp(exactBin - leftBin, 0f, 1f);
                _raw[j] = magnitudes[leftBin] + (magnitudes[leftBin + 1] - magnitudes[leftBin]) * frac;
            }
        }

        // Normalize. FFT magnitudes for windowed real input scale with
        // input amplitude; we map to a 0..1 visual range. Auto-gain
        // (running peak normalization) makes the bars hit ~85-95% of
        // canvas height regardless of source level — quiet recordings
        // still look full, loud ones use the headroom.
        //
        // TARGET 1.2: where we want the loudest band to land. CEILING 1.4
        // leaves a bit of overshoot room for transients without clipping.
        // PEAK_DECAY 0.995: with the 50% FFT hop this stays slow enough
        // not to pump on individual kicks, while still adapting between
        // songs / dynamic passages.
        const float CEILING = 1.4f;
        const float MIN_PEAK = 0.003f;     // floor so auto-gain doesn't divide by ~0
        const float TARGET = 1.2f;         // where we want recent peaks to land
        const float PEAK_DECAY = 0.995f;

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
                // Asymmetric smoothing — fast attack, slower release. The
                // overlapped FFT already gives us denser updates, so these
                // coefficients keep the display tight without making quiet
                // floor noise flicker.
                float a = target > _smoothed[j] ? 0.62f : 0.22f;
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
                DeviceId: _activeDeviceId ?? "",
                DeviceName: _activeDeviceName,
                UpdatedAtUtcMs: _lastUpdate.ToUnixTimeMilliseconds());
        }
    }

    /// <summary>
    /// Enumerate every active render endpoint. The widget surfaces this
    /// list so users with software mixers (SteelSeries Sonar, Voicemeeter,
    /// Equalizer APO, etc.) can point the visualizer at the virtual
    /// device carrying their music — without having to change their
    /// Windows default playback device. Marks the system default + the
    /// one we're currently capturing from so the UI can highlight them.
    /// </summary>
    public IReadOnlyList<AudioSourceDto> ListRenderDevices()
    {
        var result = new List<AudioSourceDto>();
        string? defaultId = null;
        try { defaultId = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; }
        catch { /* no default — empty list of devices possible */ }

        try
        {
            // NAudio's MMDeviceCollection isn't IDisposable; the underlying
            // COM resources get released as MMDevice instances go out of
            // scope. We snapshot the metadata into our DTO list and let
            // GC handle the COM refs.
            var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            for (int i = 0; i < devices.Count; i++)
            {
                var d = devices[i];
                result.Add(new AudioSourceDto(
                    Id: d.ID,
                    Name: d.FriendlyName,
                    IsDefault: d.ID == defaultId,
                    IsActive: d.ID == _activeDeviceId));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Audio spectrum: failed to enumerate render endpoints.");
        }
        return result;
    }

    public void Dispose()
    {
        try { StopAndDisposeCapture(); } catch { /* shutting down */ }
    }
}

public sealed record AudioSpectrumDto(
    float[] Bins,
    float Bass,
    float Mid,
    float Treble,
    bool CaptureOk,
    string DeviceId,
    string DeviceName,
    long UpdatedAtUtcMs);

public sealed record AudioSourceDto(
    string Id,
    string Name,
    bool IsDefault,
    bool IsActive);
