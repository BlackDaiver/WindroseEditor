using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WindroseEditor
{
    public enum BsonType : byte
    {
        Double   = 0x01,
        String   = 0x02,
        Document = 0x03,
        Array    = 0x04,
        Binary   = 0x05,
        Bool     = 0x08,
        DateTime = 0x09,
        Null     = 0x0A,
        Int32    = 0x10,
        Int64    = 0x12,
    }

    public readonly struct BsonBinary
    {
        public readonly byte[] Data;
        public readonly byte Subtype;
        public BsonBinary(byte[] data, byte subtype) { Data = data; Subtype = subtype; }
    }

    /// <summary>BSON value — preserves exact type for byte-perfect round-trip.</summary>
    public sealed class BsonValue
    {
        public readonly BsonType Type;
        private readonly object? _v;

        private BsonValue(BsonType t, object? v) { Type = t; _v = v; }

        // Factories
        public static BsonValue FromDouble(double v)              => new(BsonType.Double,   v);
        public static BsonValue FromString(string v)              => new(BsonType.String,   v);
        public static BsonValue FromDocument(BsonDocument v)      => new(BsonType.Document, v);
        public static BsonValue FromArray(BsonDocument v)         => new(BsonType.Array,    v);
        public static BsonValue FromBinary(byte[] d, byte sub)    => new(BsonType.Binary,   new BsonBinary(d, sub));
        public static BsonValue FromBool(bool v)                  => new(BsonType.Bool,     v);
        public static BsonValue FromDateTime(long v)              => new(BsonType.DateTime, v);
        public static readonly BsonValue Null                      = new(BsonType.Null,     null);
        public static BsonValue FromInt32(int v)                  => new(BsonType.Int32,    v);
        public static BsonValue FromInt64(long v)                 => new(BsonType.Int64,    v);

        // Accessors
        public double       AsDouble()   => (double)_v!;
        public string       AsString()   => (string)_v!;
        public BsonDocument AsDocument() => (BsonDocument)_v!;
        public BsonBinary   AsBinary()   => (BsonBinary)_v!;
        public bool         AsBool()     => (bool)_v!;
        public long         AsDateTime() => (long)_v!;
        public int          AsInt32()    => (int)_v!;
        public long         AsInt64()    => (long)_v!;

        public bool IsNull     => Type == BsonType.Null;
        public bool IsDocument => Type == BsonType.Document || Type == BsonType.Array;

        /// <summary>Returns integer value for both Int32 and Int64 types.</summary>
        public long TryAsLong() => Type switch
        {
            BsonType.Int32 => AsInt32(),
            BsonType.Int64 => AsInt64(),
            BsonType.Double => (long)AsDouble(),
            _ => 0
        };

        public override string ToString() => Type switch
        {
            BsonType.Null     => "null",
            BsonType.Bool     => AsBool().ToString(),
            BsonType.Int32    => AsInt32().ToString(),
            BsonType.Int64    => AsInt64().ToString(),
            BsonType.Double   => AsDouble().ToString("G"),
            BsonType.String   => AsString(),
            BsonType.Document => $"[doc {AsDocument().Count} fields]",
            BsonType.Array    => $"[array {AsDocument().Count} items]",
            _ => $"<{Type}>"
        };
    }

    /// <summary>
    /// Ordered BSON document — maintains insertion order for byte-perfect serialization.
    /// </summary>
    public sealed class BsonDocument : IEnumerable<KeyValuePair<string, BsonValue>>
    {
        private readonly List<string> _order = new();
        private readonly Dictionary<string, BsonValue> _map = new(StringComparer.Ordinal);

        public int Count => _order.Count;

        public BsonValue this[string key]
        {
            get => _map[key];
            set
            {
                if (!_map.ContainsKey(key)) _order.Add(key);
                _map[key] = value;
            }
        }

        public bool ContainsKey(string key) => _map.ContainsKey(key);

        public bool TryGetValue(string key, out BsonValue? value)
        {
            bool found = _map.TryGetValue(key, out var v);
            value = v;
            return found;
        }

        public void Remove(string key)
        {
            if (_map.Remove(key)) _order.Remove(key);
        }

        public IEnumerator<KeyValuePair<string, BsonValue>> GetEnumerator()
        {
            foreach (var k in _order)
                yield return new KeyValuePair<string, BsonValue>(k, _map[k]);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>Walk a dot-separated path, e.g. "Inventory.Modules.0.Slots".</summary>
        public BsonValue? Navigate(string path)
        {
            BsonValue? cur = BsonValue.FromDocument(this);
            foreach (var part in path.Split('.'))
            {
                if (cur == null || !cur.IsDocument) return null;
                cur.AsDocument().TryGetValue(part, out cur);
            }
            return cur;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BSON Parser
    // ─────────────────────────────────────────────────────────────────────────

    public static class BsonParser
    {
        public static BsonDocument Parse(byte[] data, int pos = 0)
        {
            int docSize = BitConverter.ToInt32(data, pos);
            int end = pos + docSize;
            pos += 4;
            var doc = new BsonDocument();

            while (pos < end - 1)
            {
                byte btype = data[pos++];
                if (btype == 0) break;

                string name = ReadCString(data, ref pos);

                switch (btype)
                {
                    case 0x01: // double
                        doc[name] = BsonValue.FromDouble(BitConverter.ToDouble(data, pos));
                        pos += 8;
                        break;

                    case 0x02: // string
                    {
                        int slen = BitConverter.ToInt32(data, pos); pos += 4;
                        doc[name] = BsonValue.FromString(Encoding.UTF8.GetString(data, pos, slen - 1));
                        pos += slen;
                        break;
                    }

                    case 0x03: // embedded document
                    {
                        int sz = BitConverter.ToInt32(data, pos);
                        doc[name] = BsonValue.FromDocument(Parse(data, pos));
                        pos += sz;
                        break;
                    }

                    case 0x04: // array — must preserve type for round-trip
                    {
                        int sz = BitConverter.ToInt32(data, pos);
                        doc[name] = BsonValue.FromArray(Parse(data, pos));
                        pos += sz;
                        break;
                    }

                    case 0x05: // binary
                    {
                        int blen = BitConverter.ToInt32(data, pos); pos += 4;
                        byte sub = data[pos++];
                        byte[] bd = new byte[blen];
                        Buffer.BlockCopy(data, pos, bd, 0, blen);
                        doc[name] = BsonValue.FromBinary(bd, sub);
                        pos += blen;
                        break;
                    }

                    case 0x08: // bool
                        doc[name] = BsonValue.FromBool(data[pos++] != 0);
                        break;

                    case 0x09: // datetime
                        doc[name] = BsonValue.FromDateTime(BitConverter.ToInt64(data, pos));
                        pos += 8;
                        break;

                    case 0x0A: // null
                        doc[name] = BsonValue.Null;
                        break;

                    case 0x10: // int32
                        doc[name] = BsonValue.FromInt32(BitConverter.ToInt32(data, pos));
                        pos += 4;
                        break;

                    case 0x12: // int64
                        doc[name] = BsonValue.FromInt64(BitConverter.ToInt64(data, pos));
                        pos += 8;
                        break;

                    default:
                        throw new InvalidDataException(
                            $"Unknown BSON type 0x{btype:X2} at pos {pos - 1} field '{name}'");
                }
            }
            return doc;
        }

        static string ReadCString(byte[] data, ref int pos)
        {
            int start = pos;
            while (pos < data.Length && data[pos] != 0) pos++;
            string s = Encoding.UTF8.GetString(data, start, pos - start);
            pos++; // skip null terminator
            return s;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BSON Serializer
    // ─────────────────────────────────────────────────────────────────────────

    public static class BsonSerializer
    {
        public static byte[] Serialize(BsonDocument doc)
        {
            using var ms = new MemoryStream();
            WriteDoc(ms, doc);
            return ms.ToArray();
        }

        static void WriteDoc(MemoryStream ms, BsonDocument doc)
        {
            long sizePos = ms.Position;
            ms.Write(BitConverter.GetBytes(0), 0, 4); // placeholder

            foreach (var (key, val) in doc)
                WriteField(ms, key, val);

            ms.WriteByte(0); // terminator

            long endPos = ms.Position;
            int size = (int)(endPos - sizePos);
            ms.Position = sizePos;
            ms.Write(BitConverter.GetBytes(size), 0, 4);
            ms.Position = endPos;
        }

        static void WriteField(MemoryStream ms, string key, BsonValue val)
        {
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);

            ms.WriteByte((byte)val.Type);
            ms.Write(keyBytes, 0, keyBytes.Length);
            ms.WriteByte(0); // null terminator for key

            switch (val.Type)
            {
                case BsonType.Double:
                    ms.Write(BitConverter.GetBytes(val.AsDouble()), 0, 8);
                    break;

                case BsonType.String:
                {
                    byte[] sb = Encoding.UTF8.GetBytes(val.AsString());
                    int len = sb.Length + 1; // +1 for null terminator
                    ms.Write(BitConverter.GetBytes(len), 0, 4);
                    ms.Write(sb, 0, sb.Length);
                    ms.WriteByte(0);
                    break;
                }

                case BsonType.Document:
                case BsonType.Array:
                    WriteDoc(ms, val.AsDocument());
                    break;

                case BsonType.Binary:
                {
                    var bin = val.AsBinary();
                    ms.Write(BitConverter.GetBytes(bin.Data.Length), 0, 4);
                    ms.WriteByte(bin.Subtype);
                    ms.Write(bin.Data, 0, bin.Data.Length);
                    break;
                }

                case BsonType.Bool:
                    ms.WriteByte(val.AsBool() ? (byte)1 : (byte)0);
                    break;

                case BsonType.DateTime:
                    ms.Write(BitConverter.GetBytes(val.AsDateTime()), 0, 8);
                    break;

                case BsonType.Null:
                    break; // no payload

                case BsonType.Int32:
                    ms.Write(BitConverter.GetBytes(val.AsInt32()), 0, 4);
                    break;

                case BsonType.Int64:
                    ms.Write(BitConverter.GetBytes(val.AsInt64()), 0, 8);
                    break;

                default:
                    throw new InvalidDataException($"Cannot serialize BSON type {val.Type}");
            }
        }
    }
}
