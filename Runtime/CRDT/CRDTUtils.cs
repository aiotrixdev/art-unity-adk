using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ART.ADK.CRDT
{
    public static class CRDTUtils
    {
        public static string GenerateId()
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var rand = new System.Random().Next(0, (int)Math.Pow(36, 6));
            return $"{ts}-{Convert.ToString(rand, 16)}";
        }

        public static double NowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        public static LDMeta DefaultMeta(string replicaId = "client")
        {
            return new LDMeta((int)NowMs(), 1, replicaId);
        }

        public static LDEntryType DetermineType(LDValue value)
        {
            switch (value.Type)
            {
                case LDValueType.String: return LDEntryType.String;
                case LDValueType.Number: return LDEntryType.Number;
                case LDValueType.Boolean: return LDEntryType.Boolean;
                case LDValueType.Array: return LDEntryType.Array;
                case LDValueType.Map: return LDEntryType.Object;
                default: return LDEntryType.Object;
            }
        }

        public static LDValue ToLDValue(object v, string replicaId = "client")
        {
            if (v == null) return LDValue.Null;

            if (v is string s) return LDValue.FromString(s);
            if (v is bool b) return LDValue.FromBool(b);
            if (v is double d) return LDValue.FromNumber(d);
            if (v is int i) return LDValue.FromNumber(i);
            if (v is float f) return LDValue.FromNumber(f);
            if (v is long l) return LDValue.FromNumber(l);

            if (v is JValue jv)
            {
                switch (jv.Type)
                {
                    case JTokenType.String: return LDValue.FromString(jv.ToString());
                    case JTokenType.Boolean: return LDValue.FromBool(jv.Value<bool>());
                    case JTokenType.Integer: return LDValue.FromNumber(jv.Value<double>());
                    case JTokenType.Float: return LDValue.FromNumber(jv.Value<double>());
                    default: return LDValue.Null;
                }
            }

            if (v is JArray jarr)
            {
                var ldArr = new LDArray { Meta = DefaultMeta(replicaId) };
                string prev = null;
                foreach (var item in jarr)
                {
                    var id = GenerateId();
                    var ldVal = ToLDValue(item, replicaId);
                    var meta = DefaultMeta(replicaId);
                    meta.After = prev;
                    meta.AfterIsSet = true;
                    var entry = new LDEntry(id, id, DetermineType(ldVal), ldVal, meta);
                    ldArr.Entries[id] = entry;
                    prev = id;
                }
                return LDValue.FromArray(ldArr);
            }

            if (v is IList<object> list)
            {
                var ldArr = new LDArray { Meta = DefaultMeta(replicaId) };
                string prev = null;
                foreach (var item in list)
                {
                    var id = GenerateId();
                    var ldVal = ToLDValue(item, replicaId);
                    var meta = DefaultMeta(replicaId);
                    meta.After = prev;
                    meta.AfterIsSet = true;
                    var entry = new LDEntry(id, id, DetermineType(ldVal), ldVal, meta);
                    ldArr.Entries[id] = entry;
                    prev = id;
                }
                return LDValue.FromArray(ldArr);
            }

            if (v is JObject jobj)
            {
                var ldMap = new LDMap { Meta = DefaultMeta(replicaId) };
                foreach (var kv in jobj)
                {
                    var ldVal = ToLDValue(kv.Value, replicaId);
                    ldMap.Index[kv.Key] = new LDEntry(GenerateId(), kv.Key, DetermineType(ldVal), ldVal, DefaultMeta(replicaId));
                }
                return LDValue.FromMap(ldMap);
            }

            if (v is IDictionary<string, object> dict)
            {
                var ldMap = new LDMap { Meta = DefaultMeta(replicaId) };
                foreach (var kv in dict)
                {
                    var ldVal = ToLDValue(kv.Value, replicaId);
                    ldMap.Index[kv.Key] = new LDEntry(GenerateId(), kv.Key, DetermineType(ldVal), ldVal, DefaultMeta(replicaId));
                }
                return LDValue.FromMap(ldMap);
            }

            return LDValue.Null;
        }

        public static LDMap FromAnyToLDMap(object v)
        {
            if (v == null) return new LDMap();
            if (v is LDMap m) return m;
            var ldVal = ToLDValue(v);
            if (ldVal.Type == LDValueType.Map) return ldVal.MapValue;
            return new LDMap();
        }

        public static object ToAny(LDValue val)
        {
            switch (val.Type)
            {
                case LDValueType.String: return val.StringValue;
                case LDValueType.Number: return val.NumberValue;
                case LDValueType.Boolean: return val.BoolValue;
                case LDValueType.Null: return null;
                case LDValueType.Map:
                    var dict = new Dictionary<string, object>();
                    foreach (var kv in val.MapValue.Index)
                        dict[kv.Key] = ToAny(kv.Value.Value);
                    return dict;
                case LDValueType.Array:
                    var ids = LinearizeRGA(val.ArrayValue);
                    var list = new List<object>();
                    foreach (var id in ids)
                    {
                        if (val.ArrayValue.Entries.TryGetValue(id, out var entry))
                            list.Add(ToAny(entry.Value));
                    }
                    return list;
                default: return null;
            }
        }

        public static LDContainer ToContainer(LDValue val)
        {
            if (val.Type == LDValueType.Map) return LDContainer.FromMap(val.MapValue);
            if (val.Type == LDValueType.Array) return LDContainer.FromArray(val.ArrayValue);
            throw new ARTInvalidPathException("Value is not a container");
        }

        public static List<string> LinearizeRGA(LDArray arr)
        {
            var afterToKids = new Dictionary<string, List<LDEntry>>();
            // Use "" as the sentinel for "after = null" (head)
            const string headKey = "";

            foreach (var kv in arr.Entries)
            {
                var e = kv.Value;
                string after;
                if (e.Meta.AfterIsSet)
                    after = e.Meta.After ?? headKey;
                else
                    after = headKey;

                if (!afterToKids.ContainsKey(after))
                    afterToKids[after] = new List<LDEntry>();
                afterToKids[after].Add(e);
            }

            // Sort siblings
            foreach (var key in afterToKids.Keys.ToList())
            {
                afterToKids[key].Sort((a, b) =>
                {
                    if (a.Meta.UpdatedAt != b.Meta.UpdatedAt)
                        return a.Meta.UpdatedAt.CompareTo(b.Meta.UpdatedAt);
                    if (a.Meta.ReplicaId != b.Meta.ReplicaId)
                        return string.Compare(a.Meta.ReplicaId, b.Meta.ReplicaId, StringComparison.Ordinal);
                    return string.Compare(a.Id, b.Id, StringComparison.Ordinal);
                });
            }

            var output = new List<string>();
            var seen = new HashSet<string>();

            void Walk(string parent)
            {
                var key = parent ?? headKey;
                if (!afterToKids.TryGetValue(key, out var kids)) return;
                foreach (var e in kids)
                {
                    if (!seen.Add(e.Id)) continue;
                    if (e.Meta.Tombstone != true) output.Add(e.Id);
                    Walk(e.Id);
                }
            }

            Walk(null);
            return output;
        }

        public static string EntryTypeToString(LDEntryType t)
        {
            switch (t)
            {
                case LDEntryType.String: return "string";
                case LDEntryType.Number: return "number";
                case LDEntryType.Boolean: return "boolean";
                case LDEntryType.Object: return "object";
                case LDEntryType.Array: return "array";
                default: return "object";
            }
        }

        public static LDEntryType StringToEntryType(string s)
        {
            switch (s)
            {
                case "string": return LDEntryType.String;
                case "number": return LDEntryType.Number;
                case "boolean": return LDEntryType.Boolean;
                case "array": return LDEntryType.Array;
                default: return LDEntryType.Object;
            }
        }
    }
}
