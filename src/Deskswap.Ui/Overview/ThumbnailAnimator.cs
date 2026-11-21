using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Deskswap.Core;
using Deskswap.Platform;

namespace Deskswap.Ui.Overview;

/// <summary>
/// Moves live previews on a thread of their own, one step per frame the compositor draws (overview-ux.md §9,
/// performance). Previews are drawn by the compositor, not by WPF, so moving them from the UI thread tied them to
/// WPF's frame timing and to whatever else the UI thread was doing; here each frame gets exactly one update, timed by
/// the compositor itself (<c>DwmFlush</c>).
/// </summary>
/// <remarks>
/// While a preview moves here, nothing else may update it: the UI thread calls <see cref="Stop"/> first, which
/// returns where it was. Completion callbacks run on the UI thread.
/// </remarks>
internal sealed class ThumbnailAnimator : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<WindowThumbnail, Motion> _motions = [];
    private readonly AutoResetEvent _wake = new(initialState: false);
    private readonly Dispatcher _dispatcher;
    private Thread? _thread;
    private volatile bool _stopping;

    /// <summary>Initializes a new instance of the <see cref="ThumbnailAnimator"/> class.</summary>
    /// <param name="dispatcher">The UI thread's dispatcher, where callbacks run.</param>
    public ThumbnailAnimator(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>Raised on the UI thread when every motion has finished, with how smoothly they ran.</summary>
    public event EventHandler<string>? Measured;

    /// <summary>Starts moving a preview, replacing any motion it had.</summary>
    /// <param name="thumbnail">The preview.</param>
    /// <param name="from">Where it starts, in its overview window's pixels.</param>
    /// <param name="to">Where it ends.</param>
    /// <param name="durationMs">How long it takes.</param>
    /// <param name="easeIn">Accelerate (leaving) rather than decelerate (arriving).</param>
    /// <param name="opacity">Its opacity, 0 to 255.</param>
    /// <param name="finished">Called on the UI thread when it arrives (not when stopped).</param>
    public void Start(WindowThumbnail thumbnail, Rect from, Rect to, double durationMs, bool easeIn, byte opacity, Action finished)
    {
        lock (_gate)
        {
            _motions[thumbnail] = new Motion(from, to, durationMs, easeIn, opacity, finished) { Current = from };
        }

        EnsureThread();
        _wake.Set();
    }

    /// <summary>Stops moving a preview so the UI thread can update it.</summary>
    /// <param name="thumbnail">The preview.</param>
    /// <param name="current">Where it is now.</param>
    /// <param name="remainingMs">How much of its motion was left.</param>
    /// <returns><see langword="true"/> if it was moving.</returns>
    public bool Stop(WindowThumbnail thumbnail, out Rect current, out double remainingMs)
    {
        lock (_gate)
        {
            if (_motions.Remove(thumbnail, out var motion))
            {
                current = motion.Current;
                remainingMs = motion.Start == 0 ? motion.Duration
                    : Math.Max(0, motion.Duration - Stopwatch.GetElapsedTime(motion.Start).TotalMilliseconds);
                return true;
            }
        }

        current = default;
        remainingMs = 0;
        return false;
    }

    /// <summary>Gets a value indicating whether a preview is moving.</summary>
    /// <param name="thumbnail">The preview.</param>
    /// <returns><see langword="true"/> while it moves.</returns>
    public bool IsMoving(WindowThumbnail thumbnail)
    {
        lock (_gate)
        {
            return _motions.ContainsKey(thumbnail);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(1));
        lock (_gate)
        {
            _motions.Clear();
        }

        _wake.Dispose();
    }

    private void EnsureThread()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Deskswap preview motion",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    private void Run()
    {
        var finished = new List<Action>();
        var arrived = new List<WindowThumbnail>();
        var timing = new FrameTiming();
        while (!_stopping)
        {
            bool any;
            lock (_gate)
            {
                any = _motions.Count > 0;
            }

            if (!any)
            {
                if (timing.Frames > 0)
                {
                    string report = timing.Report("Preview motion");
                    _dispatcher.BeginInvoke(() => Measured?.Invoke(this, report));
                    timing = new FrameTiming();
                }

                _wake.WaitOne();
                continue;
            }

            long now = Stopwatch.GetTimestamp();
            timing.Frame(now);
            lock (_gate)
            {
                foreach (var (thumbnail, motion) in _motions)
                {
                    if (motion.Start == 0)
                    {
                        motion.Start = now; // Timed from the first frame, so a late start never skips ahead.
                    }

                    double t = motion.Duration <= 0 ? 1 : Math.Clamp(Stopwatch.GetElapsedTime(motion.Start, now).TotalMilliseconds / motion.Duration, 0, 1);
                    double eased = motion.EaseIn ? t * t * t : 1 - Math.Pow(1 - t, 3);
                    motion.Current = Lerp(motion.From, motion.To, eased);
                    try
                    {
                        thumbnail.Update(ToScreenRect(motion.Current), motion.Opacity);
                    }
                    catch (ObjectDisposedException)
                    {
                        t = 1; // Released without being stopped; an exception here would end the process.
                    }

                    if (t >= 1)
                    {
                        arrived.Add(thumbnail);
                        finished.Add(motion.Finished);
                    }
                }

                foreach (var thumbnail in arrived)
                {
                    _motions.Remove(thumbnail);
                }

                arrived.Clear();
            }

            if (finished.Count > 0)
            {
                var callbacks = finished.ToArray();
                finished.Clear();
                _dispatcher.BeginInvoke(() =>
                {
                    foreach (var callback in callbacks)
                    {
                        callback();
                    }
                });
            }

            timing.Work(Stopwatch.GetTimestamp() - now);

            // One update per composed frame: wait until the compositor has drawn this one.
            ScreenCapture.WaitForCompositor();
        }
    }

    private static ScreenRect ToScreenRect(Rect r) =>
        ScreenRect.FromSize((int)Math.Round(r.X), (int)Math.Round(r.Y), Math.Max(1, (int)Math.Round(r.Width)), Math.Max(1, (int)Math.Round(r.Height)));

    private static Rect Lerp(Rect a, Rect b, double t) => new(
        a.X + ((b.X - a.X) * t),
        a.Y + ((b.Y - a.Y) * t),
        Math.Max(1, a.Width + ((b.Width - a.Width) * t)),
        Math.Max(1, a.Height + ((b.Height - a.Height) * t)));

    private sealed class Motion(Rect from, Rect to, double duration, bool easeIn, byte opacity, Action finished)
    {
        public Rect From { get; } = from;

        public Rect To { get; } = to;

        public double Duration { get; } = duration;

        public bool EaseIn { get; } = easeIn;

        public byte Opacity { get; } = opacity;

        public Action Finished { get; } = finished;

        public Rect Current { get; set; }

        public long Start { get; set; }
    }
}

/// <summary>
/// Frame times of one animation, for the log: how many frames, how long the longest gap between two was, and how
/// many arrived late (more than 1.5 times the typical gap, i.e. a dropped frame).
/// </summary>
internal sealed class FrameTiming
{
    private readonly List<double> _gaps = [];
    private long _first;
    private long _last;
    private double _workTotal;
    private double _workMax;

    /// <summary>Gets the number of frames counted.</summary>
    public int Frames { get; private set; }

    /// <summary>Counts a frame.</summary>
    /// <param name="now">Its <see cref="Stopwatch"/> timestamp.</param>
    public void Frame(long now)
    {
        if (_first == 0)
        {
            _first = now;
        }
        else
        {
            _gaps.Add(Stopwatch.GetElapsedTime(_last, now).TotalMilliseconds);
        }

        _last = now;
        Frames++;
    }

    /// <summary>Counts the work done for the last frame.</summary>
    /// <param name="elapsed">Its length in <see cref="Stopwatch"/> ticks.</param>
    public void Work(long elapsed)
    {
        double ms = elapsed * 1000.0 / Stopwatch.Frequency;
        _workTotal += ms;
        _workMax = Math.Max(_workMax, ms);
    }

    /// <summary>Describes the timing, for the log.</summary>
    /// <param name="what">What was animated.</param>
    /// <returns>One line.</returns>
    public string Report(string what)
    {
        double typical = 0;
        if (_gaps.Count > 0)
        {
            var sorted = _gaps.Order().ToList();
            typical = sorted[sorted.Count / 2];
        }

        double worst = _gaps.Count == 0 ? 0 : _gaps.Max();
        int late = typical <= 0 ? 0 : _gaps.Count(g => g > typical * 1.5);
        double total = _last == 0 ? 0 : Stopwatch.GetElapsedTime(_first, _last).TotalMilliseconds;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{what}: {Frames} frames in {total:0} ms, typical {typical:0.0} ms, worst {worst:0.0} ms, {late} late; work avg {(Frames == 0 ? 0 : _workTotal / Frames):0.00} ms, max {_workMax:0.00} ms.");
    }
}
