using System;
using System.Collections.Generic;
using System.Threading;
using Autodesk.Revit.DB;

namespace ClashControlConnector.Core
{
    /// <summary>
    /// Accumulates DocumentChanged events into sets of modified/added/deleted ElementIds.
    /// Supports two modes:
    ///   - Manual flush only (interval = 0): changes sent on Sync with Central
    ///   - Timed interval: auto-flushes every N seconds when changes exist
    /// Sync with Central always triggers a flush regardless of mode.
    /// </summary>
    public class ChangeDebouncer : IDisposable
    {
        private readonly HashSet<ElementId> _modifiedIds = new HashSet<ElementId>();
        private readonly HashSet<ElementId> _addedIds = new HashSet<ElementId>();
        private readonly HashSet<ElementId> _deletedIds = new HashSet<ElementId>();
        private readonly object _lock = new object();
        private readonly Action<HashSet<ElementId>, HashSet<ElementId>, HashSet<ElementId>> _onFlush;
        private Timer _timer;
        private int _flushing;
        // Set when an explicit flush arrives while another flush is in progress;
        // the in-flight flush drains it before finishing so the request isn't lost.
        private int _flushPending;

        public ChangeDebouncer(
            Action<HashSet<ElementId>, HashSet<ElementId>, HashSet<ElementId>> onFlush)
        {
            _onFlush = onFlush;
        }

        /// <summary>
        /// Set the auto-flush interval. 0 = manual only (sync-triggered).
        /// </summary>
        public void SetInterval(int seconds)
        {
            _timer?.Dispose();
            _timer = null;

            if (seconds > 0)
            {
                int ms = seconds * 1000;
                _timer = new Timer(TimerFlush, null, ms, ms);
            }
        }

        /// <summary>
        /// Accumulate element changes.
        /// </summary>
        public void Add(ICollection<ElementId> modified, ICollection<ElementId> added, ICollection<ElementId> deleted)
        {
            lock (_lock)
            {
                foreach (var id in modified) _modifiedIds.Add(id);
                foreach (var id in added) _addedIds.Add(id);
                foreach (var id in deleted)
                {
                    _deletedIds.Add(id);
                    _addedIds.Remove(id);
                    _modifiedIds.Remove(id);
                }
            }
        }

        public bool HasChanges
        {
            get
            {
                lock (_lock)
                {
                    return _modifiedIds.Count > 0 || _addedIds.Count > 0 || _deletedIds.Count > 0;
                }
            }
        }

        /// <summary>
        /// Flush all accumulated changes to the callback and clear the buffers.
        /// Concurrent flushes are serialized: when a flush is already in progress
        /// (e.g. the timer fired), an explicit flush is not dropped — it sets a
        /// pending flag that the in-flight flush drains before returning.
        /// </summary>
        public void Flush()
        {
            FlushInternal(explicitRequest: true);
        }

        private void FlushInternal(bool explicitRequest)
        {
            if (Interlocked.CompareExchange(ref _flushing, 1, 0) != 0)
            {
                // Another flush is running. Explicit (sync-triggered) requests must
                // not be silently lost — ask the in-flight flush to run once more.
                if (explicitRequest) Interlocked.Exchange(ref _flushPending, 1);
                return;
            }

            try
            {
                do
                {
                    FlushOnce();
                }
                while (Interlocked.Exchange(ref _flushPending, 0) == 1);
            }
            finally
            {
                Interlocked.Exchange(ref _flushing, 0);
            }
        }

        private void FlushOnce()
        {
            HashSet<ElementId> modified, added, deleted;
            lock (_lock)
            {
                if (_modifiedIds.Count == 0 && _addedIds.Count == 0 && _deletedIds.Count == 0)
                    return;

                modified = new HashSet<ElementId>(_modifiedIds);
                added = new HashSet<ElementId>(_addedIds);
                deleted = new HashSet<ElementId>(_deletedIds);

                _modifiedIds.Clear();
                _addedIds.Clear();
                _deletedIds.Clear();
            }

            _onFlush(modified, added, deleted);
        }

        /// <summary>
        /// Discard all accumulated changes without flushing.
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _modifiedIds.Clear();
                _addedIds.Clear();
                _deletedIds.Clear();
            }
        }

        private void TimerFlush(object state)
        {
            if (HasChanges) FlushInternal(explicitRequest: false);
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}
