using System.Diagnostics;

namespace FanslationStudio.LlmKit.Utility;

/// <summary>
/// Per-file write-back bookkeeping shared by the pooled translation scheduler and the QC pass:
/// workers report changed items and finished items, and the file is re-serialized to disk only
/// when it has actually changed - periodically (throttled by both an item count and a minimum
/// interval, so a large file isn't re-serialized every few items) and once more when the file's
/// last pending item finishes.
/// </summary>
internal sealed class BufferedFileWriter
{
    private readonly object _lock = new();
    private readonly string _outputFile;
    private readonly Func<string> _serialize;
    private readonly Func<bool>? _beforeFlush;
    private readonly Action<long>? _onWrite;
    private readonly int _bufferThreshold;
    private readonly TimeSpan _minFlushInterval;
    private readonly Stopwatch _sinceLastWrite = Stopwatch.StartNew();

    private int _bufferedChanges;
    private int _pendingItems;
    private int _dirty;

    /// <param name="serialize">Produces the file content; always invoked under this writer's lock.</param>
    /// <param name="beforeFlush">Optional pass run under the lock before every flush (e.g. duplicate
    /// propagation); returns true when it changed anything, which marks the file dirty.</param>
    /// <param name="onWrite">Optional callback with the elapsed milliseconds of each write.</param>
    public BufferedFileWriter(string outputFile, Func<string> serialize, int bufferThreshold,
        TimeSpan? minFlushInterval = null, Func<bool>? beforeFlush = null, Action<long>? onWrite = null)
    {
        _outputFile = outputFile;
        _serialize = serialize;
        _bufferThreshold = bufferThreshold;
        _minFlushInterval = minFlushInterval ?? DefaultMinFlushInterval;
        _beforeFlush = beforeFlush;
        _onWrite = onWrite;
    }

    public static TimeSpan DefaultMinFlushInterval { get; set; } = TimeSpan.FromSeconds(10);

    public string OutputFile => _outputFile;

    public bool IsDirty => Volatile.Read(ref _dirty) == 1;

    public int PendingItems => Volatile.Read(ref _pendingItems);

    public int WriteCount { get; private set; }

    /// <summary>Set once, before dispatch, to the number of work items this run will hand out for the file.</summary>
    public void SetPendingItems(int count) => Volatile.Write(ref _pendingItems, count);

    /// <summary>
    /// Records one changed item. Flushes once more than the buffer threshold has accumulated and the
    /// minimum interval has elapsed - without blocking: if another worker is already flushing this
    /// file, the caller returns immediately instead of waiting on the lock.
    /// </summary>
    public void RecordChange()
    {
        Volatile.Write(ref _dirty, 1);

        if (Interlocked.Increment(ref _bufferedChanges) <= _bufferThreshold)
            return;

        if (!Monitor.TryEnter(_lock))
            return;

        try
        {
            if (_bufferedChanges > _bufferThreshold && _sinceLastWrite.Elapsed >= _minFlushInterval)
                FlushLocked();
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    /// <summary>Marks one dispatched item finished; flushes when it was the file's last pending item.</summary>
    public void ItemDone()
    {
        if (Interlocked.Decrement(ref _pendingItems) == 0)
            Flush();
    }

    /// <summary>Runs the before-flush pass and writes the file if anything changed since the last write.</summary>
    public void Flush()
    {
        lock (_lock)
            FlushLocked();
    }

    private void FlushLocked()
    {
        if (_beforeFlush?.Invoke() == true)
            Volatile.Write(ref _dirty, 1);

        if (Interlocked.Exchange(ref _dirty, 0) == 0)
            return;

        var stopwatch = Stopwatch.StartNew();
        FileHelper.WriteAllTextWithRetry(_outputFile, _serialize());
        _onWrite?.Invoke(stopwatch.ElapsedMilliseconds);

        Interlocked.Exchange(ref _bufferedChanges, 0);
        _sinceLastWrite.Restart();
        WriteCount++;
    }
}
