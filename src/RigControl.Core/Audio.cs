using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RigControl.Core;

/// <summary>What the sound is doing right now, for audio-reactive lighting. Values are 0..1.</summary>
/// <param name="Level">Overall loudness.</param>
/// <param name="Bands">Loudness per frequency band, bass first (log-spaced, 40 Hz – 16 kHz).</param>
/// <param name="Beat">1 right on a beat, fading towards 0 until the next.</param>
/// <param name="Beats">How many beats so far (effects step through colours with it).</param>
public sealed record AudioFrame(float Level, float[] Bands, float Beat, int Beats)
{
    public static AudioFrame Silent { get; } = new(0, new float[AudioAnalyzer.BandCount], 0, 0);
}

/// <summary>
/// Turns audio samples into an <see cref="AudioFrame"/>: a 2048-point FFT every 512 samples, log-spaced bands,
/// automatic gain (quiet and loud music both fill the range) and beat detection on the bass. Only these numbers
/// leave the analyzer; the sound itself is never stored.
/// </summary>
public sealed class AudioAnalyzer
{
    public const int BandCount = 16;
    const int Size = 2048, Hop = 512;
    const double MinHz = 40, MaxHz = 16000;

    readonly float[] _ring = new float[Size];
    int _write, _sinceHop;
    readonly float[] _bands = new float[BandCount];
    double _level, _peak = 1e-4, _bandPeak = 1e-4;
    readonly Queue<double> _bassHistory = new();
    double _bassSum, _lastBeatAt = -1, _beat;
    int _beats;
    double _t; // seconds of audio analysed
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _lastFeed = double.NegativeInfinity;
    AudioFrame _frame = AudioFrame.Silent;

    static readonly float[] Window = [.. Enumerable.Range(0, Size).Select(i => (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Size - 1))))];

    /// <summary>The latest frame. Fades out when no audio arrives (a paused player sends nothing at all).</summary>
    public AudioFrame Current
    {
        get
        {
            double quiet = _clock.Elapsed.TotalSeconds - _lastFeed;
            if (quiet < 0.15) return _frame;
            float k = (float)Math.Exp(-(quiet - 0.15) * 6);
            var f = _frame;
            return f with { Level = f.Level * k, Bands = [.. f.Bands.Select(b => b * k)], Beat = f.Beat * k };
        }
    }

    /// <summary>Adds mono samples (-1..1).</summary>
    public void Feed(ReadOnlySpan<float> samples, int sampleRate)
    {
        _lastFeed = _clock.Elapsed.TotalSeconds;
        foreach (var s in samples)
        {
            _ring[_write] = s;
            _write = (_write + 1) % Size;
            if (++_sinceHop < Hop) continue;
            _sinceHop = 0;
            _t += Hop / (double)sampleRate;
            Analyze(sampleRate);
        }
    }

    void Analyze(int sampleRate)
    {
        var re = new float[Size];
        var im = new float[Size];
        double sumSq = 0;
        for (int i = 0; i < Size; i++)
        {
            float s = _ring[(_write + i) % Size];
            sumSq += s * s;
            re[i] = s * Window[i];
        }
        Fft(re, im);

        // Band energies: average magnitude over log-spaced bins.
        var raw = new double[BandCount];
        double binHz = sampleRate / (double)Size;
        for (int b = 0; b < BandCount; b++)
        {
            double lo = MinHz * Math.Pow(MaxHz / MinHz, b / (double)BandCount), hi = MinHz * Math.Pow(MaxHz / MinHz, (b + 1) / (double)BandCount);
            int from = Math.Max(1, (int)(lo / binHz)), to = Math.Min(Size / 2 - 1, Math.Max(from, (int)(hi / binHz)));
            double sum = 0;
            for (int k = from; k <= to; k++) sum += Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            raw[b] = sum / (to - from + 1);
        }

        // Automatic gain: follow the loudest recent value, let it sink slowly so quiet passages still show.
        double rms = Math.Sqrt(sumSq / Size);
        _peak = Math.Max(rms, Math.Max(_peak * 0.997, 1e-4));
        _bandPeak = Math.Max(raw.Max(), Math.Max(_bandPeak * 0.997, 1e-4));
        double level = Math.Clamp(rms / _peak, 0, 1);
        _level = level > _level ? level : _level * 0.85 + level * 0.15; // fast attack, gentle release
        for (int b = 0; b < BandCount; b++)
        {
            float v = (float)Math.Clamp(Math.Sqrt(raw[b] / _bandPeak), 0, 1); // sqrt: closer to how loud it sounds
            _bands[b] = v > _bands[b] ? v : _bands[b] * 0.8f + v * 0.2f;
        }

        // Beats: the bass jumping well above its average of the last second.
        double bass = raw[0] + raw[1] + raw[2];
        _bassHistory.Enqueue(bass);
        _bassSum += bass;
        int keep = (int)(sampleRate / (double)Hop); // about one second
        while (_bassHistory.Count > keep) _bassSum -= _bassHistory.Dequeue();
        double average = _bassSum / _bassHistory.Count;
        if (_bassHistory.Count >= keep / 2 && bass > average * 1.5 && bass > _bandPeak * 0.15 && _t - _lastBeatAt > 0.2)
        {
            _lastBeatAt = _t;
            _beats++;
            _beat = 1;
        }
        else _beat *= Math.Exp(-Hop / (double)sampleRate * 6); // fades in about half a second

        _frame = new AudioFrame((float)_level, [.. _bands], (float)_beat, _beats);
    }

    /// <summary>In-place radix-2 FFT (length a power of two).</summary>
    internal static void Fft(float[] re, float[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double a = -2 * Math.PI / len;
            float wr = (float)Math.Cos(a), wi = (float)Math.Sin(a);
            for (int i = 0; i < n; i += len)
            {
                float cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int p = i + k, q = p + len / 2;
                    float tr = re[q] * cr - im[q] * ci, ti = re[q] * ci + im[q] * cr;
                    re[q] = re[p] - tr; im[q] = im[p] - ti;
                    re[p] += tr; im[p] += ti;
                    (cr, ci) = (cr * wr - ci * wi, cr * wi + ci * wr);
                }
            }
        }
    }
}

/// <summary>
/// Listens to what the speakers play (loopback) or to a microphone, and feeds an <see cref="AudioAnalyzer"/>.
/// Runs only while an audio effect is active; restarts itself when the device goes away or comes back.
/// </summary>
public sealed class AudioListener : IDisposable
{
    readonly AudioAnalyzer _analyzer = new();
    readonly object _lock = new();
    IWaveIn? _capture;
    (bool input, string? device)? _want, _running;
    Timer? _retry;

    public AudioFrame Current => _running is null ? AudioFrame.Silent : _analyzer.Current;

    /// <summary>Audio devices to choose from: (id, name). Outputs are listened to as "what you hear".</summary>
    public static List<(string id, string name)> Devices(bool input)
    {
        try
        {
            using var e = new MMDeviceEnumerator();
            return [.. e.EnumerateAudioEndPoints(input ? DataFlow.Capture : DataFlow.Render, DeviceState.Active).Select(d => (d.ID, d.FriendlyName))];
        }
        catch { return []; }
    }

    /// <summary>Listen to this source (null device = the Windows default), or to nothing.</summary>
    public void Listen((bool input, string? device)? source)
    {
        lock (_lock)
        {
            _want = source;
            if (_running == source && _capture is not null) return;
            Stop();
            if (source is { } s) Start(s);
        }
    }

    void Start((bool input, string? device) s)
    {
        try
        {
            using var e = new MMDeviceEnumerator();
            var dev = s.device is string id ? e.GetDevice(id) : e.GetDefaultAudioEndpoint(s.input ? DataFlow.Capture : DataFlow.Render, Role.Multimedia);
            IWaveIn cap = s.input ? new WasapiCapture(dev) : new WasapiLoopbackCapture(dev);
            var format = cap.WaveFormat;
            cap.DataAvailable += (_, a) => Feed(a, format);
            cap.RecordingStopped += (_, a) =>
            {
                // Unplugged, default device changed, exclusive mode taken: try again shortly.
                if (a.Exception is not null) AppConfig.Log?.Invoke($"audio: capture stopped ({a.Exception.Message})");
                lock (_lock) if (_capture == cap) { _capture = null; _running = null; ScheduleRetry(); }
            };
            cap.StartRecording();
            _capture = cap;
            _running = s;
            AppConfig.Log?.Invoke($"audio: listening to {(s.input ? "microphone" : "output")} \"{dev.FriendlyName}\" ({format.SampleRate} Hz, {format.Channels} ch)");
        }
        catch (Exception ex)
        {
            AppConfig.Log?.Invoke($"audio: can't listen ({ex.Message})");
            ScheduleRetry();
        }
    }

    void ScheduleRetry()
    {
        _retry?.Dispose();
        _retry = new Timer(_ => { lock (_lock) if (_want is { } w && _capture is null) Start(w); }, null, 2000, Timeout.Infinite);
    }

    void Feed(WaveInEventArgs a, WaveFormat f)
    {
        int ch = Math.Max(1, f.Channels), frames = a.BytesRecorded / Math.Max(1, f.BlockAlign);
        if (frames == 0) return;
        // High-rate interfaces (96/192 kHz): average down to about 48 kHz, so the bass bands keep their resolution.
        int step = Math.Max(1, f.SampleRate / 48000);
        var mono = new float[frames / step];
        var span = a.Buffer.AsSpan(0, a.BytesRecorded);
        bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat || (f.Encoding == WaveFormatEncoding.Extensible && f.BitsPerSample == 32);
        for (int i = 0; i < frames; i++)
        {
            float sum = 0;
            for (int c = 0; c < ch; c++)
            {
                int o = i * f.BlockAlign + c * (f.BitsPerSample / 8);
                sum += isFloat ? BitConverter.ToSingle(span.Slice(o, 4))
                    : f.BitsPerSample == 16 ? BitConverter.ToInt16(span.Slice(o, 2)) / 32768f
                    : f.BitsPerSample == 24 ? ((span[o] | span[o + 1] << 8 | (sbyte)span[o + 2] << 16) / 8388608f)
                    : BitConverter.ToInt32(span.Slice(o, 4)) / 2147483648f;
            }
            if (i / step < mono.Length) mono[i / step] += sum / ch / step;
        }
        _analyzer.Feed(mono, f.SampleRate / step);
    }

    void Stop()
    {
        _retry?.Dispose();
        _retry = null;
        var cap = _capture;
        _capture = null;
        _running = null;
        if (cap is null) return;
        try { cap.StopRecording(); } catch { }
        cap.Dispose();
        AppConfig.Log?.Invoke("audio: stopped listening");
    }

    public void Dispose() { lock (_lock) { _want = null; Stop(); } }
}
