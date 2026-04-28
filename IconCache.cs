using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WindroseEditor
{
    /// <summary>
    /// Загружает и кэширует PNG-иконки предметов.
    ///
    /// Приоритет источников:
    ///   1. windrose_items.db  — таблица icons (BLOB), загружается лениво
    ///   2. icon_map.json      — карта iconRef → путь к файлу PNG
    ///   3. Папка icons/       — прямой поиск по пути
    /// </summary>
    public static class IconCache
    {
        // ── File-system fallback ─────────────────────────────────────────
        static string _iconDir = "";
        static Dictionary<string, string> _map = new();   // iconRef → путь к файлу

        // ── SQLite source ─────────────────────────────────────────────────
        static SqliteConnection? _conn;
        static readonly HashSet<string> _sqliteRefs =
            new(StringComparer.OrdinalIgnoreCase);     // icon_refs, присутствующие в БД

        // ── Memory cache ──────────────────────────────────────────────────
        static readonly Dictionary<string, Image?> _cache = new();

        // ── Статистика ────────────────────────────────────────────────────
        public static bool HasIcons =>
            _sqliteRefs.Count > 0 || _map.Count > 0 || !string.IsNullOrEmpty(_iconDir);

        public static int MappedCount =>
            _sqliteRefs.Count > 0 ? _sqliteRefs.Count : _map.Count;

        // ─────────────────────────────────────────────────────────────────
        // Инициализация из windrose_items.db
        // (устанавливает постоянное соединение только для чтения)
        // ─────────────────────────────────────────────────────────────────
        public static void LoadFromSqlite(string dbPath)
        {
            // Сбрасываем всё предыдущее состояние
            _conn?.Close();
            _conn = null;
            _sqliteRefs.Clear();
            _cache.Clear();
            _map.Clear();
            _iconDir = "";

            if (!File.Exists(dbPath)) return;

            try
            {
                // Держим соединение открытым — иконки грузим лениво по запросу
                _conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                _conn.Open();

                // Предзагружаем только ключи (не сами BLOB-ы)
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT icon_ref FROM icons";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    _sqliteRefs.Add(reader.GetString(0));
            }
            catch
            {
                _conn?.Close();
                _conn = null;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Инициализация из файловой системы
        // (используется при загрузке icon_map.json / папки icons/)
        // ─────────────────────────────────────────────────────────────────
        public static void Load(string editorBaseDir)
        {
            // Сбрасываем SQLite-соединение (переключаемся на файловую систему)
            _conn?.Close();
            _conn = null;
            _sqliteRefs.Clear();
            _cache.Clear();
            _map.Clear();

            _iconDir = Path.Combine(editorBaseDir, "icons");

            string mapPath = Path.Combine(editorBaseDir, "icon_map.json");
            if (File.Exists(mapPath))
            {
                try
                {
                    string json = File.ReadAllText(mapPath);
                    using var doc = JsonDocument.Parse(json);
                    foreach (var kv in doc.RootElement.EnumerateObject())
                    {
                        string? val = kv.Value.GetString();
                        if (val != null)
                            _map[kv.Name] = Path.Combine(
                                editorBaseDir,
                                val.Replace('/', Path.DirectorySeparatorChar));
                    }
                }
                catch { /* игнорируем */ }
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Получение иконки (с кэшированием)
        // ─────────────────────────────────────────────────────────────────
        public static Image? Get(string iconRef)
        {
            if (string.IsNullOrEmpty(iconRef)) return null;
            if (_cache.TryGetValue(iconRef, out var cached)) return cached;

            Image? img = null;

            // 1. SQLite BLOB
            if (_conn != null && _sqliteRefs.Contains(iconRef))
            {
                try
                {
                    using var cmd = _conn.CreateCommand();
                    cmd.CommandText = "SELECT png_data FROM icons WHERE icon_ref = @ref LIMIT 1";
                    cmd.Parameters.AddWithValue("@ref", iconRef);
                    using var reader = cmd.ExecuteReader();
                    if (reader.Read() && !reader.IsDBNull(0))
                    {
                        byte[] blob = reader.GetFieldValue<byte[]>(0);
                        using var ms = new MemoryStream(blob);
                        img = Image.FromStream(ms);
                    }
                }
                catch { img = null; }
            }

            // 2. icon_map.json / папка icons/
            if (img == null)
            {
                string? path = ResolveFilePath(iconRef);
                if (path != null && File.Exists(path))
                {
                    try
                    {
                        using var ms = new MemoryStream(File.ReadAllBytes(path));
                        img = Image.FromStream(ms);
                    }
                    catch { img = null; }
                }
            }

            _cache[iconRef] = img;
            return img;
        }

        // ─────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────
        static string? ResolveFilePath(string iconRef)
        {
            if (_map.TryGetValue(iconRef, out var mapped)) return mapped;

            if (!string.IsNullOrEmpty(_iconDir))
            {
                string rel  = iconRef.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                string path = Path.Combine(_iconDir, rel + ".png");
                if (File.Exists(path)) return path;

                string noGame = rel.StartsWith("Game" + Path.DirectorySeparatorChar)
                    ? rel[(4 + 1)..] : rel;
                string path2 = Path.Combine(_iconDir, noGame + ".png");
                if (File.Exists(path2)) return path2;
            }

            return null;
        }
    }
}
