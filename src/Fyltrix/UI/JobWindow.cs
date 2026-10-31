using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Fyltrix.Core;

namespace Fyltrix.UI;

/// <summary>
/// Runs work on a background thread with a small, non-blocking progress window (Cancel button) that only
/// appears if the work takes longer than a moment. The main window stays usable meanwhile.
/// </summary>
public sealed class JobWindow : Window
{
    private static readonly List<CancellationTokenSource> Active = new();

    private readonly TextBlock _status = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _detail = new() { TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Theme.Brush("Fg2"), Margin = new Thickness(0, 4, 0, 10) };
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1 };
    private readonly CancellationTokenSource _cts;
    private bool _finished;

    public static int RunningCount => Active.Count;

    /// <summary>Cancels every running job (used on shutdown).</summary>
    public static void CancelAll()
    {
        foreach (var c in Active.ToList()) { try { c.Cancel(); } catch (ObjectDisposedException) { } }
    }

    private JobWindow(Window owner, string title, CancellationTokenSource cts, bool cancellable)
    {
        _cts = cts;
        Owner = owner;
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ShowActivated = true;
        Background = Theme.Brush("Bg2");
        Foreground = Theme.Brush("Fg1");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 13;
        SourceInitialized += (_, _) => Shell.UseDarkTitleBar(this);

        var sp = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        _status.Text = title;
        sp.Children.Add(_status);
        sp.Children.Add(_detail);
        sp.Children.Add(_bar);
        _bar.IsIndeterminate = true;
        var cancel = Dialogs.Button("Cancel");
        cancel.HorizontalAlignment = HorizontalAlignment.Right;
        cancel.Margin = new Thickness(0, 14, 0, 0);
        cancel.IsEnabled = cancellable;
        cancel.Click += (_, _) => RequestCancel(cancel);
        sp.Children.Add(cancel);
        Content = sp;
        Closing += (_, e) =>
        {
            if (_finished) return;
            e.Cancel = true; // closing = cancel; the window goes away when the work has stopped
            if (cancellable) RequestCancel(cancel);
        };
    }

    private void RequestCancel(Button b)
    {
        _cts.Cancel();
        b.IsEnabled = false;
        _status.Text = "Cancelling…";
    }

    private void Apply(OperationProgress p)
    {
        if (_cts.IsCancellationRequested) return;
        _status.Text = p.Status;
        _detail.Text = p.Detail ?? "";
        if (p.Fraction < 0) _bar.IsIndeterminate = true;
        else { _bar.IsIndeterminate = false; _bar.Value = Math.Clamp(p.Fraction, 0, 1); }
    }

    private void Finish()
    {
        _finished = true;
        Close();
    }

    public static Task RunAsync(Window owner, string title, Action<IProgress<OperationProgress>, CancellationToken> work, bool cancellable = true) =>
        RunAsync<object?>(owner, title, (p, ct) => { work(p, ct); return null; }, cancellable);

    public static async Task<T> RunAsync<T>(Window owner, string title, Func<IProgress<OperationProgress>, CancellationToken, T> work, bool cancellable = true)
    {
        var cts = new CancellationTokenSource();
        var sink = new LatestProgress();
        Active.Add(cts);
        JobWindow? window = null;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) => { if (window != null && sink.TryTake(out var p)) window.Apply(p); };
        var task = Task.Run(() => work(sink, cts.Token));
        try
        {
            if (await Task.WhenAny(task, Task.Delay(350)) != task)
            {
                window = new JobWindow(owner, title, cts, cancellable);
                if (sink.TryTake(out var p)) window.Apply(p);
                window.Show();
                timer.Start();
            }
            return await task;
        }
        finally
        {
            timer.Stop();
            Active.Remove(cts);
            window?.Finish();
            cts.Dispose();
        }
    }

    /// <summary>Keeps only the newest report so fast loops never flood the dispatcher.</summary>
    private sealed class LatestProgress : IProgress<OperationProgress>
    {
        private OperationProgress? _latest;
        private readonly object _gate = new();
        public void Report(OperationProgress value) { lock (_gate) _latest = value; }
        public bool TryTake(out OperationProgress value)
        {
            lock (_gate)
            {
                value = _latest ?? default;
                bool had = _latest != null;
                _latest = null;
                return had;
            }
        }
    }
}
