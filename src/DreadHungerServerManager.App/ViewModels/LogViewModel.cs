using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using DreadHungerServerManager.Core.Models;

namespace DreadHungerServerManager.App.ViewModels;

public sealed class LogLineViewModel(ServerLogLine line)
{
    public string Text { get; } = line.Text;

    public LogLineKind Kind { get; } = line.Kind;

    public override string ToString() => Text;
}

/// <summary>
/// サーバーログの表示。行は任意のスレッドから届くため一旦キューに溜め、
/// 100ミリ秒ごとにまとめてUIスレッドで反映する（起動時の大量出力で画面が固まらないように）。
/// </summary>
public sealed class LogViewModel(SynchronizationContext uiContext) : ObservableObject
{
    private static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(100);

    private readonly ConcurrentQueue<ServerLogLine> _pending = new();
    private int _flushScheduled;
    private bool _autoScroll = true;
    private int _maxLines = 5000;

    public event EventHandler? LinesAppended;

    public event EventHandler? SettingsChanged;

    public ObservableCollection<LogLineViewModel> Lines { get; } = [];

    public bool AutoScroll
    {
        get => _autoScroll;
        set
        {
            if (SetProperty(ref _autoScroll, value))
            {
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public int MaxLines
    {
        get => _maxLines;
        set
        {
            if (SetProperty(ref _maxLines, value))
            {
                Trim();
                OnPropertyChanged(nameof(SummaryText));
            }
        }
    }

    public string SummaryText => $"{Lines.Count:N0} 行（最新 {MaxLines:N0} 行まで表示）";

    public void Enqueue(IEnumerable<ServerLogLine> lines)
    {
        foreach (var line in lines)
        {
            _pending.Enqueue(line);
        }

        if (Interlocked.Exchange(ref _flushScheduled, 1) == 0)
        {
            _ = FlushSoonAsync();
        }
    }

    public void Clear()
    {
        Lines.Clear();
        OnPropertyChanged(nameof(SummaryText));
    }

    public static string Format(IEnumerable<LogLineViewModel> lines) =>
        string.Join(Environment.NewLine, lines.Select(line => line.Text));

    private async Task FlushSoonAsync()
    {
        await Task.Delay(FlushDelay).ConfigureAwait(false);
        uiContext.Post(_ => Flush(), null);
    }

    private void Flush()
    {
        Volatile.Write(ref _flushScheduled, 0);
        var appended = false;
        while (_pending.TryDequeue(out var line))
        {
            Lines.Add(new LogLineViewModel(line));
            appended = true;
        }

        if (!appended)
        {
            return;
        }

        Trim();
        OnPropertyChanged(nameof(SummaryText));
        LinesAppended?.Invoke(this, EventArgs.Empty);
    }

    private void Trim()
    {
        while (Lines.Count > _maxLines)
        {
            Lines.RemoveAt(0);
        }
    }
}
