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
        /// Guard prevents concurrent flushes from timer and sync events.
        /// </summary>
        public void Flush()
        {
            if (Interlocked.CompareExchange(ref _flushing, 1, 0) != 0)
                return;

            try
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
            finally
            {
                Interlocked.Exchange(ref _flushing, 0);
            }
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
            if (HasChanges) Flush();
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}
