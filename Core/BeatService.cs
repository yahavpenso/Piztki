using NAudio.Wave;

namespace Piztki.Core;

/// <summary>
/// Follows the rhythm of whatever is playing by measuring how much bass, mid, and treble energy
/// is in the sound going to the speakers. The samples are reduced to three numbers on the spot;
/// nothing is stored, recorded, or sent anywhere.
/// </summary>
public sealed class BeatService : IDisposable
{
    readonly object _gate = new();
    WasapiLoopbackCapture? _capture;

    // one-pole low-pass filter states used to split the sound into three bands
    double _lpLow, _lpMid;
    readonly double[] _envelope = new double[3];
    readonly double[] _floor = new double[3];
    readonly double[] _ceiling = { 1e-4, 1e-4, 1e-4 };
    readonly float[] _levels = new float[3];
    long _lastDataTicks;

    public bool Running => _capture != null;

    // ----- tempo: where the beats fall, so the mascot can move on them -----

    const int Hop = 100;                 // onset samples per second
    const int Window = 8 * Hop;          // how much music the tempo is worked out from
    readonly float[] _onsets = new float[Window];
    int _onsetAt;                        // next slot to write (the buffer is a ring)
    int _onsetCount;
    long _lastOnsetTicks;                // Stopwatch time of the newest onset sample
    int _sinceAnalysis;
    double _period;                      // seconds per beat; 0 while unknown
    double _beatOrigin;                  // Stopwatch seconds of one beat
    double _candidate;                   // a new tempo has to show up twice before it replaces the old one

    static double NowSeconds => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>The song's tempo in beats per minute, or 0 while it isn't known (silence, speech, the first seconds).</summary>
    public double Bpm
    {
        get
        {
            if (Environment.TickCount64 - Interlocked.Read(ref _lastDataTicks) > 400) return 0;
            double period = Volatile.Read(ref _period);
            return period > 0 ? 60 / period : 0;
        }
    }

    /// <summary>
    /// Where we are inside the current beat, 0..1 (0 = the beat just hit), plus the beat's length in
    /// seconds. False while the tempo isn't known.
    /// </summary>
    public bool TryGetBeat(out double phase, out double period)
    {
        phase = 0;
        period = Volatile.Read(ref _period);
        if (period <= 0 || Bpm <= 0) return false;
        double beats = (NowSeconds - Volatile.Read(ref _beatOrigin)) / period;
        phase = beats - Math.Floor(beats);
        return true;
    }

    readonly double[] _lastBand = new double[3];
    int _agree;

    /// <summary>For tests: the strongest repeat lengths of the last analysis (bpm:1x/2x/3x/4x/half).</summary>
    public static bool TraceTempo;
    public static string Trace = "";

    /// <summary>Adds one 10 ms slice to the onset record: how much louder each band got than a moment ago.</summary>
    void TrackOnsets(double low, double mid, double high)
    {
        // loudness on a log scale, so quiet and loud songs behave alike; kick and snare count most
        double rise = 0;
        Span<double> now = stackalloc double[] { low, mid, high };
        Span<double> weight = stackalloc double[] { 1.0, 0.7, 0.3 };
        for (int band = 0; band < 3; band++)
        {
            double level = Math.Log(1 + 3000 * now[band]);
            rise += weight[band] * Math.Max(0, level - _lastBand[band]);
            _lastBand[band] = level;
        }

        _onsets[_onsetAt] = (float)rise;
        _onsetAt = (_onsetAt + 1) % Window;
        if (_onsetCount < Window) _onsetCount++;

        if (++_sinceAnalysis >= Hop / 2 && _onsetCount >= 5 * Hop) // twice a second, once there are five seconds to look at
        {
            _sinceAnalysis = 0;
            _analysisDue = true; // done after the whole delivery is in, so the newest sample's time is known
        }
    }

    bool _analysisDue;

    /// <summary>The newest single measurement, before any steadying. For tests.</summary>
    public double RawBpm { get; private set; }

    /// <summary>
    /// Finds the beat length whose repeats line up best with the recent onsets, then the offset at
    /// which a comb of that spacing catches the most of them (the phase).
    /// </summary>
    void FindTempo()
    {
        int n = _onsetCount;
        var x = new float[n];
        double mean = 0;
        for (int i = 0; i < n; i++)
        {
            x[i] = _onsets[(_onsetAt - n + i + 2 * Window) % Window]; // oldest first
            mean += x[i];
        }
        mean /= n;
        double power = 0;
        for (int i = 0; i < n; i++)
        {
            x[i] = Math.Max(0, x[i] - (float)mean); // keep only what stands out: the hits
            power += x[i] * x[i];
        }
        // the hits are all positive, which would make every delay look like a fair match:
        // centre them on zero so only real repeats stand out
        double rest = 0;
        for (int i = 0; i < n; i++) rest += x[i];
        rest /= n;
        for (int i = 0; i < n; i++) x[i] -= (float)rest;
        if (power / n < 1e-5)
        {
            Forget(); // silence, or a steady hum with nothing to move to
            return;
        }

        // how well the sound repeats after each delay
        int minLag = Hop * 60 / 180, maxLag = Hop * 60 / 70;
        int reach = Math.Min(n - Hop, maxLag * 4);
        var corr = new double[reach + 1];
        for (int lag = minLag / 2; lag <= reach; lag++)
        {
            double sum = 0;
            for (int i = lag; i < n; i++) sum += x[i] * x[i - lag];
            corr[lag] = sum / (n - lag);
        }
        double At(double lag)
        {
            int i = (int)Math.Round(lag);
            return i >= 1 && i < reach ? Math.Max(corr[i], Math.Max(corr[i - 1], corr[i + 1])) : 0;
        }

        // a real beat also repeats after two, three, and four beats; a syncopated echo of it does not.
        // 70 to 180 beats a minute, with a gentle preference for the middle of that range
        int bestLag = 0;
        double best = 0;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            // ...and half a beat is where the hi-hats sit, which a dotted-note echo doesn't have either
            double score = corr[lag] + 0.8 * At(lag * 2.0) + 0.2 * At(lag * 3.0) + 0.8 * At(lag * 4.0) + 0.4 * At(lag * 0.5);
            double octaves = Math.Log2(60.0 * Hop / lag / 110);
            score *= Math.Exp(-0.5 * octaves * octaves / (0.8 * 0.8));
            if (score > best)
            {
                best = score;
                bestLag = lag;
            }
        }
        if (TraceTempo)
        {
            var sb = new System.Text.StringBuilder();
            for (int lag = minLag; lag <= maxLag; lag++)
                if (corr[lag] > corr[lag - 1] && corr[lag] >= corr[lag + 1] && corr[lag] > 0.3 * corr[bestLag])
                    sb.Append($"{60.0 * Hop / lag:0}:{corr[lag] / corr[bestLag]:0.00}/{At(lag * 2.0) / corr[bestLag]:0.00}/{At(lag * 3.0) / corr[bestLag]:0.00}/{At(lag * 4.0) / corr[bestLag]:0.00}/{At(lag * 0.5) / corr[bestLag]:0.00}  ");
            Trace = sb.ToString();
        }
        if (bestLag == 0 || corr[bestLag] < 0.05 * power / n)
        {
            Forget();
            return;
        }

        // sharpen the lag between two samples (parabola through the neighbours)
        double a = corr[bestLag - 1], b = corr[bestLag], c = corr[bestLag + 1];
        double shift = Math.Abs(a - 2 * b + c) < 1e-12 ? 0 : Math.Clamp(0.5 * (a - c) / (a - 2 * b + c), -0.5, 0.5);
        double found = (bestLag + shift) / Hop;

        RawBpm = 60 / found;
        double current = _period;
        // twice or half the speed we already dance at is the same beat counted differently: stay as we are
        if (current > 0)
        {
            if (Math.Abs(found / 2 - current) / current < 0.04) found /= 2;
            else if (Math.Abs(found * 2 - current) / current < 0.04) found *= 2;
        }
        double period;
        if (current > 0 && Math.Abs(found - current) / current < 0.04)
        {
            period = current * 0.85 + found * 0.15; // the same tempo, measured again: settle on it
            _candidate = 0;
            _agree = 0;
        }
        else if (current > 0)
        {
            // something different. One odd bar must not change the dance: it has to come back six times in a row (three seconds)
            if (_candidate > 0 && Math.Abs(found - _candidate) / _candidate < 0.04) _agree++;
            else
            {
                _candidate = found;
                _agree = 1;
            }
            // one-and-a-half times faster or slower is the classic mix-up (a dotted rhythm sounding like the beat):
            // that one has to hold for ten seconds before the dance changes
            double ratio = _candidate / current;
            bool dotted = Math.Abs(ratio - 1.5) < 0.07 || Math.Abs(ratio - 2.0 / 3) < 0.04 || Math.Abs(ratio - 0.75) < 0.04 || Math.Abs(ratio - 4.0 / 3) < 0.06;
            if (_agree >= (dotted ? 8 : 5))
            {
                period = _candidate;
                _candidate = 0;
                _agree = 0;
            }
            else period = current;
        }
        else
        {
            period = found;
        }

        // the phase: slide a comb of that spacing along the last few seconds
        double hopsPerBeat = period * Hop;
        int steps = Math.Max(1, (int)Math.Round(hopsPerBeat));
        int bestPhase = 0;
        double bestCatch = double.MinValue;
        for (int phase = 0; phase < steps; phase++)
        {
            double caught = 0;
            for (double at = n - 1 - phase; at >= 1; at -= hopsPerBeat)
            {
                int i = (int)Math.Round(at);
                // a beat rarely lands exactly on a sample: take its neighbours too
                caught += x[i] + 0.5 * (x[i - 1] + (i < n - 1 ? x[i + 1] : 0));
            }
            if (caught > bestCatch)
            {
                bestCatch = caught;
                bestPhase = phase;
            }
        }

        // keep the count going where it was unless the comb clearly moved: small corrections only
        double newest = _lastOnsetTicks / (double)System.Diagnostics.Stopwatch.Frequency;
        double origin = newest - bestPhase / (double)Hop;
        double old = _beatOrigin;
        if (current > 0 && Math.Abs(period - current) / current < 0.04)
        {
            double off = (origin - old) / period;
            off -= Math.Round(off);                 // -0.5..0.5 of a beat
            origin = old + Math.Round((newest - old) / period) * period + (Math.Abs(off) < 0.25 ? off * 0.3 : off) * period;
        }
        Volatile.Write(ref _beatOrigin, origin);
        Volatile.Write(ref _period, period);
    }

    /// <summary>A new song started: whatever tempo the last one had says nothing about this one.</summary>
    public void NewSong() => _newSong = true;

    volatile bool _newSong;

    void Forget()
    {
        Volatile.Write(ref _period, 0);
        _candidate = 0;
        _agree = 0;
    }

    /// <summary>Bass, mid, and treble right now, each 0..1 within the song's own recent range.</summary>
    public (double bass, double mid, double high) Levels
    {
        get
        {
            // while nothing is playing Windows sends no audio at all, so the bars simply rest
            if (Environment.TickCount64 - Interlocked.Read(ref _lastDataTicks) > 150) return (0, 0, 0);
            return (Volatile.Read(ref _levels[0]), Volatile.Read(ref _levels[1]), Volatile.Read(ref _levels[2]));
        }
    }

    public bool Start()
    {
        lock (_gate)
        {
            if (_capture != null) return true;
            try
            {
                var capture = new WasapiLoopbackCapture();
                capture.DataAvailable += OnData;
                capture.RecordingStopped += (_, _) => Stop(); // e.g. the user switched to headphones
                capture.StartRecording();
                _capture = capture;
                return true;
            }
            catch
            {
                _capture = null;
                return false; // no output device, or it can't be listened to
            }
        }
    }

    public void Stop()
    {
        WasapiLoopbackCapture? capture;
        lock (_gate)
        {
            capture = _capture;
            _capture = null;
        }
        if (capture == null) return;

        // stopping can block briefly and must not run on the capture's own callback thread
        Task.Run(() =>
        {
            try
            {
                capture.DataAvailable -= OnData;
                capture.StopRecording();
                capture.Dispose();
            }
            catch
            {
                // the device may already be gone
            }
        });
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        if (sender is not WasapiLoopbackCapture capture || e.BytesRecorded == 0) return;

        var format = capture.WaveFormat;
        int channels = Math.Max(1, format.Channels);
        int bytesPerSample = format.BitsPerSample / 8;
        bool isFloat = format.BitsPerSample == 32; // the Windows mixer hands out 32-bit float
        if (!isFloat && format.BitsPerSample != 16) return;

        int frames = e.BytesRecorded / (bytesPerSample * channels);
        if (frames == 0) return;

        // filter coefficients for ~150 Hz (kick and bass) and ~2.2 kHz (top of the mids)
        double rate = format.SampleRate;
        double aLow = 1 - Math.Exp(-2 * Math.PI * 150 / rate);
        double aMid = 1 - Math.Exp(-2 * Math.PI * 2200 / rate);

        double sumLow = 0, sumMid = 0, sumHigh = 0;
        double sliceLow = 0, sliceMid = 0, sliceHigh = 0;
        int sliceFrames = 0, sliceSize = Math.Max(1, (int)(rate / Hop));
        var buffer = e.Buffer;
        for (int f = 0; f < frames; f++)
        {
            double sample = 0;
            int offset = f * channels * bytesPerSample;
            for (int c = 0; c < channels; c++)
            {
                sample += isFloat
                    ? BitConverter.ToSingle(buffer, offset + c * 4)
                    : BitConverter.ToInt16(buffer, offset + c * 2) / 32768.0;
            }
            sample /= channels;

            _lpLow += aLow * (sample - _lpLow);
            _lpMid += aMid * (sample - _lpMid);
            double low = _lpLow, mid = _lpMid - _lpLow, high = sample - _lpMid;

            sumLow += low * low;
            sumMid += mid * mid;
            sumHigh += high * high;

            sliceLow += low * low;
            sliceMid += mid * mid;
            sliceHigh += high * high;
            if (++sliceFrames == sliceSize)
            {
                TrackOnsets(Math.Sqrt(sliceLow / sliceSize), Math.Sqrt(sliceMid / sliceSize), Math.Sqrt(sliceHigh / sliceSize));
                sliceLow = sliceMid = sliceHigh = 0;
                sliceFrames = 0;
            }
        }
        _lastOnsetTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_newSong)
        {
            _newSong = false;
            Forget();
            _onsetCount = 0;
            _sinceAnalysis = 0;
        }
        if (_analysisDue)
        {
            _analysisDue = false;
            FindTempo();
        }

        double seconds = frames / rate;
        Track(0, Math.Sqrt(sumLow / frames), seconds);
        Track(1, Math.Sqrt(sumMid / frames), seconds);
        Track(2, Math.Sqrt(sumHigh / frames), seconds);
        Interlocked.Exchange(ref _lastDataTicks, Environment.TickCount64);
    }

    /// <summary>
    /// Turns a band's raw energy into a 0..1 bar height. Modern music is mastered to be almost
    /// constantly loud, so measuring against silence would pin the bars at the top. Instead each
    /// band is measured between its own recent quietest and loudest moments, which is exactly the
    /// part that moves with the beat.
    /// </summary>
    void Track(int band, double energy, double seconds)
    {
        // jump up instantly on a hit, fall back quickly so the next hit is visible
        _envelope[band] = Math.Max(energy, _envelope[band] * Math.Exp(-seconds / 0.09));
        double env = _envelope[band];

        // the loud reference sinks slowly, the quiet reference rises slowly; both snap to new extremes
        _ceiling[band] = Math.Max(env, _ceiling[band] * Math.Exp(-seconds / 4.0));
        _floor[band] = Math.Min(env, _floor[band] + (_ceiling[band] - _floor[band]) * (1 - Math.Exp(-seconds / 2.5)));

        double range = _ceiling[band] - _floor[band];
        double level = range < 1e-6 ? 0 : Math.Clamp((env - _floor[band]) / range, 0, 1);
        Volatile.Write(ref _levels[band], (float)Math.Pow(level, 1.3)); // a slight curve makes the hits stand out
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Turns the running beat phase into one clean "now" per beat. The phase gets nudged as the tempo is
/// re-measured, so a plain "it went backwards" check would fire twice around a correction.
/// </summary>
public struct BeatEdge
{
    double _last;
    long _lastBeat;

    public bool Step(double phase, double period)
    {
        bool wrapped = _last - phase > 0.5; // a real wrap from near 1 back to near 0, not a small step back
        _last = phase;
        if (!wrapped) return false;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if ((now - _lastBeat) / (double)System.Diagnostics.Stopwatch.Frequency < 0.6 * period) return false;
        _lastBeat = now;
        return true;
    }
}
