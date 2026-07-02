using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using m_mslc_overlay.core;

namespace m_mslc_overlay.services
{
    /// <summary>
    /// ATOM81 — Priority queue dispatcher for translation tasks.
    /// P0: HardCommit (SDK FINAL) — immediate, not cancellable
    /// P1: SoftCommit/Debounce/OffsetChange with WordCount >= 4 — async
    /// P2: Reserved for Speculative (future ATOM76)
    /// P3: WordCount <= 3 (short fragments) — deferred, cancellable
    /// </summary>
    public class TranslationPriorityQueue : IDisposable
    {
        private record PendingTask(
            CommitMetadata Meta,
            Func<CommitMetadata, CancellationToken, Task> Execute,
            CancellationTokenSource Cts,
            int Priority);

        private readonly object _lock = new();
        private readonly List<PendingTask> _queue = new();
        private Task _runnerTask = Task.CompletedTask;
        private bool _disposed;
        private readonly SemaphoreSlim _signal = new(0, int.MaxValue);

        /// Assign P0-P3 based on CommitMetadata.
        public static int GetPriority(CommitMetadata meta)
        {
            if (string.Equals(meta.Reason, "HardCommit", StringComparison.OrdinalIgnoreCase))
                return 0;
            if (meta.WordCount <= 3)
                return 3;
            return 1; // SoftCommit, DebounceCommit, OffsetChange with >=4 words
        }

        /// Enqueue a translation task. Returns a CancellationTokenSource so caller can cancel P3 tasks.
        public CancellationTokenSource Enqueue(CommitMetadata meta, Func<CommitMetadata, CancellationToken, Task> execute)
        {
            var cts = new CancellationTokenSource();
            int priority = GetPriority(meta);
            var task = new PendingTask(meta, execute, cts, priority);

            lock (_lock)
            {
                if (_disposed) return cts;

                // P0 cancels all P3 tasks currently queued (short fragments no longer needed if a hard commit arrives)
                if (priority == 0)
                {
                    foreach (var pending in _queue)
                    {
                        if (pending.Priority == 3)
                            pending.Cts.Cancel();
                    }
                    _queue.RemoveAll(p => p.Priority == 3);
                }

                // Insert maintaining priority order (lower number = higher priority = earlier in list)
                int insertAt = _queue.Count;
                for (int i = 0; i < _queue.Count; i++)
                {
                    if (priority < _queue[i].Priority)
                    {
                        insertAt = i;
                        break;
                    }
                }
                _queue.Insert(insertAt, task);
            }

            _signal.Release();
            EnsureRunnerActive();
            return cts;
        }

        /// Cancel a specific task by its CancellationTokenSource.
        public void Cancel(CancellationTokenSource cts) => cts.Cancel();

        private void EnsureRunnerActive()
        {
            lock (_lock)
            {
                if (_runnerTask.IsCompleted)
                    _runnerTask = Task.Run(RunAsync);
            }
        }

        private async Task RunAsync()
        {
            while (true)
            {
                await _signal.WaitAsync();

                PendingTask? task;
                lock (_lock)
                {
                    if (_queue.Count == 0) continue;
                    task = _queue[0];
                    _queue.RemoveAt(0);
                }

                if (task.Cts.IsCancellationRequested)
                    continue;

                try
                {
                    await task.Execute(task.Meta, task.Cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Task was cancelled — expected for P3
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[TranslationPriorityQueue] Task error: {ex.Message}");
                }
                finally
                {
                    task.Cts.Dispose();
                }

                lock (_lock)
                {
                    if (_queue.Count == 0) break;
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                foreach (var t in _queue) t.Cts.Cancel();
                _queue.Clear();
            }
        }
    }
}
