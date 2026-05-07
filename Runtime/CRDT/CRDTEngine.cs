using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ART.ADK.CRDT
{
    /// <summary>
    /// CRDT Engine implementing conflict-free replicated data types with
    /// map/object and RGA array support, operation compaction, and flush debouncing.
    /// </summary>
    public class CRDTEngine
    {
        internal LDMap Snapshot;
        private readonly Dictionary<string, List<Action<object>>> _listeners = new Dictionary<string, List<Action<object>>>();
        private Action<List<CRDTOperation>> _mergeCallback;
        internal List<CRDTOperation> Pending = new List<CRDTOperation>();
        internal string ClientReplicaId = "client";

        private double _lastFlushAt;
        private const double MinFlushMs = 50;
        private CancellationTokenSource _trailingTimerCts;
        private readonly object _queueLock = new object();

        public CRDTEngine(LDMap initial, Action<List<CRDTOperation>> mergeCallback)
        {
            Snapshot = initial;
            _mergeCallback = mergeCallback;
        }

        public CRDTProxy State()
        {
            return new CRDTProxy(this, new string[0]);
        }

        public void SetMergeCallback(Action<List<CRDTOperation>> cb) => _mergeCallback = cb;
        public void SetReplicaId(string id) => ClientReplicaId = id;
        public string GetReplicaId() => ClientReplicaId;
        public LDMap GetState() => Snapshot;

        // ---- Pending Ops ----
        internal void AppendPending(CRDTOperation op)
        {
            lock (_queueLock) { Pending.Add(op); }
        }

        internal void AppendPending(List<CRDTOperation> ops)
        {
            lock (_queueLock) { Pending.AddRange(ops); }
        }

        // ---- Flush ----
        public async Task Flush()
        {
            List<CRDTOperation> ops;
            lock (_queueLock)
            {
                if (Pending.Count == 0) return;
                ops = CompactOps(Pending);
                Pending.Clear();
            }
            if (ops.Count == 0) return;
            Merge(ops);
            _mergeCallback?.Invoke(ops);
        }

        internal void ScheduleFlush()
        {
            lock (_queueLock)
            {
                if (_trailingTimerCts != null) return;
                _trailingTimerCts = new CancellationTokenSource();
                var cts = _trailingTimerCts;

                _ = Task.Run(async () =>
                {
                    await Task.Delay((int)MinFlushMs);
                    lock (_queueLock) { _trailingTimerCts = null; }
                    if (!cts.IsCancellationRequested)
                        await Flush();
                });
            }
        }

        // ---- Navigation ----
        internal LDValue GetContainerAt(string[] path)
        {
            LDValue node = LDValue.FromMap(Snapshot);
            foreach (var seg in path)
            {
                if (node.Type == LDValueType.Map)
                {
                    if (!node.MapValue.Index.TryGetValue(seg, out var e)) return null;
                    node = e.Value;
                }
                else if (node.Type == LDValueType.Array)
                {
                    if (!node.ArrayValue.Entries.TryGetValue(seg, out var e)) return null;
                    node = e.Value;
                }
                else return null;
            }
            if (node.Type == LDValueType.Map || node.Type == LDValueType.Array)
                return node;
            return null;
        }

        internal object ReadJSONAt(string[] path)
        {
            LDValue node = LDValue.FromMap(Snapshot);
            foreach (var seg in path)
            {
                if (node.Type == LDValueType.Map)
                {
                    if (!node.MapValue.Index.TryGetValue(seg, out var e)) return null;
                    node = e.Value;
                }
                else if (node.Type == LDValueType.Array)
                {
                    if (!node.ArrayValue.Entries.TryGetValue(seg, out var e)) return null;
                    node = e.Value;
                }
                else return null;
            }
            return CRDTUtils.ToAny(node);
        }

        private LDValue Navigate(string[] path)
        {
            LDValue node = LDValue.FromMap(Snapshot);
            foreach (var seg in path)
            {
                if (node.Type == LDValueType.Map)
                {
                    if (!node.MapValue.Index.TryGetValue(seg, out var e))
                        throw new ARTInvalidPathException(string.Join(".", path));
                    node = e.Value;
                }
                else if (node.Type == LDValueType.Array)
                {
                    if (!node.ArrayValue.Entries.TryGetValue(seg, out var e))
                        throw new ARTInvalidPathException(string.Join(".", path));
                    node = e.Value;
                }
                else throw new ARTInvalidPathException($"Cannot navigate into primitive at {seg}");
            }
            return node;
        }

        private LDContainer NavigateToParent(string[] path, bool forceCreate)
        {
            LDContainer node = LDContainer.FromMap(Snapshot);
            for (int i = 0; i < path.Length - 1; i++)
            {
                var seg = path[i];
                if (node.ContainerType == LDContainerType.Map)
                {
                    var m = node.MapValue;
                    if (!m.Index.ContainsKey(seg))
                    {
                        if (!forceCreate) throw new ARTInvalidPathException(string.Join(".", path));
                        var ldVal = LDValue.FromMap(new LDMap());
                        m.Index[seg] = new LDEntry(CRDTUtils.GenerateId(), seg, LDEntryType.Object, ldVal,
                            new LDMeta((int)CRDTUtils.NowMs(), 1, ClientReplicaId));
                    }
                    var e = m.Index[seg];
                    node = CRDTUtils.ToContainer(e.Value);
                }
                else if (node.ContainerType == LDContainerType.Array)
                {
                    var a = node.ArrayValue;
                    if (!a.Entries.ContainsKey(seg))
                    {
                        if (!forceCreate) throw new ARTInvalidPathException(string.Join(".", path));
                        var meta = new LDMeta((int)CRDTUtils.NowMs(), 1, ClientReplicaId);
                        meta.AfterIsSet = true;
                        a.Entries[seg] = new LDEntry(seg, seg, LDEntryType.Object, LDValue.FromMap(new LDMap()), meta);
                    }
                    node = CRDTUtils.ToContainer(a.Entries[seg].Value);
                }
            }
            return node;
        }

        private void EnsureMapParents(LDMap root, string[] path, long ts, string replicaId)
        {
            LDContainer node = LDContainer.FromMap(root);
            for (int i = 0; i < path.Length - 1; i++)
            {
                var seg = path[i];
                if (node.ContainerType == LDContainerType.Map)
                {
                    var m = node.MapValue;
                    if (!m.Index.ContainsKey(seg))
                        m.Index[seg] = new LDEntry(CRDTUtils.GenerateId(), seg, LDEntryType.Object,
                            LDValue.FromMap(new LDMap()), new LDMeta(ts, 1, replicaId));

                    var e = m.Index[seg];
                    if (e.Value.Type == LDValueType.Map)
                        node = LDContainer.FromMap(e.Value.MapValue);
                    else if (e.Value.Type == LDValueType.Array)
                        node = LDContainer.FromArray(e.Value.ArrayValue);
                }
                else if (node.ContainerType == LDContainerType.Array)
                {
                    var a = node.ArrayValue;
                    if (!a.Entries.ContainsKey(seg))
                    {
                        var meta = new LDMeta(ts, 1, replicaId);
                        meta.AfterIsSet = true;
                        a.Entries[seg] = new LDEntry(seg, seg, LDEntryType.Object, LDValue.FromMap(new LDMap()), meta);
                    }
                    if (a.Entries[seg].Value.Type == LDValueType.Map)
                        node = LDContainer.FromMap(a.Entries[seg].Value.MapValue);
                }
            }
        }

        // ---- Array helpers ----
        internal LDArray EnsureArrayContainer(string[] path)
        {
            var existing = GetContainerAt(path);
            if (existing != null && existing.Type == LDValueType.Array)
                return existing.ArrayValue;

            var newArr = new LDArray { Meta = CRDTUtils.DefaultMeta(ClientReplicaId) };
            var key = path.Length > 0 ? path[path.Length - 1] : "";
            var entry = new LDEntry(CRDTUtils.GenerateId(), key, LDEntryType.Array,
                LDValue.FromArray(newArr), new LDMeta((int)CRDTUtils.NowMs(), 1, ClientReplicaId));

            if (path.Length <= 1)
            {
                Snapshot.Index[key] = entry;
            }
            else
            {
                var parentPath = path.Take(path.Length - 1).ToArray();
                var parent = GetContainerAt(parentPath);
                if (parent?.Type == LDValueType.Map)
                    parent.MapValue.Index[key] = entry;
            }
            return newArr;
        }

        internal List<string> VisibleIdsFor(string[] path)
        {
            var ids = BaseIdsFor(path);
            List<CRDTOperation> pendingOps;
            var key = string.Join(".", path);
            lock (_queueLock)
            {
                pendingOps = Pending.Where(op =>
                {
                    if (op.OpType == CRDTOpType.ArrayPush || op.OpType == CRDTOpType.ArrayUnshift || op.OpType == CRDTOpType.ArrayRemove)
                        return string.Join(".", op.Path) == key;
                    return false;
                }).ToList();
            }

            foreach (var op in pendingOps)
            {
                switch (op.OpType)
                {
                    case CRDTOpType.ArrayPush:
                        var pos = op.Ref != null ? ids.IndexOf(op.Ref) + 1 : ids.Count;
                        if (pos < 0) pos = ids.Count;
                        ids.Insert(pos, op.Entry.Id);
                        break;
                    case CRDTOpType.ArrayUnshift:
                        ids.Insert(0, op.Entry.Id);
                        break;
                    case CRDTOpType.ArrayRemove:
                        ids.Remove(op.Ref);
                        break;
                }
            }
            return ids;
        }

        private List<string> BaseIdsFor(string[] path)
        {
            var cont = GetContainerAt(path);
            if (cont?.Type != LDValueType.Array) return new List<string>();
            return CRDTUtils.LinearizeRGA(cont.ArrayValue);
        }

        internal string GetArrayIdAt(string[] path, int idx)
        {
            var ids = VisibleIdsFor(path);
            var n = ids.Count;
            var i = idx < 0 ? n + idx : idx;
            if (i < 0 || i >= n) return null;
            return ids[i];
        }

        // ---- EnsureParentsOps ----
        internal List<CRDTOperation> EnsureParentsOps(string[] full)
        {
            var ops = new List<CRDTOperation>();
            for (int i = 0; i < full.Length - 1; i++)
            {
                var sub = full.Take(i + 1).ToArray();
                if (ReadJSONAt(sub) != null) continue;

                var parentPath = full.Take(i).ToArray();
                var parentCont = GetContainerAt(parentPath);
                if (parentCont?.Type == LDValueType.Array) continue;

                var entry = new LDEntry(CRDTUtils.GenerateId(), full[i], LDEntryType.Object,
                    LDValue.FromMap(new LDMap()), new LDMeta((long)CRDTUtils.NowMs(), 1, ClientReplicaId));
                ops.Add(CRDTOperation.Add(sub, entry, (long)CRDTUtils.NowMs(), ClientReplicaId));
            }
            return ops;
        }

        // ---- Op Compaction ----
        private List<CRDTOperation> CompactOps(List<CRDTOperation> batch)
        {
            var parentAddsSeen = new HashSet<string>();
            var parentAdds = new List<CRDTOperation>();
            var arrayOps = new List<CRDTOperation>();
            var leafMap = new Dictionary<string, (bool isRemove, LDEntry entry)>();

            foreach (var op in batch)
            {
                if (op.OpType == CRDTOpType.ArrayPush || op.OpType == CRDTOpType.ArrayUnshift || op.OpType == CRDTOpType.ArrayRemove)
                {
                    arrayOps.Add(op);
                    continue;
                }

                if (op.OpType == CRDTOpType.Add && op.Entry != null)
                {
                    if (op.Entry.EntryType == LDEntryType.Object || op.Entry.EntryType == LDEntryType.Array)
                    {
                        var k = string.Join(".", op.Path);
                        if (parentAddsSeen.Add(k))
                            parentAdds.Add(op);
                        continue;
                    }
                }

                var key = string.Join(".", op.Path);
                if (op.OpType == CRDTOpType.Remove)
                    leafMap[key] = (true, null);
                else if (op.Entry != null)
                    leafMap[key] = (false, op.Entry);
            }

            parentAdds.Sort((a, b) => a.Path.Length.CompareTo(b.Path.Length));
            var leaves = new List<CRDTOperation>();
            foreach (var kv in leafMap)
            {
                var path = kv.Key.Split('.');
                if (kv.Value.isRemove)
                    leaves.Add(CRDTOperation.Remove(path, (long)CRDTUtils.NowMs(), ClientReplicaId));
                else
                    leaves.Add(CRDTOperation.Replace(path, kv.Value.entry, (long)CRDTUtils.NowMs(), ClientReplicaId));
            }

            var result = new List<CRDTOperation>();
            result.AddRange(parentAdds);
            result.AddRange(leaves);
            result.AddRange(arrayOps);
            return result;
        }

        // ---- Merge ----
        public void Merge(List<CRDTOperation> ops)
        {
            foreach (var op in ops)
            {
                switch (op.OpType)
                {
                    case CRDTOpType.ArrayPush:
                    {
                        var arr = EnsureArrayContainer(op.Path);
                        op.Entry.Meta.UpdatedAt = op.Timestamp;
                        op.Entry.Meta.ReplicaId = op.ReplicaId;
                        op.Entry.Meta.After = op.Ref;
                        op.Entry.Meta.AfterIsSet = true;
                        arr.Entries[op.Entry.Id] = op.Entry;
                        break;
                    }
                    case CRDTOpType.ArrayUnshift:
                    {
                        var arr = EnsureArrayContainer(op.Path);
                        op.Entry.Meta.UpdatedAt = op.Timestamp;
                        op.Entry.Meta.ReplicaId = op.ReplicaId;
                        op.Entry.Meta.After = null;
                        op.Entry.Meta.AfterIsSet = true;
                        arr.Entries[op.Entry.Id] = op.Entry;
                        break;
                    }
                    case CRDTOpType.ArrayRemove:
                    {
                        var arr = EnsureArrayContainer(op.Path);
                        if (arr.Entries.TryGetValue(op.Ref, out var target))
                        {
                            if (target.Meta.Tombstone != true || target.Meta.UpdatedAt <= op.Timestamp)
                            {
                                target.Meta.Tombstone = true;
                                target.Meta.UpdatedAt = op.Timestamp;
                                target.Meta.ReplicaId = op.ReplicaId;
                            }
                        }
                        break;
                    }
                    case CRDTOpType.Remove:
                    {
                        var key = op.Path.Last();
                        if (op.Path.Length == 1)
                        {
                            Snapshot.Index.Remove(key);
                        }
                        else
                        {
                            try
                            {
                                var parent = NavigateToParent(op.Path, false);
                                if (parent.ContainerType == LDContainerType.Map)
                                    parent.MapValue.Index.Remove(key);
                                else
                                    parent.ArrayValue.Entries.Remove(key);
                            }
                            catch { }
                        }
                        break;
                    }
                    case CRDTOpType.Add:
                    {
                        if (op.Entry == null) continue;
                        EnsureMapParents(Snapshot, op.Path, op.Timestamp, op.ReplicaId);
                        var key = op.Path.Last();
                        try
                        {
                            var parent = NavigateToParent(op.Path, true);
                            if (parent.ContainerType == LDContainerType.Map)
                                parent.MapValue.Index[key] = op.Entry;
                            else
                                parent.ArrayValue.Entries[key] = op.Entry;
                        }
                        catch { }
                        break;
                    }
                    case CRDTOpType.Replace:
                    {
                        EnsureMapParents(Snapshot, op.Path, op.Timestamp, op.ReplicaId);
                        var key = op.Path.Last();
                        try
                        {
                            var parent = NavigateToParent(op.Path, true);
                            if (parent.ContainerType == LDContainerType.Map)
                                parent.MapValue.Index[key] = op.Entry;
                            else
                                parent.ArrayValue.Entries[key] = op.Entry;
                        }
                        catch
                        {
                            // Array element upsert fallback
                            if (op.Path.Length >= 3)
                            {
                                var arrayPath = op.Path.Take(op.Path.Length - 2).ToArray();
                                var elemId = op.Path[op.Path.Length - 2];
                                var cont = GetContainerAt(arrayPath);
                                if (cont?.Type == LDValueType.Array)
                                {
                                    var a = cont.ArrayValue;
                                    if (!a.Entries.ContainsKey(elemId))
                                    {
                                        var meta = new LDMeta(op.Timestamp, 1, op.ReplicaId);
                                        meta.AfterIsSet = true;
                                        a.Entries[elemId] = new LDEntry(elemId, elemId, LDEntryType.Object,
                                            LDValue.FromMap(new LDMap()), meta);
                                    }
                                    try
                                    {
                                        var p = NavigateToParent(op.Path, false);
                                        if (p.ContainerType == LDContainerType.Map)
                                            p.MapValue.Index[key] = op.Entry;
                                        else
                                            p.ArrayValue.Entries[key] = op.Entry;
                                    }
                                    catch { }
                                }
                            }
                        }
                        break;
                    }
                }
            }

            // Notify listeners
            var affectedPaths = new HashSet<string>(ops.Select(o => string.Join(".", o.Path)));
            foreach (var kv in _listeners.ToList())
            {
                if (affectedPaths.Any(p => p.StartsWith(kv.Key)))
                {
                    object json;
                    try
                    {
                        var segments = string.IsNullOrEmpty(kv.Key) ? new string[0] : kv.Key.Split('.');
                        json = CRDTUtils.ToAny(Navigate(segments));
                    }
                    catch { json = null; }

                    foreach (var cb in kv.Value) cb(json);
                }
            }
        }

        // ---- Query ----
        public (Func<Task<object>> execute, Func<Action<object>, Action> listen) Query(string path = null)
        {
            var segments = string.IsNullOrEmpty(path) || path == "index"
                ? new string[0]
                : path.Split('.');

            Func<Task<object>> execute = async () =>
            {
                try { return CRDTUtils.ToAny(Navigate(segments)); }
                catch { return null; }
            };

            Func<Action<object>, Action> listen = cb =>
            {
                var key = path ?? "";
                lock (_queueLock)
                {
                    if (!_listeners.ContainsKey(key))
                        _listeners[key] = new List<Action<object>>();
                    _listeners[key].Add(cb);
                }
                try { cb(CRDTUtils.ToAny(Navigate(segments))); }
                catch { cb(null); }

                return () =>
                {
                    lock (_queueLock)
                    {
                        if (_listeners.ContainsKey(key))
                            _listeners[key].Remove(cb);
                    }
                };
            };

            return (execute, listen);
        }
    }
}
