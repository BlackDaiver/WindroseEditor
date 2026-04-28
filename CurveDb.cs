using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WindroseEditor
{
    /// <summary>
    /// Загружает curves_db.json и предоставляет интерполяцию значений кривых.
    /// curves_db.json формат: { "CT_path": { "RowName": [[time,value],...] } }
    /// </summary>
    public static class CurveDb
    {
        // Внешний ключ — полный CT-путь как в JSON предмета
        // Внутренний — имя строки
        static Dictionary<string, Dictionary<string, float[][]>> _tables = new();

        public static bool Loaded { get; private set; }
        public static int  TableCount { get; private set; }
        public static int  RowCount   { get; private set; }

        // ── SQLite (из windrose_items.db) ──────────────────────────────────────
        public static string LoadFromSqlite(string dbPath)
        {
            if (!File.Exists(dbPath)) return "";  // не ошибка — CT необязательны

            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                conn.Open();

                // Проверяем что таблица curve_data существует
                using (var chk = conn.CreateCommand())
                {
                    chk.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='curve_data'";
                    if ((long)(chk.ExecuteScalar() ?? 0L) == 0) return "";
                }

                var tables = new Dictionary<string, Dictionary<string, float[][]>>(StringComparer.OrdinalIgnoreCase);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT table_path, row_name, points FROM curve_data";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string tablePath = r.GetString(0);
                    string rowName   = r.GetString(1);
                    byte[] blob      = r.GetFieldValue<byte[]>(2);
                    float[][] pts    = UnpackCurvePoints(blob);

                    if (!tables.TryGetValue(tablePath, out var rows))
                        tables[tablePath] = rows = new Dictionary<string, float[][]>(StringComparer.Ordinal);
                    rows[rowName] = pts;
                }

                _tables    = tables;
                TableCount = tables.Count;
                RowCount   = 0;
                foreach (var tbl in tables.Values) RowCount += tbl.Count;
                Loaded     = TableCount > 0;
                return "";
            }
            catch (Exception ex)
            {
                return $"Ошибка загрузки кривых из SQLite: {ex.Message}";
            }
        }

        // BLOB (N × 8 байт: float32 time + float32 value) → float[][]
        static float[][] UnpackCurvePoints(byte[] blob)
        {
            int count = blob.Length / 8;
            var result = new float[count][];
            for (int i = 0; i < count; i++)
            {
                result[i] = new float[2]
                {
                    BitConverter.ToSingle(blob, i * 8),
                    BitConverter.ToSingle(blob, i * 8 + 4),
                };
            }
            return result;
        }

        // ── JSON (curves_db.json, legacy) ──────────────────────────────────────
        public static string Load(string curvesPath)
        {
            if (!File.Exists(curvesPath))
                return "";   // не ошибка — CT файлы необязательны

            try
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var data = JsonSerializer.Deserialize<
                    Dictionary<string, Dictionary<string, float[][]>>>(
                    File.ReadAllText(curvesPath, System.Text.Encoding.UTF8), opts);

                _tables     = data ?? new();
                TableCount  = _tables.Count;
                RowCount    = 0;
                foreach (var tbl in _tables.Values) RowCount += tbl.Count;
                Loaded      = TableCount > 0;
                return "";
            }
            catch (Exception ex)
            {
                return $"Ошибка загрузки curves_db.json: {ex.Message}";
            }
        }

        /// <summary>
        /// Возвращает значение кривой по таблице, строке и уровню (времени).
        /// При CurveLevel == 0 используйте фактический уровень предмета.
        /// Возвращает null, если таблица/строка не найдена.
        /// </summary>
        public static float? GetValue(string tablePath, string rowName, float level)
        {
            if (!_tables.TryGetValue(tablePath, out var rows)) return null;
            if (!rows.TryGetValue(rowName, out var keys))      return null;
            if (keys == null || keys.Length == 0)              return null;

            // Одна точка — константа
            if (keys.Length == 1) return keys[0][1];

            // Ищем точные совпадения и интерполируем
            for (int i = 0; i < keys.Length - 1; i++)
            {
                float t0 = keys[i][0],     v0 = keys[i][1];
                float t1 = keys[i + 1][0], v1 = keys[i + 1][1];

                if (level <= t0) return v0;

                if (level < t1)
                {
                    // Линейная интерполяция
                    float alpha = (level - t0) / (t1 - t0);
                    return v0 + alpha * (v1 - v0);
                }
            }

            // За пределами кривой — последнее значение
            return keys[^1][1];
        }

        // ── Форматирование значений ────────────────────────────────────────────

        /// <summary>
        /// Форматирует числовое значение согласно DisplayType.
        /// </summary>
        public static string FormatValue(float value, string displayType, bool inverse = false)
        {
            if (inverse) value = -value;

            return displayType switch
            {
                "SecondsAsMinutes" => FormatDuration(value),
                "RatioToPercent"   => $"{value * 100f:0.#}%",
                "ValueToPercent"   => $"{value * 100f:+0.#;-0.#;0}%",
                "ValueAsValue"     => FormatNumber(value),
                _                  => FormatNumber(value),
            };
        }

        public static string FormatNumber(float v)
        {
            if (Math.Abs(v - Math.Round(v)) < 0.005f)
                return ((int)Math.Round(v)).ToString();
            return v.ToString("0.##");
        }

        static string FormatDuration(float seconds)
        {
            string hu = AppLanguage.T("ч.",   "h");
            string mu = AppLanguage.T("мин.", "min");
            string su = AppLanguage.T("сек.", "sec");

            if (seconds >= 3600)
            {
                float h = seconds / 3600f;
                return h == Math.Floor(h) ? $"{(int)h} {hu}" : $"{h:0.#} {hu}";
            }
            if (seconds >= 60)
            {
                float m = seconds / 60f;
                return m == Math.Floor(m) ? $"{(int)m} {mu}" : $"{m:0.#} {mu}";
            }
            return $"{(int)seconds} {su}";
        }

        // ── Отображаемые названия характеристик ───────────────────────────────
        public static string StatDisplayName(string stat) =>
            AppLanguage.IsRu ? StatDisplayNameRu(stat) : StatDisplayNameEn(stat);

        static string StatDisplayNameRu(string stat) => stat switch
        {
            "Vitality"             => "Живучесть",
            "Defence"              => "Защита",
            "AttackPower"          => "Сила атаки",
            "Strength"             => "Сила",
            "Agility"              => "Ловкость",
            "Endurance"            => "Выносливость",
            "Precision"            => "Меткость",
            "Mastery"              => "Мастерство",
            "Slash"                => "Режущий урон",
            "Pierce"               => "Колющий урон",
            "Blunt"                => "Дробящий урон",
            "Crude"                => "Дробящий урон",
            "ShipCannonDamage"     => "Урон",
            "ShipCannonReloadTime" => "Перезарядка",
            "ShipFireAccuracyRANK" => "Меткость",
            "ShipFireRangeRANK"    => "Дальность",
            "None"                 => "",
            _                      => stat,
        };

        static string StatDisplayNameEn(string stat) => stat switch
        {
            "Vitality"             => "Vitality",
            "Defence"              => "Defence",
            "AttackPower"          => "Attack Power",
            "Strength"             => "Strength",
            "Agility"              => "Agility",
            "Endurance"            => "Endurance",
            "Precision"            => "Precision",
            "Mastery"              => "Mastery",
            "Slash"                => "Slash",
            "Pierce"               => "Pierce",
            "Blunt"                => "Blunt",
            "Crude"                => "Blunt",
            "ShipCannonDamage"     => "Damage",
            "ShipCannonReloadTime" => "Reload",
            "ShipFireAccuracyRANK" => "Accuracy",
            "ShipFireRangeRANK"    => "Range",
            "None"                 => "",
            _                      => stat,
        };

        // ── Символы характеристик ──────────────────────────────────────────────
        public static string StatSymbol(string stat) => stat switch
        {
            "Vitality"             => "♥",
            "Defence"              => "◈",
            "AttackPower"          => "⚔",
            "Strength"             => "◆",
            "Agility"              => "▲",
            "Endurance"            => "⬡",
            "Precision"            => "◎",
            "Mastery"              => "✦",
            "Slash"                => "⟋",
            "Pierce"               => "↑",
            "Blunt"                => "●",
            "Crude"                => "●",
            "ShipCannonDamage"     => "💥",
            "ShipCannonReloadTime" => "⏱",
            "ShipFireAccuracyRANK" => "◎",
            "ShipFireRangeRANK"    => "↔",
            _                      => "•",
        };
    }
}
