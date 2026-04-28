using System;
using System.Collections.Generic;
using System.IO;
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
        public const int CF_PLAYER   = 2;   // R5BLPlayer
        public const int CF_BUILDING = 4;   // R5BLBuilding (ships)

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

                            // Player record: CF=2, 32-byte GUID key, valid BSON
                            if (cfId == 2 && keyLen == 32 && valLen > 1000
                                && val.Length >= 4
                                && BitConverter.ToInt32(val, 0) == valLen)
                            {
                                lastKey   = key;
                                lastBson  = val;
                                playerSeq = batchSeq;
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
                CfId      = 2,
                PlayerKey = lastKey,
                BsonBytes = lastBson,
                SaveDir   = saveDir
            };
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
                        else p++;
                    }
                    catch { p++; }
                }
            }
            return (lastSeq, nextFileNum, logNum);
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

            // Build WriteBatch payload
            using var batch = new MemoryStream();
            batch.Write(BitConverter.GetBytes(seq),          0, 8); // sequence
            batch.Write(BitConverter.GetBytes(entries.Count), 0, 4); // count

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

            return WriteWalBytes(newPath, batch.ToArray());
        }

        // Backward-compat single-entry wrapper
        public static bool WriteWal(string saveDir, long seq, long fileNum, int cfId,
                                     byte[] key, byte[] bsonBytes)
            => WriteWalMulti(saveDir, seq, fileNum,
                             new[] { new WalEntry(cfId, key, bsonBytes) });

        static bool WriteWalBytes(string path, byte[] batchArr)
        {
            // Fragment into 32KB blocks with 7-byte headers (crc:4, len:2, type:1)
            const int MaxData = BlockSize - 7;
            using var output = new MemoryStream();
            int offset = 0, total = batchArr.Length;

            while (offset < total)
            {
                int chunkLen = Math.Min(MaxData, total - offset);
                bool isFirst = (offset == 0);
                bool isLast  = (offset + chunkLen >= total);
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

            try { File.WriteAllBytes(path, output.ToArray()); return true; }
            catch { return false; }
        }

        // ──────────────────────────────────────────────────────────────────
        // R5BLBuilding CF Scanner — finds ship documents owned by a player.
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

            // Newest SST files first — most current compacted data
            var ssts = Directory.GetFiles(saveDir, "*.sst")
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

                    // Must contain the PlayerId string somewhere in first 300 bytes
                    bool hasPlayer = false;
                    int searchEnd = Math.Min(valueStart + 300, raw.Length - pgBytes.Length);
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
                var result = TryScanSstDirect(sst, guidBytes, saveDir);
                if (result != null) return result;
            }
            return null;
        }

        static PlayerSaveData? TryScanSstDirect(string sstPath, byte[] guidBytes, string saveDir)
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
                    return new PlayerSaveData
                    {
                        Sequence  = 99999,
                        CfId      = 2,
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
        static readonly string[] CfNames =
            { "default", "R5BLPlayer", "R5BLShip", "R5BLBuilding", "R5BLActor_BuildingBlock" };

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

                // CfNames[1] = "R5BLPlayer" (index 1 after removing R5LargeObjects)
                byte[]? bsonBytes = TryGetByKey(lib, fnGet, fnFree, db, rOpts, cfHandles[1],
                                                System.Text.Encoding.ASCII.GetBytes(guid),
                                                ref errPtr);

                if (bsonBytes == null)
                {
                    // Key not found — iterate the R5BLPlayer CF to find any valid player entry
                    bsonBytes = IterateFindPlayerBson(lib, db, rOpts, cfHandles[1],
                                                      out guid);
                }

                if (bsonBytes == null) return null;

                return new PlayerSaveData
                {
                    Sequence  = 99999,
                    CfId      = 2,
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

        static string? FindRocksDbDll(string saveDir)
        {
            string exe = AppDomain.CurrentDomain.BaseDirectory;
            foreach (string candidate in new[]
            {
                Path.Combine(exe, "rocksdb.dll"),
                Path.Combine(exe, "..", "rocksdb.dll"),
                Path.Combine(saveDir, "rocksdb.dll"),
            })
            {
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            return null;
        }
    }
}
