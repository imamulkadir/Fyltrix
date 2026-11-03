using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Threading;
using Fyltrix.Core;

namespace Fyltrix.UI;

/// <summary>Document-scoped, public Windows accessibility scrolling. No browser internals or global input.</summary>
internal sealed class NativePdfAccess : IDisposable
{
    // UI Automation requires one non-UI thread for all event registration/removal in a process.
    // Keep that apartment alive across document reloads and simultaneous Compare windows.
    private static readonly Lazy<Dispatcher> Worker = new(() =>
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true, Name = "PDF accessibility" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    });
    internal Action<string>? Trace; // Optional regression diagnostics; no formatting/work when unset.
    private DispatcherTimer? _startup, _acknowledgement;
    private readonly double[] _desired = { double.NaN, double.NaN }, _inFlight = { double.NaN, double.NaN };
    private readonly long[] _sentAt = new long[2];
    private readonly int[] _attempts = new int[2];
    internal sealed record Geometry(System.Windows.Rect Bounds, double ScrollableHeight);
    private readonly Geometry?[] _geometry = new Geometry?[2];
    private readonly double[] _lastPercent = { double.NaN, double.NaN };
    private long _wheelAt;
    private int _wheelSource;
    private int _metricsQueued;
    private long _interactionAt;
    private int _interactionSource;
    private int _disposeRequested;
    private readonly AutomationElement?[] _roots = new AutomationElement?[2], _documents = new AutomationElement?[2];
    private readonly ScrollPattern?[] _scroll = new ScrollPattern?[2];
    private AutomationElement? _windowRoot;
    private readonly AutomationPropertyChangedEventHandler _changed;
    private readonly IntPtr _windowHandle;
    private readonly record struct PendingScroll(double Percent, double Tolerance, long SentAt);
    private readonly List<PendingScroll>[] _pendingEchoes = { new(), new() };
    private readonly object _echoGate = new();
    private int _appliedUpdates;
    internal int AppliedUpdates => Volatile.Read(ref _appliedUpdates);
    private readonly IntPtr[] _handles;
    private readonly Action<bool, bool, string?> _status;
    private readonly StructureChangedEventHandler _structure;
    private readonly object _pendingGate = new();
    private int _pendingSide, _discoverQueued;
    private double _pendingPercent;
    private bool _scrollQueued;
    private volatile bool _stopped, _enabled, _leftAvailable, _rightAvailable;
    internal bool LeftAvailable => _leftAvailable;
    internal bool RightAvailable => _rightAvailable;
    internal bool MatchesHandles(IntPtr left, IntPtr right) => _handles[0] == left && _handles[1] == right;
    internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal NativePdfAccess(IntPtr window, IntPtr left, IntPtr right, Action<bool, bool, string?> status)
    {
        _windowHandle = window;
        _handles = new[] { left, right };
        _status = status;
        _structure = (_, _) =>
        {
            if (Interlocked.Exchange(ref _discoverQueued, 1) == 0)
                Enqueue(() => { Interlocked.Exchange(ref _discoverQueued, 0); Discover(); });
        };
        _changed = (sender, args) =>
        {
            try
            {
                if (sender is not AutomationElement source) return;
                for (int side = 0; side < 2; side++)
                {
                    var document = _documents[side];
                    if (document != null && Automation.Compare(source, document))
                    {
                        if (args.Property == ScrollPattern.VerticalViewSizeProperty || args.Property == AutomationElement.BoundingRectangleProperty)
                            RefreshGeometry();
                        else if (args.Property == ScrollPattern.VerticalScrollPercentProperty && args.NewValue is double percent && percent >= 0)
                        { Trace?.Invoke($"event {side} {percent}"); QueueScroll(side, percent); }
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or System.Runtime.InteropServices.COMException or InvalidOperationException) { Trace?.Invoke($"event unavailable: {ex.GetType().Name}"); }
        };
        Enqueue(Start);
    }

    private void Enqueue(Action action)
    {
        if (_stopped) return;
        Worker.Value.BeginInvoke(new Action(() =>
        {
            if (_stopped) return;
            try { action(); }
            catch (Exception ex)
            {
                Diagnostics.Log("Native PDF accessibility", ex);
                _status(false, false, "Native PDF accessibility scrolling is unavailable.");
                Dispose();
            }
        }));
    }

    private void Start()
    {
        _windowRoot = AutomationElement.FromHandle(_windowHandle);
        for (int side = 0; side < 2; side++)
            if (_handles[side] != IntPtr.Zero) _roots[side] = AutomationElement.FromHandle(_handles[side]);
        Automation.AddStructureChangedEventHandler(_windowRoot, TreeScope.Subtree, _structure);
        _acknowledgement = new DispatcherTimer(DispatcherPriority.Background, Worker.Value) { Interval = TimeSpan.FromMilliseconds(200) };
        _acknowledgement.Tick += (_, _) => CheckAcknowledgements();
        RetryDiscovery();
    }

    private void RetryDiscovery()
    {
        _startup?.Stop();
        var deadline = Stopwatch.StartNew();
        _startup = new DispatcherTimer(DispatcherPriority.Background, Worker.Value) { Interval = TimeSpan.FromMilliseconds(200) };
        _startup.Tick += (_, _) =>
        {
            if (_stopped) { _startup?.Stop(); return; }
            Discover();
            bool missing = _handles[0] != IntPtr.Zero && !_leftAvailable || _handles[1] != IntPtr.Zero && !_rightAvailable;
            if (!missing || deadline.ElapsedMilliseconds >= 10000)
            {
                _startup?.Stop();
                if (missing) _status(_leftAvailable, _rightAvailable,
                    "Native PDF scrolling is unavailable until both viewers expose a scrollable document.");
            }
        };
        _startup.Start();
        Discover();
    }

    internal void Refresh(int side)
    {
        Enqueue(() =>
        {
            Trace?.Invoke($"refresh {side}");
            DetachDocument(side);
            if (side == 0) _leftAvailable = false; else _rightAvailable = false;
            _status(_leftAvailable, _rightAvailable, null);
            RetryDiscovery();
        });
    }

    private void Cleanup()
    {
        try
        {
            _startup?.Stop();
            _acknowledgement?.Stop();
            _startup = _acknowledgement = null;
            for (int side = 0; side < 2; side++) DetachDocument(side);
            if (_windowRoot != null) Automation.RemoveStructureChangedEventHandler(_windowRoot, _structure);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ElementNotAvailableException) { }
        finally
        {
            _roots[0] = _roots[1] = _windowRoot = null;
            Completion.TrySetResult();
        }
    }

    private void DetachDocument(int side)
    {
        try
        {
            if (_documents[side] != null)
                Automation.RemoveAutomationPropertyChangedEventHandler(_documents[side]!, _changed);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ElementNotAvailableException) { }
        _documents[side] = null;
        _scroll[side] = null;
        _desired[side] = _inFlight[side] = double.NaN;
        Volatile.Write(ref _geometry[side], null);
        Volatile.Write(ref _lastPercent[side], double.NaN);
        _attempts[side] = 0;
        lock (_echoGate) _pendingEchoes[side].Clear();
    }

    private (AutomationElement? Document, ScrollPattern? Pattern) FindDocument(int side)
    {
        if (_roots[side] == null) return (null, null);
        var candidates = _roots[side]!.FindAll(TreeScope.Subtree,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
        foreach (AutomationElement candidate in candidates)
            if (candidate.TryGetCurrentPattern(ScrollPattern.Pattern, out var value) &&
                ((ScrollPattern)value).Current.VerticallyScrollable)
                return (candidate, (ScrollPattern)value);
        return (null, null);
    }

    private void Discover()
    {
        for (int side = 0; side < 2; side++)
        {
            if (_roots[side] == null) continue;
            try
            {
                var (document, pattern) = FindDocument(side);
                if (document == null) continue;
                bool changed = _documents[side] == null || !Automation.Compare(_documents[side]!, document);
                if (changed)
                {
                    Trace?.Invoke($"replace document {side}");
                    DetachDocument(side);
                    Automation.AddAutomationPropertyChangedEventHandler(document, TreeScope.Element, _changed,
                        ScrollPattern.VerticalScrollPercentProperty, ScrollPattern.VerticalViewSizeProperty, AutomationElement.BoundingRectangleProperty);
                }
                _documents[side] = document;
                _scroll[side] = pattern;
                UpdateGeometry(side);
                Volatile.Write(ref _lastPercent[side], pattern!.Current.VerticalScrollPercent);
                if (side == 0) _leftAvailable = true; else _rightAvailable = true;
                if (changed)
                {
                    _status(_leftAvailable, _rightAvailable, null);
                    if (_leftAvailable && _rightAvailable && _enabled) AlignLeft();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ElementNotAvailableException) { }
        }
    }

    private void QueueScroll(int side, double percent)
    {
        if (_stopped) return;
        Volatile.Write(ref _lastPercent[side], percent);
        // Both native viewers already received this wheel gesture. Accessibility updates
        // arrive later and must not restart/jump either viewer's native scroll animation.
        if (Interlocked.Read(ref _wheelAt) != 0) return;
        lock (_echoGate)
        {
            var echoes = _pendingEchoes[side];
            echoes.RemoveAll(echo => Stopwatch.GetElapsedTime(echo.SentAt).TotalSeconds > 2);
            int match = echoes.FindIndex(echo => Math.Abs(percent-echo.Percent) <= echo.Tolerance);
            if (match >= 0)
            {
                double tolerance = echoes[match].Tolerance;
                echoes.RemoveAt(match);
                Enqueue(() => Acknowledged(side, percent, tolerance));
                return;
            }
        }
        if (!_enabled) return;
        if (!IsPreferredSource(side)) return;
        lock (_pendingGate)
        {
            _pendingSide = side; _pendingPercent = percent;
            if (_scrollQueued) return;
            _scrollQueued = true;
        }
        Enqueue(() =>
        {
            int source; double position;
            lock (_pendingGate) { source = _pendingSide; position = _pendingPercent; _scrollQueued = false; }
            Synchronize(source, position);
        });
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ClientRect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr window, out ClientRect rectangle);

    private void UpdateGeometry(int side)
    {
        if (_documents[side] == null || _scroll[side] == null) return;
        try
        {
            var bounds = _documents[side]!.Current.BoundingRectangle;
            var view = _scroll[side]!.Current.VerticalViewSize;
            double range = bounds.Height * (100 / Math.Max(.001, view) - 1);
            Volatile.Write(ref _geometry[side], new Geometry(bounds, range));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ElementNotAvailableException)
        { Volatile.Write(ref _geometry[side], null); }
    }

    internal void RefreshGeometry()
    {
        if (Interlocked.Exchange(ref _metricsQueued, 1) == 0)
            Enqueue(() => { Interlocked.Exchange(ref _metricsQueued, 0); UpdateGeometry(0); UpdateGeometry(1); });
    }

    internal bool TryWheelGeometry(int source, out Geometry? from, out Geometry? to)
    {
        from = Volatile.Read(ref _geometry[source]);
        to = Volatile.Read(ref _geometry[1-source]);
        return _enabled && !_stopped && _leftAvailable && _rightAvailable &&
            from is { ScrollableHeight: > 0 } && to is { ScrollableHeight: > 0 } &&
            !from.Bounds.IsEmpty && !to.Bounds.IsEmpty;
    }

    internal void BeginNativeWheel(int source)
    {
        Volatile.Write(ref _wheelSource, source);
        Interlocked.Exchange(ref _wheelAt, Stopwatch.GetTimestamp());
        Enqueue(() =>
        {
            _desired[0] = _desired[1] = _inFlight[0] = _inFlight[1] = double.NaN;
            lock (_echoGate) { _pendingEchoes[0].Clear(); _pendingEchoes[1].Clear(); }
            _acknowledgement?.Start();
        });
    }

    internal void CancelNativeWheel() => Interlocked.Exchange(ref _wheelAt, 0);

    internal void PreferNativeInput(int source)
    {
        Volatile.Write(ref _interactionSource, source);
        Interlocked.Exchange(ref _interactionAt, Stopwatch.GetTimestamp());
        if (Interlocked.Exchange(ref _wheelAt, 0) == 0) return;
        Enqueue(() =>
        {
            _desired[0] = _desired[1] = _inFlight[0] = _inFlight[1] = double.NaN;
            _acknowledgement?.Stop();
        });
    }

    private bool IsPreferredSource(int source)
    {
        long interaction = Interlocked.Read(ref _interactionAt);
        return interaction == 0 || Stopwatch.GetElapsedTime(interaction).TotalMilliseconds >= 500 || source == Volatile.Read(ref _interactionSource);
    }

    private double Epsilon(int side)
    {
        var state = _scroll[side]!.Current;
        double height = _documents[side]!.Current.BoundingRectangle.Height;
        // Accessibility can temporarily report empty screen bounds for offscreen/covered
        // browser fragments. Use the host's real client size rather than a 100% tolerance.
        if (height <= 0 && GetClientRect(_handles[side], out var client)) height = client.Bottom-client.Top;
        double range = height * (100 / Math.Max(.001, state.VerticalViewSize) - 1);
        double tolerance = range > 0 ? Math.Min(.1, 100 / range) : .01;
        Trace?.Invoke($"geometry {side}: height={height}, view={state.VerticalViewSize}, tolerance={tolerance}");
        return tolerance;
    }

    private void Synchronize(int source, double percent)
    {
        if (!_enabled || _scroll[source] == null || _scroll[1-source] == null || percent < 0 || !IsPreferredSource(source)) return;
        // Source input supersedes an older requested alignment of this pane.
        _desired[source] = double.NaN;
        int target = 1-source;
        _desired[target] = Math.Clamp(percent, 0, 100);
        SendDesired(target);
    }

    private void SendDesired(int side)
    {
        if (!_enabled || double.IsNaN(_desired[side])) return;
        if (!double.IsNaN(_inFlight[side])) { _acknowledgement?.Start(); return; }
        try
        {
            var (document, pattern) = FindDocument(side);
            if (document == null || pattern == null) return;
            _scroll[side] = pattern;
            double position = _desired[side], tolerance = Epsilon(side);
            if (Math.Abs(pattern.Current.VerticalScrollPercent-position) <= tolerance)
            { _desired[side] = double.NaN; return; }
            // A native action can return before it completes, and overlapping actions may
            // be ignored. Keep only the newest goal; send it after the preceding action's ack.
            _inFlight[side] = position;
            _sentAt[side] = Stopwatch.GetTimestamp();
            lock (_echoGate)
            {
                var echoes = _pendingEchoes[side];
                echoes.RemoveAll(echo => Stopwatch.GetElapsedTime(echo.SentAt).TotalSeconds > 2);
                echoes.Add(new PendingScroll(position, tolerance, _sentAt[side]));
            }
            Interlocked.Increment(ref _appliedUpdates);
            pattern.SetScrollPercent(ScrollPattern.NoScroll, position);
            _acknowledgement?.Start();
            Trace?.Invoke($"sent {side} {position}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ElementNotAvailableException)
        {
            DetachDocument(side);
            if (side == 0) _leftAvailable = false; else _rightAvailable = false;
            _status(_leftAvailable, _rightAvailable, "Reconnecting native PDF scrolling...");
            RetryDiscovery();
        }
    }

    private void Acknowledged(int side, double percent, double tolerance)
    {
        if (double.IsNaN(_inFlight[side]) || Math.Abs(percent-_inFlight[side]) > tolerance) return;
        _inFlight[side] = double.NaN;
        _attempts[side] = 0;
        if (Math.Abs(percent-_desired[side]) <= tolerance) _desired[side] = double.NaN;
        if (_enabled) SendDesired(side);
    }

    private void CheckAcknowledgements()
    {
        long wheelAt = Interlocked.Read(ref _wheelAt);
        if (wheelAt != 0)
        {
            // A gesture-local settle check, not a continuous position poll. Native inertia
            // runs uninterrupted; correct any scaled/rounded residual once the gesture rests.
            if (Stopwatch.GetElapsedTime(wheelAt).TotalMilliseconds < 650) return;
            if (Interlocked.CompareExchange(ref _wheelAt, 0, wheelAt) != wheelAt) return;
            int source = Volatile.Read(ref _wheelSource);
            double percent = Volatile.Read(ref _lastPercent[source]);
            if (_enabled && !double.IsNaN(percent)) Synchronize(source, percent);
        }
        bool waiting = false;
        for (int side = 0; side < 2; side++)
        {
            if (!_enabled || double.IsNaN(_inFlight[side])) continue;
            waiting = true;
            if (Stopwatch.GetElapsedTime(_sentAt[side]).TotalMilliseconds < 500) continue;
            // Only outstanding actions have a bounded watchdog. There is no idle scroll poll.
            _inFlight[side] = double.NaN;
            if (++_attempts[side] <= 3) SendDesired(side);
            else
            {
                _desired[side] = double.NaN;
                _enabled = false;
                _status(_leftAvailable, _rightAvailable, "Native PDF scrolling did not respond. Reload the PDF to reconnect.");
            }
        }
        if (!waiting) _acknowledgement?.Stop();
    }

    internal void EnableSync(bool enabled)
    {
        Trace?.Invoke($"enable {enabled}");
        _enabled = enabled;
        if (!enabled) CancelNativeWheel();
        Enqueue(() =>
        {
            if (!enabled) { _desired[0] = _desired[1] = double.NaN; _acknowledgement?.Stop(); }
            else if (_enabled) AlignLeft();
        });
    }

    private void AlignLeft()
    {
        if (_scroll[0] == null || _scroll[1] == null) return;
        double percent = _scroll[0]!.Current.VerticalScrollPercent;
        Synchronize(0, Math.Max(0, percent));
    }

    internal void GoToPage(int side, int page, IReadOnlyList<double> heights)
    {
        PreferNativeInput(side); // an explicit comment-page choice supersedes a preceding wheel gesture
        Enqueue(() =>
        {
            if (_scroll[side] == null || page < 1 || page > heights.Count) return;
            try
            {
                double fraction = heights.Take(page-1).Sum() / heights.Sum();
                double viewport = _scroll[side]!.Current.VerticalViewSize / 100;
                // Move slightly inside the requested page to account for native inter-page separators.
                if (page > 1) fraction += heights[page-1] / heights.Sum() * .02;
                double percent = Math.Clamp(100 * fraction / Math.Max(.0001, 1-viewport), 0, 100);
                var (_, pattern) = FindDocument(side);
                if (pattern != null) pattern.SetScrollPercent(ScrollPattern.NoScroll, percent);
                // Explicit navigation may not emit a fresh source accessibility event
                // (e.g. the requested page is already visible or a comments popup has focus).
                if (_enabled) Synchronize(side, percent);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ElementNotAvailableException)
            { _status(_leftAvailable, _rightAvailable, "Use the native page control to navigate this PDF."); }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0) return;
        _enabled = false;
        _stopped = true;
        Worker.Value.BeginInvoke(new Action(Cleanup));
    }
}
