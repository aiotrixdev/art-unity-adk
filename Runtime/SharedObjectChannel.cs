using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ART.ADK.CRDT;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ART.ADK
{
    /// <summary>
    /// CRDT-backed shared object subscription for collaborative real-time state.
    /// </summary>
    public sealed class SharedObjectChannel : BaseSubscription
    {
        public CRDTEngine Crdt { get; }

        public SharedObjectChannel(
            string connectionID,
            ChannelConfig channelConfig,
            IWebSocketHandler websocketHandler,
            string process = "subscribe")
            : base(connectionID, channelConfig, websocketHandler, process)
        {
            LDMap snapshotMap;
            if (channelConfig.Snapshot != null)
                snapshotMap = CRDTUtils.FromAnyToLDMap(channelConfig.Snapshot);
            else
                snapshotMap = new LDMap();

            Crdt = new CRDTEngine(snapshotMap, _ => { });
            Crdt.SetReplicaId($"r-{Guid.NewGuid().ToString().Substring(0, 8).ToLower()}");

            Crdt.SetMergeCallback(ops => ExecuteServerMerge(ops));
        }

        public CRDTProxy State() => Crdt.State();

        public async Task Flush() => await Crdt.Flush();

        public (Func<Task<object>> execute, Func<Action<object>, Action> listen) Query(string path)
            => Crdt.Query(path);

        // ---- Send CRDT ops to server ----
        private void ExecuteServerMerge(List<CRDTOperation> ops)
        {
            if (ops.Count == 0) return;
            if (WebSocketHandler.GetConnection() == null) return;

            var serializedOps = SerializeOps(ops);
            _ = Task.Run(async () =>
            {
                try { await PushArray("merge", JArray.FromObject(serializedOps)); }
                catch (Exception ex) { Debug.Log($"[ART] CRDT push error: {ex.Message}"); }
            });
        }

        public override async Task HandleMessage(string evt, JObject payload)
        {
            var returnFlag = payload["return_flag"]?.ToString() ?? "";
            if (returnFlag == "SA") return;

            // Presence events
            if (evt == "art_presence")
            {
                var content = payload["data"];
                if (content != null)
                    Emitter.Emit("art_presence", content);
                return;
            }

            // CRDT merge events
            var rawContent = payload["content"] ?? payload["data"];
            if (rawContent == null) return;

            List<Dictionary<string, object>> rawOps = null;

            if (rawContent is JArray jarr)
            {
                rawOps = jarr.ToObject<List<Dictionary<string, object>>>();
            }
            else if (rawContent.Type == JTokenType.String)
            {
                var str = rawContent.ToString();
                try
                {
                    var parsed = JToken.Parse(str);
                    if (parsed is JArray arr2)
                    {
                        rawOps = arr2.ToObject<List<Dictionary<string, object>>>();
                    }
                    else if (parsed.Type == JTokenType.String)
                    {
                        // Double-encoded
                        var inner = JToken.Parse(parsed.ToString());
                        if (inner is JArray arr3)
                            rawOps = arr3.ToObject<List<Dictionary<string, object>>>();
                    }
                }
                catch { }
            }

            if (rawOps == null)
            {
                Debug.Log("[ART] Failed to parse CRDT ops");
                return;
            }

            var ops = DeserializeOps(rawOps);
            if (ops.Count == 0) return;

            var myReplica = Crdt.GetReplicaId();
            var filtered = ops.Where(o => o.ReplicaId != myReplica).ToList();

            if (filtered.Count == 0) return;

            Crdt.Merge(filtered);

            Crdt.Merge(filtered);
        }

        // ---- Serialization ----
        private List<Dictionary<string, object>> SerializeOps(List<CRDTOperation> ops)
        {
            return ops.Select(op =>
            {
                var d = new Dictionary<string, object>
                {
                    ["path"] = op.Path,
                    ["timestamp"] = op.Timestamp,
                    ["replicaId"] = op.ReplicaId
                };

                switch (op.OpType)
                {
                    case CRDTOpType.Add:
                        d["op"] = "add";
                        if (op.Entry != null) d["entry"] = SerializeEntry(op.Entry);
                        break;
                    case CRDTOpType.Replace:
                        d["op"] = "replace";
                        d["entry"] = SerializeEntry(op.Entry);
                        break;
                    case CRDTOpType.Remove:
                        d["op"] = "remove";
                        break;
                    case CRDTOpType.ArrayPush:
                        d["op"] = "array-push";
                        d["entry"] = SerializeEntry(op.Entry);
                        if (op.Ref != null) d["ref"] = op.Ref;
                        break;
                    case CRDTOpType.ArrayUnshift:
                        d["op"] = "array-unshift";
                        d["entry"] = SerializeEntry(op.Entry);
                        break;
                    case CRDTOpType.ArrayRemove:
                        d["op"] = "array-remove";
                        d["ref"] = op.Ref;
                        break;
                }
                return d;
            }).ToList();
        }

        private Dictionary<string, object> SerializeEntry(LDEntry entry)
        {
            return new Dictionary<string, object>
            {
                ["id"] = entry.Id,
                ["key"] = entry.Key,
                ["type"] = CRDTUtils.EntryTypeToString(entry.EntryType),
                ["value"] = SerializeValue(entry.Value, entry.Meta.ReplicaId),
                ["meta"] = SerializeMeta(entry.Meta)
            };
        }

        private Dictionary<string, object> SerializeMeta(LDMeta meta)
        {
            var d = new Dictionary<string, object>
            {
                ["updatedAt"] = meta.UpdatedAt,
                ["version"] = meta.Version,
                ["replicaId"] = meta.ReplicaId
            };
            if (meta.Tombstone.HasValue) d["tombstone"] = meta.Tombstone.Value;
            if (meta.AfterIsSet) d["after"] = meta.After;
            return d;
        }

        private object SerializeValue(LDValue value, string replicaId)
        {
            switch (value.Type)
            {
                case LDValueType.String: return value.StringValue;
                case LDValueType.Number: return value.NumberValue;
                case LDValueType.Boolean: return value.BoolValue;
                case LDValueType.Null: return null;
                case LDValueType.Map:
                    var dict = new Dictionary<string, object>();
                    foreach (var kv in value.MapValue.Index)
                    {
                        if (kv.Key == "index" && kv.Value.Value.Type == LDValueType.Map)
                        {
                            var indexDict = new Dictionary<string, object>();
                            foreach (var ik in kv.Value.Value.MapValue.Index)
                                indexDict[ik.Key] = SerializeEntry(ik.Value);
                            dict["index"] = indexDict;
                        }
                        else
                        {
                            dict[kv.Key] = SerializeEntry(kv.Value);
                        }
                    }
                    dict["meta"] = new Dictionary<string, object>
                    {
                        ["updatedAt"] = value.MapValue.Meta.UpdatedAt,
                        ["version"] = value.MapValue.Meta.Version,
                        ["replicaId"] = replicaId
                    };
                    return dict;
                case LDValueType.Array:
                    var arrDict = new Dictionary<string, object>();
                    foreach (var kv in value.ArrayValue.Entries)
                        arrDict[kv.Key] = SerializeEntry(kv.Value);
                    return arrDict;
                default: return null;
            }
        }

        // ---- Deserialization ----
        private List<CRDTOperation> DeserializeOps(List<Dictionary<string, object>> dOps)
        {
            var result = new List<CRDTOperation>();
            foreach (var d in dOps)
            {
                if (!d.TryGetValue("op", out var opObj)) continue;
                var op = opObj?.ToString();

                string[] path;
                if (d.TryGetValue("path", out var pathObj) && pathObj is JArray pathArr)
                    path = pathArr.Select(p => p.ToString()).ToArray();
                else continue;

                if (!d.TryGetValue("timestamp", out var tsObj) || !long.TryParse(tsObj.ToString(), out var ts)) continue;
                if (!d.TryGetValue("replicaId", out var ridObj)) continue;
                var replicaId = ridObj.ToString();

                switch (op)
                {
                    case "add":
                        LDEntry addEntry = null;
                        if (d.TryGetValue("entry", out var addEntryObj))
                            addEntry = DeserializeEntry(addEntryObj);
                        result.Add(CRDTOperation.Add(path, addEntry, ts, replicaId));
                        break;
                    case "replace":
                        if (!d.TryGetValue("entry", out var replEntryObj)) continue;
                        result.Add(CRDTOperation.Replace(path, DeserializeEntry(replEntryObj), ts, replicaId));
                        break;
                    case "remove":
                        result.Add(CRDTOperation.Remove(path, ts, replicaId));
                        break;
                    case "array-push":
                        if (!d.TryGetValue("entry", out var apEntryObj)) continue;
                        var apRef = d.TryGetValue("ref", out var apRefObj) ? apRefObj?.ToString() : null;
                        result.Add(CRDTOperation.ArrayPush(path, apRef, DeserializeEntry(apEntryObj), ts, replicaId));
                        break;
                    case "array-unshift":
                        if (!d.TryGetValue("entry", out var auEntryObj)) continue;
                        result.Add(CRDTOperation.ArrayUnshift(path, DeserializeEntry(auEntryObj), ts, replicaId));
                        break;
                    case "array-remove":
                        if (!d.TryGetValue("ref", out var arRefObj)) continue;
                        result.Add(CRDTOperation.ArrayRemove(path, arRefObj.ToString(), ts, replicaId));
                        break;
                }
            }
            return result;
        }

        private LDEntry DeserializeEntry(object entryObj)
        {
            JObject dEntry;
            if (entryObj is JObject jo) dEntry = jo;
            else dEntry = JObject.FromObject(entryObj);

            var id = dEntry["id"]?.ToString() ?? CRDTUtils.GenerateId();
            var key = dEntry["key"]?.ToString() ?? id;
            var type = CRDTUtils.StringToEntryType(dEntry["type"]?.ToString() ?? "object");

            LDValue value;
            if (dEntry["value"] != null)
            {
                if (type == LDEntryType.Object && dEntry["value"] is JObject valDict)
                {
                    var ldMap = new LDMap();
                    foreach (var kv in valDict)
                    {
                        if (kv.Value is JObject innerObj && innerObj["id"] != null)
                            ldMap.Index[kv.Key] = DeserializeEntry(innerObj);
                        else
                        {
                            var ldVal = CRDTUtils.ToLDValue(kv.Value);
                            ldMap.Index[kv.Key] = new LDEntry(CRDTUtils.GenerateId(), kv.Key,
                                CRDTUtils.DetermineType(ldVal), ldVal,
                                new LDMeta((long)CRDTUtils.NowMs(), 1, "remote"));
                        }
                    }
                    value = LDValue.FromMap(ldMap);
                }
                else
                {
                    value = CRDTUtils.ToLDValue(dEntry["value"]);
                }
            }
            else
            {
                value = LDValue.Null;
            }

            var meta = new LDMeta();
            if (dEntry["meta"] is JObject m)
            {
                meta.UpdatedAt = m["updatedAt"]?.Value<long>() ?? (long)CRDTUtils.NowMs();
                meta.Version = m["version"]?.Value<int>() ?? 1;
                meta.ReplicaId = m["replicaId"]?.ToString() ?? "remote";
                if (m["tombstone"] != null)
                    meta.Tombstone = m["tombstone"].Value<bool>();
                if (m.ContainsKey("after"))
                {
                    meta.After = m["after"]?.ToString();
                    meta.AfterIsSet = true;
                }
            }

            return new LDEntry(id, key, type, value, meta);
        }
    }
}
