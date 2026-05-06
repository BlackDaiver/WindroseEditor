using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;

namespace WindroseEditor
{
    /// <summary>Raw player save data read from WAL or SST.</summary>
    public class PlayerSaveData
    {
        public long   Sequence  { get; set; }
        public int    CfId      { get; set; } = 2;
        public byte[] PlayerKey { get; set; } = Array.Empty<byte>();
        public byte[] BsonBytes { get; set; } = Array.Empty<byte>();
        public string SaveDir   { get; set; } = "";
    }

    // ── WAL entry: Put (Value != null) or Delete (Value == null) ─────────────
    public record WalEntry(int CfId, byte[] Key, byte[]? Value);

    public static class RocksDbAccess
    {
        const int BlockSize = 32768;

        // ── Known Column Family IDs (from MANIFEST) ────────────────────────
        public const int CF_PLAYER   = 2;   // R5BLPlayer (historical; player data may now live in CF_ACTOR)
        public const int CF_SHIP     = 3;   // R5BLShip     (ships)
        public const int CF_BUILDING = 4;   // R5BLBuilding (world buildings — NOT ships)
        public const int CF_ACTOR    = 5;   // R5BLActor_BuildingBlock (player actor data since game update)

        // ──────────────────────────────────────────────────────────────────
        // CRC32C (Castagnoli) — RocksDB uses this, NOT standard CRC32-IEEE
        // ──────────────────────────────────────────────────────────────────
        static readonly uint[] _crcTable = BuildCrcTable();

        static uint[] BuildCrcTable()
        {
            const uint Poly = 0x82F63B78;
            var t = new uint[256];
            for (int i = 0; i < 256; i++)
            {
                uint c = (uint)i;
                for (int j = 0; j < 8; j++)
                    c = (c & 1) != 0 ? (c >> 1) ^ Poly : c >> 1;
                t[i] = c;
            }
            return t;
        }

        static uint Crc32C(byte[] data, int offset, int count)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = offset; i < offset + count; i++)
                crc = (crc >> 8) ^ _crcTable[(crc ^ data[i]) & 0xFF];
            return crc ^ 0xFFFFFFFF;
        }

        static uint MaskedCrc(byte[] data, int offset, int count)
        {
            uint raw = Crc32C(data, offset, count);
            return ((raw >> 15) | (raw << 17)) + 0xa282ead8u;
        }

        // ──────────────────────────────────────────────────────────────────
        // Varint encoding/decoding (RocksDB uses protobuf-style varints)
        // ──────────────────────────────────────────────────────────────────
        public static (long value, int nextPos) ReadVarint(byte[] data, int pos)
        {
            long result = 0; int shift = 0;
            while (pos < data.Length)
            {
                byte b = data[pos++];
                result |= (long)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            return (result, pos);
        }

        public static byte[] WriteVarint(long n)
        {
            var buf = new List<byte>(10);
            do
            {
                byte b = (byte)(n & 0x7F);
                n >>= 7;
                if (n != 0) b |= 0x80;
                buf.Add(b);
            } while (n != 0);
            return buf.ToArray();
        }

        // ──────────────────────────────────────────────────────────────────
        // WAL Reader
        // Reassembles fragmented 32KB blocks, finds the last player entry
        // (CF=2, key_len=32, large BSON value).
        // ──────────────────────────────────────────────────────────────────
        public static PlayerSaveData? ReadFromWal(string saveDir)
        {
            var logFiles = Directory.GetFiles(saveDir, "*.log")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (logFiles.Length == 0) return null;

            // Scan all log files newest-first, stop when we find player data
            for (int fi = logFiles.Length - 1; fi >= 0; fi--)
            {
                var result = TryReadWalFile(logFiles[fi], saveDir);
                if (result != null) return result;
            }
            return null;
        }

        static PlayerSaveData? TryReadWalFile(string walPath, string saveDir)
        {
            byte[] raw;
            try { raw = File.ReadAllBytes(walPath); }
            catch { return null; }

            // Reassemble payload from block fragments
            using var payloadStream = new MemoryStream();
            int pos = 0;
            while (pos + 7 <= raw.Length)
            {
                int length = BitConverter.ToUInt16(raw, pos + 4);
                byte rtype  = raw[pos + 6];
                int  start  = pos + 7;
                int  avail  = Math.Min(length, raw.Length - start);
                if (rtype >= 1 && rtype <= 4)
                    payloadStream.Write(raw, start, avail);
                pos += BlockSize;
            }

            byte[] payload = payloadStream.ToArray();
            if (payload.Length < 12) return null;

            long   maxSeqSeen = 0;   // highest sequence across ALL batches in this WAL
            long   playerSeq  = 0;   // sequence of the last player-data batch
            int    lastCfId   = CF_PLAYER; // CF of the last player-data record found
            byte[]? lastKey  = null;
            byte[]? lastBson = null;

            pos = 0;
            while (pos + 12 <= payload.Length)
            {
                try
                {
                    long batchSeq   = BitConverter.ToInt64(payload, pos);
                    int  batchCount = BitConverter.ToInt32(payload, pos + 8);
                    int  p          = pos + 12;

                    // Track the highest sequence used: batchSeq + (batchCount-1) because
                    // each Put/Delete in the batch consumes one sequence number.
                    long batchLastSeq = batchSeq + Math.Max(0, batchCount - 1);
                    if (batchLastSeq > maxSeqSeen) maxSeqSeen = batchLastSeq;

                    for (int i = 0; i < batchCount && p < payload.Length; i++)
                    {
                        byte etype = payload[p++];

                        if (etype == 0x01 || etype == 0x05) // Put or ColumnFamilyPut
                        {
                            long cfId = 0;
                            if (etype == 0x05)
                                (cfId, p) = ReadVarint(payload, p);

                            (long keyLen, int np1) = ReadVarint(payload, p); p = np1;
                            byte[] key = new byte[(int)keyLen];
                            Buffer.BlockCopy(payload, p, key, 0, (int)keyLen);
                            p += (int)keyLen;

                            (long valLen, int np2) = ReadVarint(payload, p); p = np2;
                            byte[] val = new byte[(int)valLen];
                            Buffer.BlockCopy(payload, p, val, 0, (int)valLen);
                            p += (int)valLen;

                            // Player record: 32-byte GUID key, large valid BSON, any CF.
                            // CF can change between game updates (was CF=2/R5BLPlayer,
                            // now CF=5/R5BLActor_BuildingBlock after a game update).
                            // Use valLen > 100 000 to skip ship/building records (~10-50 KB).
                            if (keyLen == 32 && valLen > 100_000
                                && val.Length >= 4
                                && BitConverter.ToInt32(val, 0) == valLen)
                            {
                                lastKey   = key;
                                lastBson  = val;
                                playerSeq = batchSeq;
                                lastCfId  = (int)cfId;
                            }
                        }
                        else if (etype == 0x00 || etype == 0x04) // Delete or ColumnFamilyDelete
                        {
                            if (etype == 0x04) ReadVarint(payload, p); // skip CF id
                            (long klen, int np) = ReadVarint(payload, p); p = np;
                            p += (int)klen;
                        }
                        else break;
                    }
                    pos = p;
                }
                catch { break; }
            }

            if (lastKey == null || lastBson == null) return null;

            // Use the highest sequence seen in the entire WAL as our base for the next
            // write — not just the player entry's sequence — so we never write a duplicate.
            return new PlayerSaveData
            {
                Sequence  = maxSeqSeen > 0 ? maxSeqSeen : playerSeq,
                CfId      = lastCfId,
                PlayerKey = lastKey,
                BsonBytes = lastBson,
                SaveDir   = saveDir
            };
        }

        // ──────────────────────────────────────────────────────────────────
        // WAL Ship Entries — reads CF_SHIP puts and deletes from all WAL logs.
        // SSTs are only updated when the game flushes/compacts; until then the
        // WAL is the authoritative source for edits made via this editor.
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Scans all WAL log files and returns ship-level changes that have not yet
        /// been compacted into SST files:
        /// <list type="bullet">
        ///   <item><c>shipPuts</c>   — GUID → raw BSON for ships added via WAL Put.</item>
        ///   <item><c>shipDeletes</c>— GUIDs of ships removed via WAL Delete tombstone.</item>
        /// </list>
        /// The two sets are mutually exclusive: a later Put un-deletes a GUID and vice versa.
        /// </summary>
        public static (Dictionary<string, byte[]> shipPuts, HashSet<string> shipDeletes)
            ReadShipWalEntries(string saveDir)
        {
            var shipPuts    = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var shipDeletes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var logFiles = Directory.GetFiles(saveDir, "*.log")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var logFile in logFiles)
            {
                byte[] raw;
                try { raw = File.ReadAllBytes(logFile); }
                catch { continue; }

                // Reassemble payload from 32 KB blocks
                using var ps = new MemoryStream();
                int pos = 0;
                while (pos + 7 <= raw.Length)
                {
                    int  length = BitConverter.ToUInt16(raw, pos + 4);
                    byte rtype  = raw[pos + 6];
                    int  start  = pos + 7;
                    int  avail  = Math.Min(length, raw.Length - start);
                    if (rtype >= 1 && rtype <= 4) ps.Write(raw, start, avail);
                    pos += BlockSize;
                }

                byte[] payload = ps.ToArray();
                pos = 0;

                while (pos + 12 <= payload.Length)
                {
                    try
                    {
                        long batchSeq   = BitConverter.ToInt64(payload, pos);
                        int  batchCount = BitConverter.ToInt32(payload, pos + 8);
                        int  p          = pos + 12;

                        for (int i = 0; i < batchCount && p < payload.Length; i++)
                        {
                            byte etype = payload[p++];

                            if (etype == 0x05) // kTypeColumnFamilyValue
                            {
                                (long cfId,   int np1) = ReadVarint(payload, p); p = np1;
                                (long keyLen, int np2) = ReadVarint(payload, p); p = np2;

                                string? guid = null;
                                if (cfId == CF_SHIP && keyLen == 32 && p + 32 <= payload.Length)
                                    guid = System.Text.Encoding.ASCII
                                               .GetString(payload, p, 32).ToUpperInvariant();
                                p += (int)keyLen;

                                (long valLen, int np3) = ReadVarint(payload, p); p = np3;
                                if (guid != null && valLen >= 4
                                    && p + (int)valLen <= payload.Length)
                                {
                                    byte[] val = new byte[(int)valLen];
                                    Buffer.BlockCopy(payload, p, val, 0, (int)valLen);
                                    if (BitConverter.ToInt32(val, 0) == val.Length)
                                    {
                                        shipPuts[guid] = val;
                                        shipDeletes.Remove(guid);
                                    }
                                }
                                p += (int)valLen;
                            }
                            else if (etype == 0x04) // kTypeColumnFamilyDeletion
                            {
                                (long cfId,   int np1) = ReadVarint(payload, p); p = np1;
                                (long keyLen, int np2) = ReadVarint(payload, p); p = np2;
                                if (cfId == CF_SHIP && keyLen == 32
                                    && p + 32 <= payload.Length)
                                {
                                    string guid = System.Text.Encoding.ASCII
                                                      .GetString(payload, p, 32).ToUpperInvariant();
                                    shipDeletes.Add(guid);
                                    shipPuts.Remove(guid);
                                }
                                p += (int)keyLen;
                            }
                            else if (etype == 0x01) // kTypeValue (default CF)
                            {
                                (long keyLen, int np1) = ReadVarint(payload, p); p = np1;
                                p += (int)keyLen;
                                (long valLen, int np2) = ReadVarint(payload, p); p = np2;
                                p += (int)valLen;
                            }
                            else if (etype == 0x00) // kTypeDeletion (default CF)
                            {
                                (long keyLen, int np1) = ReadVarint(payload, p); p = np1;
                                p += (int)keyLen;
                            }
                            else break; // unknown type — stop this batch
                        }
                        pos = p;
                    }
                    catch { break; }
                }
            }

            return (shipPuts, shipDeletes);
        }

        // ──────────────────────────────────────────────────────────────────
        // MANIFEST Parser — extracts last_sequence and log_number
        // ──────────────────────────────────────────────────────────────────
        public static (long LastSeq, long NextFileNum, long LogNum) ParseManifest(string saveDir)
        {
            var manifests = Directory.GetFiles(saveDir, "MANIFEST-*")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (manifests.Length == 0) return (0, 0, 0);

            byte[] raw;
            try { raw = File.ReadAllBytes(manifests[^1]); }
            catch { return (0, 0, 0); }

            long lastSeq = 0, nextFileNum = 0, logNum = 0;
            int pos = 0;

            while (pos < raw.Length)
            {
                if (pos + 7 > raw.Length) break;
                int length = BitConverter.ToUInt16(raw, pos + 4);
                int chunkStart = pos + 7;
                int chunkLen   = Math.Min(length, raw.Length - chunkStart);
                byte[] chunk   = new byte[chunkLen];
                Buffer.BlockCopy(raw, chunkStart, chunk, 0, chunkLen);
                pos += 7 + length;
                int rem = pos % 32768;
                if (rem > 0 && rem < 7) pos += 32768 - rem;

                int p = 0;
                while (p < chunk.Length)
                {
                    try
                    {
                        (long tag, int np) = ReadVarint(chunk, p); p = np;
                        if      (tag == 2) { (long v, int np2) = ReadVarint(chunk, p); p = np2; logNum      = Math.Max(logNum,      v); }
                        else if (tag == 3) { (long v, int np2) = ReadVarint(chunk, p); p = np2; nextFileNum = Math.Max(nextFileNum, v); }
                        else if (tag == 4) { (long v, int np2) = ReadVarint(chunk, p); p = np2; lastSeq     = Math.Max(lastSeq,     v); }
                        // kDeletedFile (6): level + file_number — two varints to skip.
                        // Without this, 'else p++' misparses multi-varint payloads and
                        // corrupts lastSeq/nextFileNum extraction from the same chunk.
                        else if (tag == 6) { (long _, int np2) = ReadVarint(chunk, p); p = np2;   // level
                                             (long _, int np3) = ReadVarint(chunk, p); p = np3; } // file_number
                        // Length-prefixed tags: kComparatorName(1), kColumnFamilyAdd(201)
                        else if (tag == 1 || tag == 201) { (long sLen, int np2) = ReadVarint(chunk, p); p = np2; p += (int)sLen; }
                        // Single-varint tags not already matched above
                        else if (tag == 9 || tag == 10 || tag == 200 || tag == 203) { (long _, int np2) = ReadVarint(chunk, p); p = np2; }
                        else p++;
                    }
                    catch { p++; }
                }
            }
            return (lastSeq, nextFileNum, logNum);
        }

        // ──────────────────────────────────────────────────────────────────
        // MANIFEST CF Mapping — SST file-number → column-family ID
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Parses the MANIFEST to return a mapping of SST file-number → CF ID.
        /// Returns an empty dict on failure (caller should treat unknowns as "scan anyway").
        /// </summary>
        public static Dictionary<long, int> ParseManifestCfMapping(string saveDir)
        {
            var mapping  = new Dictionary<long, int>();
            var manifests = Directory.GetFiles(saveDir, "MANIFEST-*")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
            if (manifests.Length == 0) return mapping;

            byte[] raw;
            try { raw = File.ReadAllBytes(manifests[^1]); }
            catch { return mapping; }

            int currentCf = 0;
            int pos = 0;

            while (pos < raw.Length)
            {
                if (pos + 7 > raw.Length) break;
                int length     = BitConverter.ToUInt16(raw, pos + 4);
                int chunkStart = pos + 7;
                int chunkLen   = Math.Min(length, raw.Length - chunkStart);
                byte[] chunk   = new byte[chunkLen];
                Buffer.BlockCopy(raw, chunkStart, chunk, 0, chunkLen);
                pos += 7 + length;
                int rem = pos % 32768;
                if (rem > 0 && rem < 7) pos += 32768 - rem;

                ParseVersionEditChunk(chunk, ref currentCf, mapping);
            }
            return mapping;
        }

        static void ParseVersionEditChunk(byte[] chunk, ref int currentCf,
                                           Dictionary<long, int> mapping)
        {
            int p = 0;
            while (p < chunk.Length)
            {
                int saved = p;
                try
                {
                    (long tag, int np) = ReadVarint(chunk, p); p = np;
                    switch (tag)
                    {
                        case 200: // kColumnFamily — sets CF context for this VersionEdit
                        {
                            (long cfId, int np2) = ReadVarint(chunk, p); p = np2;
                            currentCf = (int)cfId;
                            break;
                        }
                        case 7: // kNewFile: level, fileNum, fileSize, smallestKey, largestKey
                        {
                            (long _lvl,    int np2) = ReadVarint(chunk, p); p = np2;
                            (long fileNum, int np3) = ReadVarint(chunk, p); p = np3;
                            mapping[fileNum] = currentCf;
                            // Skip file_size + smallest key + largest key so we can
                            // continue parsing further records in the same chunk.
                            (long _sz,   int np4) = ReadVarint(chunk, p); p = np4;
                            (long skLen, int np5) = ReadVarint(chunk, p); p = np5;
                            p += (int)skLen;
                            (long lkLen, int np6) = ReadVarint(chunk, p); p = np6;
                            p += (int)lkLen;
                            break;
                        }
                        case 15:  // kNewFile4
                        case 103: // kNewFile5 (RocksDB 7.x+) — record fileNum then stop;
                                  // trailing fields are version-dependent and complex.
                        {
                            (long _lvl,    int np2) = ReadVarint(chunk, p); p = np2;
                            (long fileNum, int np3) = ReadVarint(chunk, p); p = np3;
                            mapping[fileNum] = currentCf;
                            return; // remaining fields are complex — stop this chunk
                        }
                        case 6:  // kDeletedFile — level (varint) + file_number (varint).
                                 // MUST be handled explicitly: falling to default (p = saved+1)
                                 // corrupts the stream and prevents later kNewFile5 records in
                                 // the same chunk (the final compaction result) from being read.
                                 // That would leave the newest SST unmapped → wrong CF → write
                                 // to the wrong column family → edits silently discarded by game.
                        {
                            (long _lvl,     int np2) = ReadVarint(chunk, p); p = np2;
                            (long _fileNum, int np3) = ReadVarint(chunk, p); p = np3;
                            break;
                        }
                        case 1:   // kComparatorName — length-prefixed string
                        case 201: // kColumnFamilyAdd — length-prefixed string
                        {
                            (long sLen, int np2) = ReadVarint(chunk, p); p = np2;
                            p += (int)sLen;
                            break;
                        }
                        case 2: case 3: case 4:  // logNum, nextFileNum, lastSeq
                        case 9: case 10:         // kPrevLogNumber, kMinLogNumberToKeep
                        case 203: case 300: case 301: // kMaxColumnFamily, kInAtomicGroup, kMinLogNum
                        {
                            (long _, int np2) = ReadVarint(chunk, p); p = np2;
                            break;
                        }
                        default:
                            p = saved + 1; // unknown tag — advance one byte
                            break;
                    }
                }
                catch { p = saved + 1; }
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // WAL Writer — multi-entry batch, one .log file per call.
        // Supports Put (Value != null) and Delete (Value == null) entries
        // for any column family in a single WriteBatch.
        // ──────────────────────────────────────────────────────────────────
        public static bool WriteWalMulti(string saveDir, long seq, long fileNum,
                                          IReadOnlyList<WalEntry> entries)
        {
            if (entries.Count == 0) return true;

            // Determine output file path
            string newPath;
            if (fileNum > 0)
            {
                newPath = Path.Combine(saveDir, $"{fileNum:D6}.log");
            }
            else
            {
                long maxNum = 0;
                foreach (var f in Directory.GetFiles(saveDir))
                {
                    string stem = Path.GetFileNameWithoutExtension(f);
                    if (long.TryParse(stem, out long n)) maxNum = Math.Max(maxNum, n);
                }
                newPath = Path.Combine(saveDir, $"{maxNum + 1:D6}.log");
            }

            return WriteWalBytes(newPath, BuildWalBatchPayload(seq, entries));
        }

        // Backward-compat single-entry wrapper
        public static bool WriteWal(string saveDir, long seq, long fileNum, int cfId,
                                     byte[] key, byte[] bsonBytes)
            => WriteWalMulti(saveDir, seq, fileNum,
                             new[] { new WalEntry(cfId, key, bsonBytes) });

        // ──────────────────────────────────────────────────────────────────
        // Low-level WAL building helpers
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds a raw WriteBatch payload (sequence + count + entries).
        /// Not yet wrapped in 32KB blocks — call BlockifyWal() next.
        /// </summary>
        static byte[] BuildWalBatchPayload(long seq, IReadOnlyList<WalEntry> entries)
        {
            using var batch = new MemoryStream();
            batch.Write(BitConverter.GetBytes(seq),           0, 8);
            batch.Write(BitConverter.GetBytes(entries.Count), 0, 4);

            foreach (var e in entries)
            {
                byte[] cfB = WriteVarint(e.CfId);
                byte[] kB  = WriteVarint(e.Key.Length);

                if (e.Value != null) // Put
                {
                    byte[] vB = WriteVarint(e.Value.Length);
                    batch.WriteByte(0x05); // kTypeColumnFamilyValue
                    batch.Write(cfB, 0, cfB.Length);
                    batch.Write(kB,  0, kB.Length);
                    batch.Write(e.Key, 0, e.Key.Length);
                    batch.Write(vB,  0, vB.Length);
                    batch.Write(e.Value, 0, e.Value.Length);
                }
                else // Delete
                {
                    batch.WriteByte(0x04); // kTypeColumnFamilyDeletion
                    batch.Write(cfB, 0, cfB.Length);
                    batch.Write(kB,  0, kB.Length);
                    batch.Write(e.Key, 0, e.Key.Length);
                }
            }
            return batch.ToArray();
        }

        /// <summary>
        /// Wraps a raw WriteBatch payload in 32KB WAL blocks with CRC-covered 7-byte
        /// headers (crc:4, len:2, type:1). Returns the complete WAL file contents.
        /// </summary>
        static byte[] BlockifyWal(byte[] batchArr)
        {
            const int MaxData = BlockSize - 7;
            using var output = new MemoryStream();
            int offset = 0, total = batchArr.Length;

            while (offset < total)
            {
                int chunkLen = Math.Min(MaxData, total - offset);
                bool isFirst = offset == 0;
                bool isLast  = offset + chunkLen >= total;
                byte rtype   = (isFirst && isLast) ? (byte)1
                             : isFirst             ? (byte)2
                             : isLast              ? (byte)4
                             :                       (byte)3;

                byte[] crcInput = new byte[1 + chunkLen];
                crcInput[0] = rtype;
                Buffer.BlockCopy(batchArr, offset, crcInput, 1, chunkLen);
                uint crc = MaskedCrc(crcInput, 0, crcInput.Length);

                output.Write(BitConverter.GetBytes(crc),              0, 4);
                output.Write(BitConverter.GetBytes((ushort)chunkLen), 0, 2);
                output.WriteByte(rtype);
                output.Write(batchArr, offset, chunkLen);
                offset += chunkLen;

                long written = output.Length % BlockSize;
                if (written > 0 && offset >= total)
                {
                    byte[] pad = new byte[BlockSize - written];
                    output.Write(pad, 0, pad.Length);
                }
            }
            return output.ToArray();
        }

        /// <summary>
        /// Builds a complete WAL file as a byte array (batch payload + 32KB blocks).
        /// </summary>
        public static byte[] BuildWalFileBytes(long seq, IReadOnlyList<WalEntry> entries)
            => BlockifyWal(BuildWalBatchPayload(seq, entries));

        static bool WriteWalBytes(string path, byte[] batchArr)
        {
            byte[] fileBytes = BlockifyWal(batchArr);
            try { File.WriteAllBytes(path, fileBytes); return true; }
            catch { return false; }
        }

        // ──────────────────────────────────────────────────────────────────
        // Backup ZIP injection
        // The game loads from _Latest.zip at startup, overwriting the live
        // RocksDB folder. We must inject our WAL into the ZIP so it survives.
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Parses a raw MANIFEST byte array (same format as ParseManifest, but
        /// works on bytes already in memory — e.g. extracted from a ZIP).
        /// </summary>
        public static (long LastSeq, long NextFileNum, long LogNum) ParseManifestBytes(byte[] raw)
        {
            long lastSeq = 0, nextFileNum = 0, logNum = 0;
            int pos = 0;

            while (pos < raw.Length)
            {
                if (pos + 7 > raw.Length) break;
                int length     = BitConverter.ToUInt16(raw, pos + 4);
                int chunkStart = pos + 7;
                int chunkLen   = Math.Min(length, raw.Length - chunkStart);
                byte[] chunk   = new byte[chunkLen];
                Buffer.BlockCopy(raw, chunkStart, chunk, 0, chunkLen);
                pos += 7 + length;
                int rem = pos % 32768;
                if (rem > 0 && rem < 7) pos += 32768 - rem;

                int p = 0;
                while (p < chunk.Length)
                {
                    try
                    {
                        (long tag, int np) = ReadVarint(chunk, p); p = np;
                        if      (tag == 4) { (long v, int np2) = ReadVarint(chunk, p); p = np2; lastSeq     = v; }
                        else if (tag == 2) { (long v, int np2) = ReadVarint(chunk, p); p = np2; logNum      = v; }
                        else if (tag == 3) { (long v, int np2) = ReadVarint(chunk, p); p = np2; nextFileNum = v; }
                        else if (tag == 6) { (long _, int np2) = ReadVarint(chunk, p); p = np2;
                                             (long _, int np3) = ReadVarint(chunk, p); p = np3; }
                        else if (tag == 1 || tag == 201) { (long sLen, int np2) = ReadVarint(chunk, p); p = np2; p += (int)sLen; }
                        else if (tag == 9 || tag == 10 || tag == 200 || tag == 203) { (long _, int np2) = ReadVarint(chunk, p); p = np2; }
                        else p++;
                    }
                    catch { p++; }
                }
            }
            return (lastSeq, nextFileNum, logNum);
        }

        // ── Diagnostic log ────────────────────────────────────────────────────
        static readonly string DiagLog = Path.Combine(
            Path.GetTempPath(), "WindroseEditorDiag.log");

        static void DiagWrite(string msg)
        {
            try
            {
                string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
                File.AppendAllText(DiagLog, line + Environment.NewLine);
            }
            catch { /* never let logging break saves */ }
        }

        /// <summary>
        /// Locates the _Latest.zip backup for the player DB at saveDir, then
        /// injects a WAL entry (containing all the provided WAL entries) into it.
        /// The sequence number is derived from the ZIP's own MANIFEST.
        /// Returns (null, zipPath) on success, or (errorString, "") on failure.
        /// </summary>
        public static (string? error, string zipInfo) InjectWalIntoBackupZip(
            string saveDir, IReadOnlyList<WalEntry> entries)
        {
            try { File.Delete(DiagLog); } catch { }   // fresh log per save

            DiagWrite($"InjectWalIntoBackupZip called: saveDir={saveDir}, entries={entries.Count}");

            if (entries.Count == 0) return (null, "no entries");

            string? zipPath = FindLatestBackupZip(saveDir);
            DiagWrite($"FindLatestBackupZip → {zipPath ?? "null"}");
            if (zipPath == null)
                return ($"Backup ZIP not found (saveDir={saveDir})", "");

            string tempPath = zipPath + ".tmp";
            try
            {
                // ── Step 1: read MANIFEST + meta/1 from inside the ZIP ────
                byte[]? manifestBytes = null;
                string? metaText      = null;

                using (var zf = ZipFile.OpenRead(zipPath))
                {
                    foreach (var entry in zf.Entries)
                    {
                        if (entry.FullName.StartsWith(
                                "Checkpoint/private/1/MANIFEST-",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            using var ms = new MemoryStream();
                            using var es = entry.Open();
                            es.CopyTo(ms);
                            manifestBytes = ms.ToArray();
                        }
                        else if (entry.FullName.Equals("Checkpoint/meta/1",
                                     StringComparison.OrdinalIgnoreCase))
                        {
                            using var ms = new MemoryStream();
                            using var es = entry.Open();
                            es.CopyTo(ms);
                            metaText = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                        }
                    }
                }

                DiagWrite($"manifestBytes={(manifestBytes?.Length.ToString() ?? "null")}, metaText={(metaText == null ? "null" : "ok")}");

                if (manifestBytes == null)
                    return ("No MANIFEST found inside ZIP", "");
                if (metaText == null)
                    return ("No meta/1 found inside ZIP", "");

                var (lastSeq, _, logNum) = ParseManifestBytes(manifestBytes);
                DiagWrite($"ParseManifest: lastSeq={lastSeq}, logNum={logNum}");
                if (logNum == 0)
                    return ("Could not parse logNum from ZIP MANIFEST", "");

                string walEntryName = $"Checkpoint/private/1/{logNum:D6}.log";
                // In meta/1 the path is relative (no leading "Checkpoint/")
                string walMetaPath  = $"private/1/{logNum:D6}.log";
                long   writeSeq     = lastSeq + 1;
                byte[] walBytes     = BuildWalFileBytes(writeSeq, entries);
                uint walCrc = Crc32C(walBytes, 0, walBytes.Length);
                DiagWrite($"walEntryName={walEntryName}, writeSeq={writeSeq}, walBytes={walBytes.Length}, walCrc={walCrc}");

                // ── Step 2: update meta/1 with the correct CRC32C of our WAL
                string updatedMeta = UpdateMetaCrc(metaText, walMetaPath, walCrc);

                // ── Step 3: rebuild ZIP, swapping in our WAL + updated meta/1
                if (File.Exists(tempPath)) File.Delete(tempPath);

                using (var srcZip = ZipFile.OpenRead(zipPath))
                {
                    using var dstStream = new FileStream(
                        tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    using var dstZip = new ZipArchive(
                        dstStream, ZipArchiveMode.Create, leaveOpen: false);

                    bool walFound = false;
                    DiagWrite($"Rebuilding ZIP: {srcZip.Entries.Count} entries, looking for {walEntryName}");
                    foreach (var srcEntry in srcZip.Entries)
                    {
                        bool isWal  = srcEntry.FullName.Equals(walEntryName,
                                          StringComparison.OrdinalIgnoreCase);
                        bool isMeta = srcEntry.FullName.Equals("Checkpoint/meta/1",
                                          StringComparison.OrdinalIgnoreCase);
                        if (isWal) walFound = true;

                        if (isWal)
                        {
                            var we = dstZip.CreateEntry(walEntryName, CompressionLevel.Optimal);
                            we.LastWriteTime = srcEntry.LastWriteTime;
                            using var ws = we.Open();
                            ws.Write(walBytes, 0, walBytes.Length);
                        }
                        else if (isMeta)
                        {
                            byte[] metaBytes = System.Text.Encoding.UTF8.GetBytes(updatedMeta);
                            var me = dstZip.CreateEntry("Checkpoint/meta/1", CompressionLevel.Optimal);
                            me.LastWriteTime = srcEntry.LastWriteTime;
                            using var ms2 = me.Open();
                            ms2.Write(metaBytes, 0, metaBytes.Length);
                        }
                        else
                        {
                            var de = dstZip.CreateEntry(srcEntry.FullName, CompressionLevel.Optimal);
                            de.LastWriteTime = srcEntry.LastWriteTime;
                            using var src = srcEntry.Open();
                            using var dst = de.Open();
                            src.CopyTo(dst);
                        }
                    }

                    if (!walFound) // Add WAL if it wasn't in the ZIP at all
                    {
                        DiagWrite("WAL not found in source ZIP — adding it");
                        var we = dstZip.CreateEntry(walEntryName, CompressionLevel.Optimal);
                        we.LastWriteTime = DateTimeOffset.UtcNow;
                        using var ws = we.Open();
                        ws.Write(walBytes, 0, walBytes.Length);
                    }
                    else
                    {
                        DiagWrite("WAL replaced in ZIP");
                    }
                } // ZipArchive flushed; FileStream closed

                DiagWrite($"Temp ZIP written: {tempPath}  size={new FileInfo(tempPath).Length}");

                // ── Step 4: atomically replace the original ZIP ────────────
                try
                {
                    File.Replace(tempPath, zipPath, null);
                    DiagWrite("File.Replace succeeded");
                }
                catch (Exception replEx)
                {
                    DiagWrite($"File.Replace failed ({replEx.Message}), trying Delete+Move");
                    File.Delete(zipPath);
                    File.Move(tempPath, zipPath);
                    DiagWrite("Delete+Move succeeded");
                }

                DiagWrite($"SUCCESS — injected WAL into: {zipPath}");
                return (null, zipPath); // success
            }
            catch (Exception ex)
            {
                DiagWrite($"EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* ignore */ }
                return ($"ZIP update failed: {ex.GetType().Name}: {ex.Message}", "");
            }
        }

        /// <summary>
        /// Updates the CRC value for <paramref name="walMetaPath"/> in a meta/1 text.
        /// Lines have the form: "private/1/XXXXXX.log crc32 &lt;value&gt;"
        /// If the line doesn't exist, appends it.
        /// </summary>
        static string UpdateMetaCrc(string metaText, string walMetaPath, uint newCrc)
        {
            var lines = metaText.Split('\n');
            bool found = false;
            for (int i = 0; i < lines.Length; i++)
            {
                // Match: "<walMetaPath> crc32 <anything>"
                if (lines[i].StartsWith(walMetaPath + " crc32 ",
                        StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = $"{walMetaPath} crc32 {newCrc}";
                    found = true;
                    break;
                }
            }
            string result = string.Join("\n", lines);
            if (!found)
            {
                // Append before the trailing newline if present
                result = result.TrimEnd('\n') + $"\n{walMetaPath} crc32 {newCrc}\n";
            }
            return result;
        }

        /// <summary>
        /// Given a RocksDB save directory, navigates up the path tree to find
        /// the adjacent RocksDB_v2_Backups folder and returns the *_Latest.zip
        /// inside it for the same player GUID.
        /// </summary>
        static string? FindLatestBackupZip(string saveDir)
        {
            // saveDir is typically:
            //   .../76561197993424152/RocksDB_v2/0.10.0/Players/<GUID>
            // Walk up until we find a directory whose parent is named RocksDB_v2.
            string playerGuid = Path.GetFileName(saveDir);
            string? current   = saveDir;

            for (int i = 0; i < 8; i++)
            {
                string? parent = Path.GetDirectoryName(current);
                if (parent == null) break;

                string parentName = Path.GetFileName(parent) ?? "";
                if (parentName.Equals("RocksDB_v2", StringComparison.OrdinalIgnoreCase))
                {
                    // grandparent is the Steam-ID folder (or equivalent root)
                    string? grandparent = Path.GetDirectoryName(parent);
                    if (grandparent == null) break;

                    string backupBase = Path.Combine(grandparent, "RocksDB_v2_Backups");
                    string playerDir  = Path.Combine(backupBase, "Players", playerGuid);
                    if (!Directory.Exists(playerDir)) break;

                    var zips = Directory.GetFiles(playerDir, "*_Latest.zip");
                    return zips.Length > 0 ? zips[0] : null;
                }
                current = parent;
            }
            return null;
        }

        // ──────────────────────────────────────────────────────────────────
        // R5BLShip CF Scanner — finds ship documents owned by a player.
        // Only SST files mapped to CF_SHIP (3) are scanned; CF_BUILDING (4)
        // SSTs are skipped to prevent ghost ships from appearing.
        // Ships use the same 40-byte key format as players (32-byte GUID +
        // 8-byte InternalKey suffix) and BSON starts with document length.
        // ──────────────────────────────────────────────────────────────────
        public static List<(string guid, byte[] bson)> ReadShipsFromSst(
            string saveDir, string playerGuid)
        {
            var result   = new List<(string, byte[])>();
            var seenGuid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            byte[] pgBytes = System.Text.Encoding.ASCII.GetBytes(
                playerGuid.ToUpperInvariant());

            // Build SST file-number → CF mapping from MANIFEST so we can restrict
            // scanning to CF_SHIP (3).  Ghost ships live in CF_BUILDING (4) SSTs and
            // would otherwise pass the player-GUID check.
            var cfMapping   = ParseManifestCfMapping(saveDir);
            bool hasMapping = cfMapping.Count > 0;

            // Newest SST files first — most current compacted data.
            // Skip SSTs that are definitively mapped to a non-ship CF.
            var ssts = Directory.GetFiles(saveDir, "*.sst")
                .Where(f =>
                {
                    if (!hasMapping) return true;   // no mapping → scan all (safe fallback)
                    string stem = Path.GetFileNameWithoutExtension(f);
                    if (!long.TryParse(stem, out long n)) return true;
                    // Include if mapped to CF_SHIP=3, or if not present in mapping at all.
                    return !cfMapping.TryGetValue(n, out int cfId) || cfId == CF_SHIP;
                })
                .OrderByDescending(f =>
                {
                    string s = Path.GetFileNameWithoutExtension(f);
                    return long.TryParse(s, out long n) ? n : 0L;
                });

            foreach (var sst in ssts)
            {
                byte[] raw;
                try { raw = File.ReadAllBytes(sst); }
                catch { continue; }

                for (int i = 0; i < raw.Length - 60; i++)
                {
                    if (raw[i] != 0x00 || raw[i + 1] != 0x28) continue;

                    int p = i + 2;
                    (long valLen, int keyStart) = ReadVarint(raw, p);
                    if (valLen < 50 || valLen > 3_000_000) continue;
                    if (keyStart + 40 + (int)valLen > raw.Length) continue;

                    // BSON length check
                    int valueStart = keyStart + 40;
                    if (BitConverter.ToInt32(raw, valueStart) != (int)valLen) continue;

                    // Must contain the PlayerId string somewhere in the BSON value
                    bool hasPlayer = false;
                    int searchEnd = Math.Min(valueStart + (int)valLen, raw.Length - pgBytes.Length);
                    for (int j = valueStart; j < searchEnd; j++)
                    {
                        bool match = true;
                        for (int k = 0; k < pgBytes.Length; k++)
                            if (raw[j + k] != pgBytes[k]) { match = false; break; }
                        if (match) { hasPlayer = true; break; }
                    }
                    if (!hasPlayer) continue;

                    // Parse BSON to confirm it's a ship doc
                    byte[] val = new byte[(int)valLen];
                    Buffer.BlockCopy(raw, valueStart, val, 0, (int)valLen);
                    try
                    {
                        var doc = BsonParser.Parse(val);
                        if (!doc.ContainsKey("ShipParams") ||
                            !doc.ContainsKey("BuildingId"))
                            continue;

                        string shipGuid = System.Text.Encoding.ASCII
                            .GetString(raw, keyStart, 32).ToUpperInvariant();

                        if (!seenGuid.Add(shipGuid)) continue; // deduplicate
                        result.Add((shipGuid, val));
                    }
                    catch { }
                }
            }
            return result;
        }

        // ──────────────────────────────────────────────────────────────────
        // Pure-C# SST Scanner — no rocksdb.dll, no decompression needed.
        // Works because the game uses kNoCompression for ALL column families.
        //
        // In an uncompressed BlockBasedTable the very first record in every
        // data block is a "restart point" and is stored verbatim:
        //
        //   [0x00]              shared_prefix_len = 0  (1 byte)
        //   [0x28]              unshared_key_len  = 40 (1 byte, <128 so single-byte varint)
        //                       (40 = 32-byte GUID + 8-byte InternalKey suffix)
        //   [<varint>]          value_len
        //   [32 bytes]          user key = ASCII player GUID
        //   [8 bytes]           sequence_number (7 bytes LE) | value_type (1 byte)
        //   [value_len bytes]   raw BSON document
        //
        // We scan every large SST file (>500 KB) for this byte pattern.
        // ──────────────────────────────────────────────────────────────────
        public static PlayerSaveData? ReadFromSstDirect(string saveDir)
        {
            string guid      = StripGuidSuffix(Path.GetFileName(saveDir));
            byte[] guidBytes = System.Text.Encoding.ASCII.GetBytes(guid);

            // Build CF mapping so we can tell which CF each SST file belongs to.
            var cfMapping = ParseManifestCfMapping(saveDir);

            // Only consider SST files large enough to hold a player BSON (~1.3 MB).
            // Sort by file number descending so we try the newest (most complete) first.
            var candidates = Directory.GetFiles(saveDir, "*.sst")
                .Where(f => new FileInfo(f).Length > 500_000)
                .OrderByDescending(f =>
                {
                    string stem = Path.GetFileNameWithoutExtension(f);
                    return long.TryParse(stem, out long n) ? n : 0L;
                });

            foreach (var sst in candidates)
            {
                // Determine the CF this SST belongs to; default to CF_PLAYER if unknown.
                int cfId = CF_PLAYER;
                string stem = Path.GetFileNameWithoutExtension(sst);
                if (long.TryParse(stem, out long fileNum) && cfMapping.TryGetValue(fileNum, out int mappedCf))
                    cfId = mappedCf;

                var result = TryScanSstDirect(sst, guidBytes, saveDir, cfId);
                if (result != null) return result;
            }
            return null;
        }

        static PlayerSaveData? TryScanSstDirect(string sstPath, byte[] guidBytes, string saveDir, int cfId)
        {
            byte[] raw;
            try { raw = File.ReadAllBytes(sstPath); }
            catch { return null; }

            if (raw.Length < 200) return null;

            for (int i = 0; i < raw.Length - guidBytes.Length - 20; i++)
            {
                // Fast pre-filter: shared_prefix_len must be 0
                if (raw[i] != 0x00) continue;

                // unshared_key_len must be 40 (32-byte GUID + 8-byte InternalKey suffix)
                int next = i + 1;
                if (next >= raw.Length || raw[next] != 0x28) continue;

                // Parse value_len varint starting at i+2
                int p = i + 2;
                if (p >= raw.Length) continue;
                (long valLen, int keyStart) = ReadVarint(raw, p);

                if (valLen < 100 || valLen > 20_000_000) continue;
                if (keyStart + 40 + (int)valLen > raw.Length) continue;

                // Verify GUID bytes match
                bool match = true;
                for (int j = 0; j < guidBytes.Length; j++)
                {
                    if (raw[keyStart + j] != guidBytes[j]) { match = false; break; }
                }
                if (!match) continue;

                // Value follows the 40-byte internal key
                int valueStart = keyStart + 40;
                byte[] val = new byte[(int)valLen];
                Buffer.BlockCopy(raw, valueStart, val, 0, (int)valLen);

                // Validate BSON: first 4 bytes = document total length
                if (val.Length >= 4 && BitConverter.ToInt32(val, 0) == val.Length)
                {
                    // Extract sequence number from the 8-byte InternalKey suffix
                    // (bytes 32-39 after the GUID key): packed as (seq << 8) | type
                    long sstSeq = 0;
                    if (keyStart + 40 <= raw.Length)
                    {
                        ulong ikey = BitConverter.ToUInt64(raw, keyStart + 32);
                        sstSeq = (long)(ikey >> 8);
                    }

                    return new PlayerSaveData
                    {
                        Sequence  = sstSeq > 0 ? sstSeq : 99999,
                        CfId      = cfId,
                        PlayerKey = guidBytes,
                        BsonBytes = val,
                        SaveDir   = saveDir
                    };
                }
            }
            return null;
        }

        // ──────────────────────────────────────────────────────────────────
        // SST Reader via rocksdb.dll (P/Invoke) — last-resort fallback.
        // Requires a compatible rocksdb.dll (same major version as the game).
        // The game uses RocksDB 10.4.2; if the bundled DLL is older this will
        // silently return null and ReadFromSstDirect should be used instead.
        // ──────────────────────────────────────────────────────────────────
        // CF name list must exactly match the game's column families in order.
        // "R5LargeObjects" was added in the game update that also moved player
        // BSON from R5BLPlayer (CF=2) to R5BLActor_BuildingBlock (CF=5).
        static readonly string[] CfNames =
            { "default", "R5LargeObjects", "R5BLPlayer", "R5BLShip", "R5BLBuilding", "R5BLActor_BuildingBlock" };

        public static PlayerSaveData? ReadFromSst(string saveDir, string? dllPath = null)
        {
            // Locate rocksdb.dll
            string? dll = dllPath ?? FindRocksDbDll(saveDir);
            if (dll == null) return null;

            IntPtr lib;
            try { lib = NativeLibrary.Load(dll); }
            catch { return null; }

            try
            {
                return ReadFromSstInternal(lib, saveDir);
            }
            finally
            {
                NativeLibrary.Free(lib);
            }
        }

        static PlayerSaveData? ReadFromSstInternal(IntPtr lib, string saveDir)
        {
            // Resolve function pointers
            if (!NativeLibrary.TryGetExport(lib, "rocksdb_options_create", out var pOpts)
             || !NativeLibrary.TryGetExport(lib, "rocksdb_readoptions_create", out var pROpts)
             || !NativeLibrary.TryGetExport(lib, "rocksdb_open_for_read_only_column_families", out var pOpen)
             || !NativeLibrary.TryGetExport(lib, "rocksdb_get_cf", out var pGet)
             || !NativeLibrary.TryGetExport(lib, "rocksdb_free", out var pFree)
             || !NativeLibrary.TryGetExport(lib, "rocksdb_close", out var pClose))
                return null;

            var fnOpts  = Marshal.GetDelegateForFunctionPointer<D_VoidReturn>(pOpts);
            var fnROpts = Marshal.GetDelegateForFunctionPointer<D_VoidReturn>(pROpts);
            var fnOpen  = Marshal.GetDelegateForFunctionPointer<D_Open>(pOpen);
            var fnGet   = Marshal.GetDelegateForFunctionPointer<D_Get>(pGet);
            var fnFree  = Marshal.GetDelegateForFunctionPointer<D_FreePtr>(pFree);
            var fnClose = Marshal.GetDelegateForFunctionPointer<D_FreePtr>(pClose);

            int n = CfNames.Length;
            IntPtr dbOpts = fnOpts();
            IntPtr rOpts  = fnROpts();

            IntPtr[] cfOptsArr = new IntPtr[n];
            for (int i = 0; i < n; i++) cfOptsArr[i] = fnOpts();

            byte[][] cfNameBytes = CfNames.Select(s => System.Text.Encoding.ASCII.GetBytes(s + "\0")).ToArray();
            IntPtr[] cfHandles   = new IntPtr[n];
            IntPtr   errPtr      = IntPtr.Zero;

            var      gcHandles  = new List<GCHandle>();
            IntPtr[] cfNamePtrs = new IntPtr[n];
            for (int i = 0; i < n; i++)
            {
                var gh = GCHandle.Alloc(cfNameBytes[i], GCHandleType.Pinned);
                gcHandles.Add(gh);
                cfNamePtrs[i] = gh.AddrOfPinnedObject();
            }

            IntPtr db = IntPtr.Zero;
            try
            {
                db = fnOpen(dbOpts, saveDir, n, cfNamePtrs, cfOptsArr, cfHandles, 0, ref errPtr);
                if (errPtr != IntPtr.Zero || db == IntPtr.Zero) return null;

                // The key is always the real player GUID (32 hex chars).
                // Folder name may have a suffix like "_copy" — strip it.
                string folderName = Path.GetFileName(saveDir);
                string guid = StripGuidSuffix(folderName);
                byte[] guidKey = System.Text.Encoding.ASCII.GetBytes(guid);

                // Try CF_ACTOR (index 5 = "R5BLActor_BuildingBlock") first — player BSON
                // moved here in the game update that also introduced R5LargeObjects.
                // Fall back to CF_PLAYER (index 2 = "R5BLPlayer") for older saves.
                byte[]? bsonBytes = null;
                int     usedCfId  = CF_ACTOR;

                bsonBytes = TryGetByKey(lib, fnGet, fnFree, db, rOpts, cfHandles[5],
                                        guidKey, ref errPtr);
                if (bsonBytes == null)
                {
                    usedCfId  = CF_PLAYER;
                    bsonBytes = TryGetByKey(lib, fnGet, fnFree, db, rOpts, cfHandles[2],
                                            guidKey, ref errPtr);
                }
                if (bsonBytes == null)
                {
                    // Direct key lookup failed — iterate CF_ACTOR then CF_PLAYER to find
                    // any valid player entry (handles key format variations).
                    usedCfId  = CF_ACTOR;
                    bsonBytes = IterateFindPlayerBson(lib, db, rOpts, cfHandles[5], out guid);
                }
                if (bsonBytes == null)
                {
                    usedCfId  = CF_PLAYER;
                    bsonBytes = IterateFindPlayerBson(lib, db, rOpts, cfHandles[2], out guid);
                }

                if (bsonBytes == null) return null;

                // Sequence is unknown when loaded via P/Invoke (no InternalKey access).
                // Save() will fall back to manifestSeq which is correct in this path.
                return new PlayerSaveData
                {
                    Sequence  = 99999,
                    CfId      = usedCfId,
                    PlayerKey = System.Text.Encoding.ASCII.GetBytes(guid),
                    BsonBytes = bsonBytes,
                    SaveDir   = saveDir
                };
            }
            catch { return null; }
            finally
            {
                if (db != IntPtr.Zero) fnClose(db);
                foreach (var gh in gcHandles) gh.Free();
            }
        }

        /// <summary>Strip non-GUID suffixes like "_copy", "_backup" from folder name.</summary>
        static string StripGuidSuffix(string folderName)
        {
            // A GUID in this game is always 32 uppercase hex chars
            if (folderName.Length == 32 && folderName.All(c =>
                (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f')))
                return folderName;
            // Try first 32 chars
            if (folderName.Length > 32)
            {
                string prefix = folderName[..32];
                if (prefix.All(c => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f')))
                    return prefix;
            }
            return folderName;
        }

        static byte[]? TryGetByKey(IntPtr lib, D_Get fnGet, D_FreePtr fnFree,
                                    IntPtr db, IntPtr rOpts, IntPtr cfHandle,
                                    byte[] key, ref IntPtr errPtr)
        {
            errPtr = IntPtr.Zero;
            IntPtr valPtr = fnGet(db, rOpts, cfHandle, key, (UIntPtr)key.Length,
                                  out UIntPtr valLen, ref errPtr);
            if (errPtr != IntPtr.Zero || valPtr == IntPtr.Zero || valLen == UIntPtr.Zero)
                return null;

            byte[] val = new byte[(int)valLen];
            Marshal.Copy(valPtr, val, 0, val.Length);
            fnFree(valPtr);

            if (val.Length < 4 || BitConverter.ToInt32(val, 0) != val.Length)
                return null;
            return val;
        }

        static byte[]? IterateFindPlayerBson(IntPtr lib, IntPtr db, IntPtr rOpts, IntPtr cfHandle,
                                              out string foundGuid)
        {
            foundGuid = "";
            try
            {
                if (!NativeLibrary.TryGetExport(lib, "rocksdb_create_iterator_cf", out var pIter)
                 || !NativeLibrary.TryGetExport(lib, "rocksdb_iter_seek_to_first",  out var pSeek)
                 || !NativeLibrary.TryGetExport(lib, "rocksdb_iter_valid",           out var pValid)
                 || !NativeLibrary.TryGetExport(lib, "rocksdb_iter_key",             out var pKey)
                 || !NativeLibrary.TryGetExport(lib, "rocksdb_iter_value",           out var pVal)
                 || !NativeLibrary.TryGetExport(lib, "rocksdb_iter_next",            out var pNext)
                 || !NativeLibrary.TryGetExport(lib, "rocksdb_iter_destroy",         out var pDestroy))
                    return null;

                var fnIter    = Marshal.GetDelegateForFunctionPointer<D_IterCreate>(pIter);
                var fnSeek    = Marshal.GetDelegateForFunctionPointer<D_IterVoid>(pSeek);
                var fnValid   = Marshal.GetDelegateForFunctionPointer<D_IterBool>(pValid);
                var fnKey     = Marshal.GetDelegateForFunctionPointer<D_IterData>(pKey);
                var fnVal     = Marshal.GetDelegateForFunctionPointer<D_IterData>(pVal);
                var fnNext    = Marshal.GetDelegateForFunctionPointer<D_IterVoid>(pNext);
                var fnDestroy = Marshal.GetDelegateForFunctionPointer<D_IterVoid>(pDestroy);

                IntPtr it = fnIter(db, rOpts, cfHandle);
                fnSeek(it);
                try
                {
                    while (fnValid(it) != 0)
                    {
                        UIntPtr klen = UIntPtr.Zero, vlen = UIntPtr.Zero;
                        IntPtr kptr = fnKey(it, ref klen);
                        IntPtr vptr = fnVal(it, ref vlen);

                        int kl = (int)klen, vl = (int)vlen;
                        if (kl == 32 && vl > 1000)
                        {
                            byte[] kbuf = new byte[kl]; Marshal.Copy(kptr, kbuf, 0, kl);
                            byte[] vbuf = new byte[vl]; Marshal.Copy(vptr, vbuf, 0, vl);
                            if (BitConverter.ToInt32(vbuf, 0) == vl)
                            {
                                foundGuid = System.Text.Encoding.ASCII.GetString(kbuf);
                                return vbuf;
                            }
                        }
                        fnNext(it);
                    }
                }
                finally { fnDestroy(it); }
            }
            catch { }
            return null;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr D_IterCreate(IntPtr db, IntPtr rOpts, IntPtr cfHandle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void D_IterVoid(IntPtr it);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate byte D_IterBool(IntPtr it);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr D_IterData(IntPtr it, ref UIntPtr len);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr D_VoidReturn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr D_Open(IntPtr opts, string path, int numCf,
                               IntPtr[] cfNames, IntPtr[] cfOpts, IntPtr[] cfHandles,
                               byte errorIfLogFileExists, ref IntPtr err);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr D_Get(IntPtr db, IntPtr rOpts, IntPtr cfHandle,
                              byte[] key, UIntPtr keyLen,
                              out UIntPtr valLen, ref IntPtr err);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void D_FreePtr(IntPtr ptr);

        // ══════════════════════════════════════════════════════════════════════
        // WriteDirectAndCheckpoint  —  the reliable save path.
        //
        // WAL injection is ignored by the game: it restores SSTs from the ZIP
        // on every startup, discarding any WAL.  This method writes the BSON
        // directly into the live RocksDB via the DLL (put_cf → flush_cf),
        // then creates a RocksDB checkpoint and packages it as _Latest.zip.
        // The game restores that ZIP on next launch and sees the changes.
        // ══════════════════════════════════════════════════════════════════════

        public static (string? error, string? zipPath) WriteDirectAndCheckpoint(
            string saveDir, List<WalEntry> entries, string? dllPath = null)
        {
            string? dll = dllPath ?? FindRocksDbDll(saveDir);
            if (dll == null) return ("rocksdb.dll not found — cannot write directly to DB", null);

            IntPtr lib;
            try { lib = NativeLibrary.Load(dll); }
            catch (Exception ex) { return ($"Cannot load rocksdb.dll: {ex.Message}", null); }

            try   { return WriteDirectInternal(lib, saveDir, entries); }
            finally { NativeLibrary.Free(lib); }
        }

        static (string? error, string? zipPath) WriteDirectInternal(
            IntPtr lib, string saveDir, List<WalEntry> entries)
        {
            // ── resolve all required exports ─────────────────────────────────
            if (!NativeLibrary.TryGetExport(lib, "rocksdb_options_create",               out var pOptsCreate)
             || !NativeLibrary.TryGetExport(lib, "rocksdb_options_destroy",              out var pOptsDestroy)
             || !NativeLibrary.TryGetExport(lib, "rocksdb_open_column_families",         out var pOpen)
             || !NativeLibrary.TryGetExport(lib, "rocksdb_writeoptions_create",          out var pWOCreate)
             || !NativeLibrary.TryGetExport(lib, "rocksdb_writeoptions_destroy",         out var pWODestroy)
             || !NativeLibrary.TryGetExport(lib, "rocksdb_put_cf",                       out var pPutCf)
             || !NativeLibrary.TryGetExport(lib, "rocksdb_delete_cf",                    out var pDelCf)
             || !NativeLibrary.TryGetExport(lib, "rocksdb_flushoptions_create",          out var pFOCreate)
             || !NativeLibrary.TryGetExport(lib, "rocksdb_flushoptions_set_wait",        out var pFOSetWait)
             || !NativeLibrary.TryGetExport(lib, "rocksdb_flushoptions_destroy",         out var pFODestroy)
             || !NativeLibrary.TryGetExport(lib, "rocksdb_flush_cf",                     out var pFlushCf)
             || !NativeLibrary.TryGetExport(lib, "rocksdb_checkpoint_object_create",     out var pCkptNew)
             || !NativeLibrary.TryGetExport(lib, "rocksdb_checkpoint_create",            out var pCkptDo)
             || !NativeLibrary.TryGetExport(lib, "rocksdb_checkpoint_object_destroy",    out var pCkptFree)
             || !NativeLibrary.TryGetExport(lib, "rocksdb_column_family_handle_destroy", out var pCfFree)
             || !NativeLibrary.TryGetExport(lib, "rocksdb_close",                        out var pClose))
                return ("rocksdb.dll is missing required exports for direct write", null);

            var fnOptsCreate  = Marshal.GetDelegateForFunctionPointer<D_VoidReturn>(pOptsCreate);
            var fnOptsDestroy = Marshal.GetDelegateForFunctionPointer<D_FreePtr>(pOptsDestroy);
            var fnOpen        = Marshal.GetDelegateForFunctionPointer<D_OpenRW>(pOpen);
            var fnWOCreate    = Marshal.GetDelegateForFunctionPointer<D_VoidReturn>(pWOCreate);
            var fnWODestroy   = Marshal.GetDelegateForFunctionPointer<D_FreePtr>(pWODestroy);
            var fnPutCf       = Marshal.GetDelegateForFunctionPointer<D_PutCf>(pPutCf);
            var fnDelCf       = Marshal.GetDelegateForFunctionPointer<D_DelCf>(pDelCf);
            var fnFOCreate    = Marshal.GetDelegateForFunctionPointer<D_VoidReturn>(pFOCreate);
            var fnFOSetWait   = Marshal.GetDelegateForFunctionPointer<D_SetWait>(pFOSetWait);
            var fnFODestroy   = Marshal.GetDelegateForFunctionPointer<D_FreePtr>(pFODestroy);
            var fnFlushCf     = Marshal.GetDelegateForFunctionPointer<D_FlushCf>(pFlushCf);
            var fnCkptNew     = Marshal.GetDelegateForFunctionPointer<D_CkptCreate>(pCkptNew);
            var fnCkptDo      = Marshal.GetDelegateForFunctionPointer<D_CkptDo>(pCkptDo);
            var fnCkptFree    = Marshal.GetDelegateForFunctionPointer<D_FreePtr>(pCkptFree);
            var fnCfFree      = Marshal.GetDelegateForFunctionPointer<D_FreePtr>(pCfFree);
            var fnClose       = Marshal.GetDelegateForFunctionPointer<D_FreePtr>(pClose);

            int n = CfNames.Length;
            IntPtr dbOpts  = fnOptsCreate();
            IntPtr[] cfOptsArr = new IntPtr[n];
            for (int i = 0; i < n; i++) cfOptsArr[i] = fnOptsCreate();

            // Force kNoCompression (value=0) on all CF options so flushed SST blocks
            // use type byte 0x00 — readable by the game's RocksDB without needing Snappy.
            if (NativeLibrary.TryGetExport(lib, "rocksdb_options_set_compression", out var pSetComp))
            {
                var fnSetComp = Marshal.GetDelegateForFunctionPointer<D_SetCompression>(pSetComp);
                for (int i = 0; i < n; i++)
                    fnSetComp(cfOptsArr[i], 0); // 0 = kNoCompression
            }

            byte[][] cfNameBytes = CfNames.Select(s => System.Text.Encoding.ASCII.GetBytes(s + "\0")).ToArray();
            IntPtr[] cfHandles   = new IntPtr[n];
            IntPtr   errPtr      = IntPtr.Zero;

            var     gcHandles  = new List<GCHandle>();
            IntPtr[] cfNamePtrs = new IntPtr[n];
            for (int i = 0; i < n; i++)
            {
                var gh = GCHandle.Alloc(cfNameBytes[i], GCHandleType.Pinned);
                gcHandles.Add(gh);
                cfNamePtrs[i] = gh.AddrOfPinnedObject();
            }

            IntPtr db    = IntPtr.Zero;
            IntPtr wOpts = IntPtr.Zero;
            IntPtr fOpts = IntPtr.Zero;
            string ckptDir = Path.Combine(Path.GetTempPath(), "windrose_ckpt_direct");

            try
            {
                // ── open read-write ──────────────────────────────────────────
                db = fnOpen(dbOpts, saveDir, n, cfNamePtrs, cfOptsArr, cfHandles, ref errPtr);
                if (errPtr != IntPtr.Zero || db == IntPtr.Zero)
                {
                    string msg = errPtr != IntPtr.Zero
                        ? Marshal.PtrToStringAnsi(errPtr) ?? "unknown"
                        : "null DB handle";
                    return ($"DB open (RW) failed: {msg}\n\nМакс. вероятная причина: игра запущена. Закройте игру и повторите.", null);
                }

                // ── write entries ────────────────────────────────────────────
                wOpts = fnWOCreate();
                var modifiedCfs = new HashSet<int>();

                foreach (var entry in entries)
                {
                    if (entry.CfId < 0 || entry.CfId >= n) continue;
                    IntPtr cfh = cfHandles[entry.CfId];
                    errPtr = IntPtr.Zero;

                    if (entry.Value != null)
                        fnPutCf(db, wOpts, cfh, entry.Key, (UIntPtr)entry.Key.Length,
                                entry.Value, (UIntPtr)entry.Value.Length, ref errPtr);
                    else
                        fnDelCf(db, wOpts, cfh, entry.Key, (UIntPtr)entry.Key.Length, ref errPtr);

                    if (errPtr != IntPtr.Zero)
                    {
                        string msg = Marshal.PtrToStringAnsi(errPtr) ?? "unknown";
                        return ($"Write to CF{entry.CfId} failed: {msg}", null);
                    }
                    modifiedCfs.Add(entry.CfId);
                }

                // ── flush all touched CFs → new SST with correct XXH3 sums ─
                fOpts = fnFOCreate();
                fnFOSetWait(fOpts, 1);   // wait = true

                foreach (int cfIdx in modifiedCfs)
                {
                    errPtr = IntPtr.Zero;
                    fnFlushCf(db, fOpts, cfHandles[cfIdx], ref errPtr);
                    if (errPtr != IntPtr.Zero)
                    {
                        string msg = Marshal.PtrToStringAnsi(errPtr) ?? "unknown";
                        return ($"Flush CF{cfIdx} failed: {msg}", null);
                    }
                }

                // ── create checkpoint ────────────────────────────────────────
                if (Directory.Exists(ckptDir))
                    Directory.Delete(ckptDir, recursive: true);

                errPtr = IntPtr.Zero;
                IntPtr ckptObj = fnCkptNew(db, ref errPtr);
                if (errPtr != IntPtr.Zero || ckptObj == IntPtr.Zero)
                {
                    string msg = errPtr != IntPtr.Zero
                        ? Marshal.PtrToStringAnsi(errPtr) ?? "unknown" : "null";
                    return ($"Checkpoint object create failed: {msg}", null);
                }

                try
                {
                    errPtr = IntPtr.Zero;
                    fnCkptDo(ckptObj, ckptDir, logSizeForFlush: 0, ref errPtr);
                    if (errPtr != IntPtr.Zero)
                    {
                        string msg = Marshal.PtrToStringAnsi(errPtr) ?? "unknown";
                        return ($"Checkpoint create failed: {msg}", null);
                    }
                }
                finally { fnCkptFree(ckptObj); }

                // ── close DB before touching files ───────────────────────────
                for (int i = 0; i < n; i++)
                    if (cfHandles[i] != IntPtr.Zero) { fnCfFree(cfHandles[i]); cfHandles[i] = IntPtr.Zero; }
                fnWODestroy(wOpts); wOpts = IntPtr.Zero;
                fnFODestroy(fOpts); fOpts = IntPtr.Zero;
                fnClose(db);       db    = IntPtr.Zero;
                fnOptsDestroy(dbOpts); dbOpts = IntPtr.Zero;

                // ── package checkpoint as _Latest.zip ────────────────────────
                return PackageCheckpointAsZip(saveDir, ckptDir);
            }
            catch (Exception ex)
            {
                return ($"WriteDirectInternal exception: {ex}", null);
            }
            finally
            {
                // Safety cleanup in case of early exit
                if (db != IntPtr.Zero)
                {
                    for (int i = 0; i < n; i++)
                        if (cfHandles[i] != IntPtr.Zero) fnCfFree(cfHandles[i]);
                    if (wOpts != IntPtr.Zero) fnWODestroy(wOpts);
                    if (fOpts != IntPtr.Zero) fnFODestroy(fOpts);
                    fnClose(db);
                }
                if (dbOpts != IntPtr.Zero) fnOptsDestroy(dbOpts);
                foreach (var gh in gcHandles) gh.Free();
                if (Directory.Exists(ckptDir))
                    try { Directory.Delete(ckptDir, recursive: true); } catch { /* ignore */ }
            }
        }

        /// <summary>
        /// Packs the RocksDB checkpoint directory into a _Latest.zip that the game's
        /// backup engine can restore.  SSTs go under Checkpoint/shared_checksum/ with
        /// a synthetic session-ID suffix; private files go under Checkpoint/private/1/.
        /// meta/1 lists every file with its CRC32C so the engine can verify integrity.
        /// </summary>
        static (string? error, string? zipPath) PackageCheckpointAsZip(string saveDir, string ckptDir)
        {
            string? zipPath = FindLatestBackupZip(saveDir);
            if (zipPath == null)
                return ("Could not locate _Latest.zip backup file", null);

            // Read old counter from existing ZIP (increment to signal a newer backup)
            long oldCounter = 1_777_936_868L;
            if (File.Exists(zipPath))
            {
                try
                {
                    using var zfOld = new ZipArchive(File.OpenRead(zipPath), ZipArchiveMode.Read);
                    var oldMeta = zfOld.GetEntry("Checkpoint/meta/1");
                    if (oldMeta != null)
                    {
                        using var sr = new StreamReader(oldMeta.Open());
                        long.TryParse(sr.ReadLine()?.Trim(), out oldCounter);
                    }
                }
                catch { /* use default counter */ }
            }

            // Enumerate checkpoint files
            string[] files = Directory.GetFiles(ckptDir);

            // Parse lastSeq from the MANIFEST in the checkpoint
            long lastSeq = 0;
            foreach (string f in files)
            {
                if (Path.GetFileName(f).StartsWith("MANIFEST-", StringComparison.Ordinal))
                {
                    var (ls, _, _) = ParseManifestBytes(File.ReadAllBytes(f));
                    lastSeq = ls;
                    break;
                }
            }

            // Build entry list: (zip path, meta-relative path, crc32c, data)
            var zipEntries = new List<(string ZipPath, string MetaRel, uint Crc, byte[] Data)>();

            foreach (string fpath in files)
            {
                string fname = Path.GetFileName(fpath);
                byte[] data  = File.ReadAllBytes(fpath);
                uint   crc   = Crc32C(data, 0, data.Length);
                int    sz    = data.Length;

                string zipPath2, metaRel;
                if (fname.EndsWith(".sst", StringComparison.OrdinalIgnoreCase))
                {
                    string numStr = Path.GetFileNameWithoutExtension(fname);
                    uint.TryParse(numStr, out uint fileNum);
                    string sid   = MakeSessionId(crc, fileNum);
                    string entry = $"{numStr}_s{sid}_{sz}.sst";
                    zipPath2 = $"Checkpoint/shared_checksum/{entry}";
                    metaRel  = $"shared_checksum/{entry}";
                }
                else if (fname.EndsWith(".blob", StringComparison.OrdinalIgnoreCase))
                {
                    string numStr = Path.GetFileNameWithoutExtension(fname);
                    string entry  = $"{numStr}_{crc}_{sz}.blob";
                    zipPath2 = $"Checkpoint/shared_checksum/{entry}";
                    metaRel  = $"shared_checksum/{entry}";
                }
                else
                {
                    // MANIFEST, CURRENT, OPTIONS, WAL (.log)
                    zipPath2 = $"Checkpoint/private/1/{fname}";
                    metaRel  = $"private/1/{fname}";
                }
                zipEntries.Add((zipPath2, metaRel, crc, data));
            }

            // Build meta/1 content
            long newCounter = oldCounter + 1;
            var sb = new System.Text.StringBuilder();
            sb.Append(newCounter).Append('\n');
            sb.Append(lastSeq).Append('\n');
            sb.Append(zipEntries.Count).Append('\n');
            foreach (var (_, metaRel, crc, _) in zipEntries)
                sb.Append(metaRel).Append(" crc32 ").Append(crc).Append('\n');
            byte[] metaBytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());

            // Write new ZIP
            string tempZip = zipPath + ".tmp";
            try
            {
                using (var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write))
                using (var zf = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false,
                                               System.Text.Encoding.UTF8))
                {
                    WriteZipEntry(zf, "Checkpoint/meta/1", metaBytes);
                    foreach (var (zp, _, _, data) in zipEntries)
                        WriteZipEntry(zf, zp, data);
                }

                File.Replace(tempZip, zipPath, null);
                return (null, zipPath);
            }
            catch (Exception ex)
            {
                try { File.Delete(tempZip); } catch { /* ignore */ }
                return ($"ZIP packaging failed: {ex.Message}", null);
            }
        }

        static void WriteZipEntry(ZipArchive zf, string entryName, byte[] data)
        {
            var entry = zf.CreateEntry(entryName, CompressionLevel.Optimal);
            using var s = entry.Open();
            s.Write(data, 0, data.Length);
        }

        /// <summary>
        /// 20-char uppercase alphanumeric session-ID string for shared_checksum SST naming.
        /// The value is synthetic (not the real RocksDB session ID) but is consistent for
        /// a given (crc32c, fileNumber) pair.  The game's backup engine only verifies the
        /// CRC32C stored in meta/1 — the session string is used purely as a unique filename.
        /// </summary>
        static string MakeSessionId(uint crc32cVal, uint fileNum)
        {
            const string Chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            ulong val = ((ulong)crc32cVal << 32) | fileNum;
            char[] result = new char[20];
            for (int i = 19; i >= 0; i--)
            {
                result[i] = Chars[(int)(val % 36)];
                val /= 36;
            }
            return new string(result);
        }

        // ── New delegates for read-write operations ────────────────────────

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr D_OpenRW(IntPtr opts, string path, int numCf,
                                  IntPtr[] cfNames, IntPtr[] cfOpts, IntPtr[] cfHandles,
                                  ref IntPtr err);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void D_PutCf(IntPtr db, IntPtr wOpts, IntPtr cfHandle,
                              byte[] key, UIntPtr keyLen,
                              byte[] val, UIntPtr valLen, ref IntPtr err);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void D_DelCf(IntPtr db, IntPtr wOpts, IntPtr cfHandle,
                              byte[] key, UIntPtr keyLen, ref IntPtr err);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void D_SetWait(IntPtr opts, byte wait);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void D_SetCompression(IntPtr opts, int compressionType);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void D_FlushCf(IntPtr db, IntPtr fOpts, IntPtr cfHandle, ref IntPtr err);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr D_CkptCreate(IntPtr db, ref IntPtr err);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void D_CkptDo(IntPtr ckpt, string dir, ulong logSizeForFlush, ref IntPtr err);

        static string? FindRocksDbDll(string saveDir)
        {
            string exe = AppDomain.CurrentDomain.BaseDirectory;

            // When published as single-file with IncludeNativeLibrariesForSelfExtract=true,
            // the runtime extracts native DLLs to a temp directory before app code runs.
            // The path is exposed via AppContext data or the environment variable.
            string? extractDir = AppContext.GetData("DOTNET_BUNDLE_EXTRACT_BASE_DIR") as string
                              ?? Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR");

            var candidates = new List<string>
            {
                Path.Combine(exe, "rocksdb.dll"),
                Path.Combine(exe, "..", "rocksdb.dll"),
                Path.Combine(saveDir, "rocksdb.dll"),
            };

            if (extractDir != null)
                candidates.Insert(0, Path.Combine(extractDir, "rocksdb.dll"));

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            return null;
        }
    }
}
