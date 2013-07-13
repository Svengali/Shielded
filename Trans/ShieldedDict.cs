using System;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Trans
{
    /// <summary>
    /// Protects only regarding adds or removes. Updates are unprotected unless
    /// you choose to have shielded items.
    /// </summary>
    public class ShieldedDict<TKey, TItem> : IShielded
    {
        private class ItemKeeper
        {
            public long Version;
            public TItem Value;
            public ItemKeeper Older;
        }

        private class LocalDict
        {
            public Dictionary<TKey, TItem> Items;
            public HashSet<TKey> Reads = new HashSet<TKey>();
        }

        private readonly ConcurrentDictionary<TKey, ItemKeeper> _dict;
        private readonly ConcurrentDictionary<TKey, long> _writeStamps
            = new ConcurrentDictionary<TKey, long>();
        private ThreadLocal<LocalDict> _localDict = new ThreadLocal<LocalDict>();

        public ShieldedDict(Func<TItem, TKey> keySelector, IEnumerable<TItem> items)
        {
            _dict = new ConcurrentDictionary<TKey, ItemKeeper>(
                items.Select(i =>
                    new KeyValuePair<TKey, ItemKeeper>(
                        keySelector(i),
                        new ItemKeeper()
                        {
                            Version = 0,
                            Value = i
                        })));
        }

        public ShieldedDict()
        {
            _dict = new ConcurrentDictionary<TKey, ItemKeeper>();
        }

        private void CheckLockAndEnlist(TKey key)
        {
            var stamp = Shield.CurrentTransactionStartStamp;
            SpinWait.SpinUntil(() =>
            {
                long w;
                return !_writeStamps.TryGetValue(key, out w) || w > stamp;
            });
            Shield.Enlist(this);
        }

        private ItemKeeper CurrentTransactionOldValue(TKey key)
        {
            PrepareLocal(key);
            _localDict.Value.Reads.Add(key);

            ItemKeeper point;
            _dict.TryGetValue(key, out point);
            while (point != null && point.Version > Shield.CurrentTransactionStartStamp)
                point = point.Older;
            return point;
        }

        private bool IsLocalPrepared()
        {
            return _localDict.IsValueCreated && _localDict.Value.Reads != null;
        }

        private void PrepareLocal(TKey key)
        {
            CheckLockAndEnlist(key);
            if (!IsLocalPrepared())
            {
                if (!_localDict.IsValueCreated || _localDict.Value == null)
                    _localDict.Value = new LocalDict();
                else
                {
                    if (_localDict.Value.Reads == null)
                        _localDict.Value.Reads = new HashSet<TKey>();
                }
            }
        }

        /// <summary>
        /// Never throws, just returns null if it does not have anything.
        /// </summary>
        public TItem this [TKey key]
        {
            get
            {
                ItemKeeper v;
                if (!Shield.IsInTransaction || !IsLocalPrepared() ||
                    _localDict.Value.Items == null || !_localDict.Value.Items.ContainsKey(key))
                {
                    if (Shield.IsInTransaction)
                        v = CurrentTransactionOldValue(key);
                    else if (!_dict.TryGetValue(key, out v))
                        return default(TItem);
                    return v == null ? default(TItem) : v.Value;
                }
                else if (_dict.TryGetValue(key, out v) && v.Version > Shield.CurrentTransactionStartStamp)
                    throw new TransException("Writable read collision.");
                return _localDict.Value.Items[key];
            }
            set
            {
                PrepareLocal(key);
                if (_localDict.Value.Items == null)
                    _localDict.Value.Items = new Dictionary<TKey, TItem>();
                _localDict.Value.Items[key] = value;
            }
        }

        bool IShielded.HasChanges
        {
            get
            {
                return IsLocalPrepared() && _localDict.Value.Items != null && _localDict.Value.Items.Any();
            }
        }

        bool IShielded.CanCommit(long writeStamp)
        {
            // locals were prepared when we enlisted.
            if (_localDict.Value.Reads.Any(key =>
                    {
                        ItemKeeper v;
                        return _writeStamps.ContainsKey(key) ||
                            (_dict.TryGetValue(key, out v) && v.Version > Shield.CurrentTransactionStartStamp);
                    }))
                return false;
            else
            {
                // touch only the ones we plan to change
                if (_localDict.Value.Items != null)
                    foreach (var key in _localDict.Value.Items.Keys)
                    {
                        if (!_writeStamps.TryAdd(key, writeStamp))
                            throw new ApplicationException("Another transaction already has write lock on this key!");
                    }
                return true;
            }
        }

        private ConcurrentDictionary<TKey, long> _copies = new ConcurrentDictionary<TKey, long>();

        bool IShielded.Commit(long? writeStamp)
        {
            if (((IShielded)this).HasChanges)
            {
                foreach (var kvp in _localDict.Value.Items)
                {
                    ItemKeeper v = null;
                    _dict.TryGetValue(kvp.Key, out v);
                    var newCurrent = new ItemKeeper()
                    {
                        Value = kvp.Value,
                        Version = writeStamp.Value,
                        Older = v
                    };
                    // nobody is actually changing it now.
                    _dict[kvp.Key] = newCurrent;
                    _copies[kvp.Key] = writeStamp.Value;

                    long ourStamp;
                    if (!_writeStamps.TryRemove(kvp.Key, out ourStamp) || ourStamp != writeStamp)
                        throw new ApplicationException("Commit from unexpected transaction");
                }
                _localDict.Value.Items = null;
                _localDict.Value.Reads = null;
                return true;
            }
            if (IsLocalPrepared())
                _localDict.Value.Reads = null;
            return false;
        }

        void IShielded.Rollback(long? writeStamp)
        {
            _localDict.Value.Reads = null;
            if (_localDict.Value.Items != null)
            {
                if (writeStamp.HasValue)
                {
                    long ws;
                    foreach (var key in _localDict.Value.Items.Keys)
                        if (_writeStamps.TryGetValue(key, out ws) && ws == writeStamp.Value)
                            _writeStamps.TryRemove(key, out ws);
                }
                _localDict.Value.Items = null;
            }
        }

        void IShielded.TrimCopies(long smallestOpenTransactionId)
        {
            // NB the "smallest transaction" and others can freely read while
            // we're doing this.
            var keys = _copies.Keys;
            foreach (var key in keys)
            {
                if (_copies[key] > smallestOpenTransactionId)
                    continue;

//                if (!_dict.ContainsKey(key))
//                    continue;
                var point = _dict[key];
                while (point != null && point.Version > smallestOpenTransactionId)
                {
                    point = point.Older;
                }
                if (point != null)
                {
                    // point is the last accessible - his Older is not needed.
                    point.Older = null;
                }
                long version;
                _copies.TryRemove(key, out version);
                if (version > smallestOpenTransactionId)
                    _copies.TryAdd(key, version);
            }
        }
    }
}