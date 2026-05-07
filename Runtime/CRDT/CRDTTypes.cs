using System;
using System.Collections.Generic;

namespace ART.ADK.CRDT
{
    // ---- LDValue ----
    public enum LDValueType { String, Number, Boolean, Map, Array, Null }

    public class LDValue
    {
        public LDValueType Type { get; set; }
        public string StringValue { get; set; }
        public double NumberValue { get; set; }
        public bool BoolValue { get; set; }
        public LDMap MapValue { get; set; }
        public LDArray ArrayValue { get; set; }

        public static LDValue FromString(string s) => new LDValue { Type = LDValueType.String, StringValue = s };
        public static LDValue FromNumber(double n) => new LDValue { Type = LDValueType.Number, NumberValue = n };
        public static LDValue FromBool(bool b) => new LDValue { Type = LDValueType.Boolean, BoolValue = b };
        public static LDValue FromMap(LDMap m) => new LDValue { Type = LDValueType.Map, MapValue = m };
        public static LDValue FromArray(LDArray a) => new LDValue { Type = LDValueType.Array, ArrayValue = a };
        public static LDValue Null => new LDValue { Type = LDValueType.Null };
    }

    // ---- LDMeta ----
    public class LDMeta
    {
        public long UpdatedAt { get; set; }
        public int Version { get; set; } = 1;
        public string ReplicaId { get; set; } = "client";
        public int? Order { get; set; }
        public bool? Tombstone { get; set; }
        /// <summary>null means not set; empty string? means head (insert at beginning)</summary>
        public string After { get; set; }
        public bool AfterIsSet { get; set; }
        public string Next { get; set; }

        public LDMeta()
        {
            UpdatedAt = (long)CRDTUtils.NowMs();
        }

        public LDMeta(long updatedAt, int version, string replicaId)
        {
            UpdatedAt = updatedAt;
            Version = version;
            ReplicaId = replicaId;
        }
    }

    // ---- LDEntry ----
    public enum LDEntryType { String, Number, Boolean, Object, Array }

    public class LDEntry
    {
        public string Id { get; set; }
        public string Key { get; set; }
        public LDEntryType EntryType { get; set; }
        public LDValue Value { get; set; }
        public LDMeta Meta { get; set; }

        public LDEntry(string id, string key, LDEntryType type, LDValue value, LDMeta meta)
        {
            Id = id;
            Key = key;
            EntryType = type;
            Value = value;
            Meta = meta;
        }
    }

    // ---- LDMap ----
    public class LDMap
    {
        public Dictionary<string, LDEntry> Index { get; set; } = new Dictionary<string, LDEntry>();
        public LDMeta Meta { get; set; } = new LDMeta();

        public LDMap() { }
        public LDMap(Dictionary<string, LDEntry> index, LDMeta meta)
        {
            Index = index;
            Meta = meta;
        }
    }

    // ---- LDArray (RGA) ----
    public class LDArray
    {
        public Dictionary<string, LDEntry> Entries { get; set; } = new Dictionary<string, LDEntry>();
        public string Head { get; set; }
        public LDMeta Meta { get; set; } = new LDMeta();

        public LDArray() { }
        public LDArray(Dictionary<string, LDEntry> entries, string head, LDMeta meta)
        {
            Entries = entries;
            Head = head;
            Meta = meta;
        }
    }

    // ---- CRDTOperation ----
    public enum CRDTOpType { Add, Replace, Remove, ArrayPush, ArrayUnshift, ArrayRemove }

    public class CRDTOperation
    {
        public CRDTOpType OpType { get; set; }
        public string[] Path { get; set; }
        public LDEntry Entry { get; set; }
        public string Ref { get; set; }
        public long Timestamp { get; set; }
        public string ReplicaId { get; set; }

        public static CRDTOperation Add(string[] path, LDEntry entry, long ts, string replicaId)
            => new CRDTOperation { OpType = CRDTOpType.Add, Path = path, Entry = entry, Timestamp = ts, ReplicaId = replicaId };
        public static CRDTOperation Replace(string[] path, LDEntry entry, long ts, string replicaId)
            => new CRDTOperation { OpType = CRDTOpType.Replace, Path = path, Entry = entry, Timestamp = ts, ReplicaId = replicaId };
        public static CRDTOperation Remove(string[] path, long ts, string replicaId)
            => new CRDTOperation { OpType = CRDTOpType.Remove, Path = path, Timestamp = ts, ReplicaId = replicaId };
        public static CRDTOperation ArrayPush(string[] path, string refId, LDEntry entry, long ts, string replicaId)
            => new CRDTOperation { OpType = CRDTOpType.ArrayPush, Path = path, Ref = refId, Entry = entry, Timestamp = ts, ReplicaId = replicaId };
        public static CRDTOperation ArrayUnshift(string[] path, LDEntry entry, long ts, string replicaId)
            => new CRDTOperation { OpType = CRDTOpType.ArrayUnshift, Path = path, Entry = entry, Timestamp = ts, ReplicaId = replicaId };
        public static CRDTOperation ArrayRemove(string[] path, string refId, long ts, string replicaId)
            => new CRDTOperation { OpType = CRDTOpType.ArrayRemove, Path = path, Ref = refId, Timestamp = ts, ReplicaId = replicaId };
    }

    // ---- LDContainer ----
    public enum LDContainerType { Map, Array }

    public class LDContainer
    {
        public LDContainerType ContainerType { get; set; }
        public LDMap MapValue { get; set; }
        public LDArray ArrayValue { get; set; }

        public static LDContainer FromMap(LDMap m) => new LDContainer { ContainerType = LDContainerType.Map, MapValue = m };
        public static LDContainer FromArray(LDArray a) => new LDContainer { ContainerType = LDContainerType.Array, ArrayValue = a };
    }
}
