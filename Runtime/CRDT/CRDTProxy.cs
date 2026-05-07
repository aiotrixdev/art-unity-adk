using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ART.ADK.CRDT
{
    /// <summary>
    /// Proxy object for interacting with CRDT state using path-based access.
    /// Mirrors the Swift @dynamicMemberLookup CRDTProxy.
    /// </summary>
    public class CRDTProxy
    {
        internal readonly CRDTEngine Engine;
        internal readonly string[] ParentPath;

        internal CRDTProxy(CRDTEngine engine, string[] parentPath)
        {
            Engine = engine;
            ParentPath = parentPath;
        }

        /// <summary>Access a nested property by key.</summary>
        public CRDTProxy this[string key] => new CRDTProxy(Engine, ParentPath.Append(key).ToArray());

        /// <summary>Access an array element by index.</summary>
        public CRDTProxy this[int index]
        {
            get
            {
                var id = Engine.GetArrayIdAt(ParentPath, index);
                if (id == null)
                    return new CRDTProxy(Engine, ParentPath.Append($"__oob_{index}").ToArray());
                return new CRDTProxy(Engine, ParentPath.Append(id).ToArray());
            }
        }

        /// <summary>Read the current value at this path.</summary>
        public object Value => Engine.ReadJSONAt(ParentPath);

        /// <summary>Set a value at this path.</summary>
        public void Set(object value)
        {
            var key = ParentPath.Length > 0 ? ParentPath.Last() : "";
            var parents = Engine.EnsureParentsOps(ParentPath);

            var ldVal = CRDTUtils.ToLDValue(value);

            void PatchReplica(LDValue v)
            {
                if (v.Type == LDValueType.Map)
                {
                    foreach (var e in v.MapValue.Index.Values)
                    {
                        e.Meta.ReplicaId = Engine.ClientReplicaId;
                        PatchReplica(e.Value);
                    }
                }
                else if (v.Type == LDValueType.Array)
                {
                    foreach (var e in v.ArrayValue.Entries.Values)
                    {
                        e.Meta.ReplicaId = Engine.ClientReplicaId;
                        PatchReplica(e.Value);
                    }
                }
            }

            PatchReplica(ldVal);

            var entry = new LDEntry(
                CRDTUtils.GenerateId(), key, CRDTUtils.DetermineType(ldVal), ldVal,
                new LDMeta((int)CRDTUtils.NowMs(), 1, Engine.ClientReplicaId));

            Engine.AppendPending(parents);
            Engine.AppendPending(CRDTOperation.Replace(ParentPath, entry, (int)CRDTUtils.NowMs(), Engine.ClientReplicaId));
            Engine.ScheduleFlush();
        }

        /// <summary>Delete the value at this path.</summary>
        public void Delete()
        {
            if (Engine.ReadJSONAt(ParentPath) == null) return;
            Engine.AppendPending(CRDTOperation.Remove(ParentPath, (int)CRDTUtils.NowMs(), Engine.ClientReplicaId));
            Engine.ScheduleFlush();
        }

        /// <summary>Push items to the end of an array at this path.</summary>
        public int Push(params object[] items)
        {
            Engine.EnsureArrayContainer(ParentPath);
            var cur = Engine.VisibleIdsFor(ParentPath);
            string prev = cur.Count > 0 ? cur[cur.Count - 1] : null;

            foreach (var item in items)
            {
                var id = CRDTUtils.GenerateId();
                var ldVal = CRDTUtils.ToLDValue(item);
                var meta = new LDMeta((int)CRDTUtils.NowMs(), 1, Engine.ClientReplicaId);
                meta.After = prev;
                meta.AfterIsSet = true;
                var entry = new LDEntry(id, id, CRDTUtils.DetermineType(ldVal), ldVal, meta);
                Engine.AppendPending(CRDTOperation.ArrayPush(ParentPath, prev, entry, meta.UpdatedAt, Engine.ClientReplicaId));
                cur.Add(id);
                prev = id;
            }
            Engine.ScheduleFlush();
            return cur.Count;
        }

        /// <summary>Insert items at the beginning of an array.</summary>
        public int Unshift(params object[] items)
        {
            Engine.EnsureArrayContainer(ParentPath);
            foreach (var item in items)
            {
                var id = CRDTUtils.GenerateId();
                var ldVal = CRDTUtils.ToLDValue(item);
                var meta = new LDMeta((int)CRDTUtils.NowMs(), 1, Engine.ClientReplicaId);
                meta.After = null;
                meta.AfterIsSet = true;
                var entry = new LDEntry(id, id, CRDTUtils.DetermineType(ldVal), ldVal, meta);
                Engine.AppendPending(CRDTOperation.ArrayUnshift(ParentPath, entry, meta.UpdatedAt, Engine.ClientReplicaId));
            }
            Engine.ScheduleFlush();
            return Engine.VisibleIdsFor(ParentPath).Count;
        }

        /// <summary>Remove and return the last element of an array.</summary>
        public object Pop()
        {
            var ids = Engine.VisibleIdsFor(ParentPath);
            if (ids.Count == 0) return null;
            var lastId = ids[ids.Count - 1];
            var cont = Engine.GetContainerAt(ParentPath);
            object ret = null;
            if (cont?.Type == LDValueType.Array && cont.ArrayValue.Entries.TryGetValue(lastId, out var e))
                ret = CRDTUtils.ToAny(e.Value);
            Engine.AppendPending(CRDTOperation.ArrayRemove(ParentPath, lastId, (int)CRDTUtils.NowMs(), Engine.ClientReplicaId));
            Engine.ScheduleFlush();
            return ret;
        }

        /// <summary>Remove element at index.</summary>
        public object RemoveAt(int index)
        {
            var id = Engine.GetArrayIdAt(ParentPath, index);
            if (id == null) return null;
            var cont = Engine.GetContainerAt(ParentPath);
            object ret = null;
            if (cont?.Type == LDValueType.Array && cont.ArrayValue.Entries.TryGetValue(id, out var e))
                ret = CRDTUtils.ToAny(e.Value);
            Engine.AppendPending(CRDTOperation.ArrayRemove(ParentPath, id, (int)CRDTUtils.NowMs(), Engine.ClientReplicaId));
            Engine.ScheduleFlush();
            return ret;
        }

        /// <summary>Array length.</summary>
        public int Length => Engine.VisibleIdsFor(ParentPath).Count;

        /// <summary>Flush pending operations.</summary>
        public async Task FlushAsync() => await Engine.Flush();
    }
}
