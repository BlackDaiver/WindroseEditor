using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace WindroseEditor
{
    // ── Curve table references (от IconExtractor) ─────────────────────────────
    public class StatCurveRef
    {
        public string Table { get; set; } = "";
        public string Row   { get; set; } = "";
        public float  Level { get; set; }
        public string Stat  { get; set; } = "";
    }

    public class DescCurveRef
    {
        public string Table       { get; set; } = "";
        public string Row         { get; set; } = "";
        public float  Level       { get; set; }
        public string DisplayType { get; set; } = "";
        public bool   Inverse     { get; set; }
    }

    /// <summary>Сетовый бонус: текст с плейсхолдерами {0},{1}... из пула DescCurves.</summary>
    public class SetEffectEntry
    {
        // ── EN (R5DevCulture) — хранится в JSON legacy и в БД ─────────────────
        [JsonPropertyName("name")]
        public string NameEn          { get; set; } = "";
        [JsonPropertyName("description")]
        public string DescriptionEn   { get; set; } = "";
        public int    ActivationCount { get; set; }

        // ── RU — только из SQLite v2+ ─────────────────────────────────────────
        [JsonIgnore] public string NameRu        { get; set; } = "";
        [JsonIgnore] public string DescriptionRu { get; set; } = "";

        // ── Вычисляемые (зависят от AppLanguage) ──────────────────────────────
        [JsonIgnore] public string Name        => AppLanguage.Loc(NameEn,        NameRu);
        [JsonIgnore] public string Description => AppLanguage.Loc(DescriptionEn, DescriptionRu);
    }

    public class ItemEntry
    {
        public string       Filename        { get; set; } = "";

        // ── Локализованные строки — EN и RU раздельно ─────────────────────────
        // [JsonPropertyName] позволяет десериализовать старые JSON-базы (item_db.json)
        // без изменений: поле "display_name" → DisplayNameEn.
        [JsonPropertyName("display_name")]
        public string DisplayNameEn  { get; set; } = "";
        [JsonIgnore]
        public string DisplayNameRu  { get; set; } = "";

        [JsonPropertyName("description")]
        public string DescriptionEn  { get; set; } = "";
        [JsonIgnore]
        public string DescriptionRu  { get; set; } = "";

        [JsonPropertyName("vanity_text")]
        public string VanityTextEn   { get; set; } = "";
        [JsonIgnore]
        public string VanityTextRu   { get; set; } = "";

        // ── Computed (language-aware) ──────────────────────────────────────────
        [JsonIgnore] public string DisplayName => AppLanguage.Loc(DisplayNameEn, DisplayNameRu);
        [JsonIgnore] public string Description => AppLanguage.Loc(DescriptionEn, DescriptionRu);
        [JsonIgnore] public string VanityText  => AppLanguage.Loc(VanityTextEn,  VanityTextRu);

        // ── Нелокализованные поля ─────────────────────────────────────────────
        public string       ItemTag         { get; set; } = "";
        public string       ItemType        { get; set; } = "";
        public string       Rarity          { get; set; } = "";
        public string       Category        { get; set; } = "";
        public string       IconRef         { get; set; } = "";
        public string       ItemParamsPath  { get; set; } = "";
        public string       MainStat        { get; set; } = "";
        public List<string> SecondaryStats  { get; set; } = new();
        /// <summary>Макс. уровень из Level-атрибута. 0 = уровней нет.</summary>
        public int          MaxLevel        { get; set; }
        /// <summary>Макс. качество (0 = качества нет).</summary>
        public int          MaxQualityLevel { get; set; }
        /// <summary>Макс. кол-во в слоте.</summary>
        public int          MaxCountInSlot  { get; set; } = 9999;
        public float        Weight          { get; set; }
        public bool         KeepOnDeath     { get; set; }

        // ── Ссылки на кривые характеристик ───────────────────────────────────
        public StatCurveRef?         MainStatCurve       { get; set; }
        public List<StatCurveRef>?   SecondaryStatCurves { get; set; }
        public List<StatCurveRef>?   AddlStatCurves      { get; set; }
        public List<DescCurveRef>?   DescCurves          { get; set; }

        // ── Тексты эффектов — EN и RU параллельные списки ────────────────────
        public List<string>?         EffectsTexts        { get; set; }  // EN
        [JsonIgnore]
        public List<string>?         EffectsTextsRu      { get; set; }  // RU

        public List<SetEffectEntry>? SetEffects          { get; set; }

        /// <summary>
        /// Тексты эффектов на текущем языке.
        /// Списки параллельны: для каждой позиции — RU если не пусто, иначе EN.
        /// </summary>
        [JsonIgnore]
        public List<string>? EffectsTextsLocalized
        {
            get
            {
                if (!AppLanguage.IsRu || EffectsTextsRu == null || EffectsTextsRu.Count == 0)
                    return EffectsTexts;

                int count = Math.Max(EffectsTextsRu.Count, EffectsTexts?.Count ?? 0);
                var merged = new List<string>(count);
                for (int i = 0; i < count; i++)
                {
                    string ru = (EffectsTextsRu != null && i < EffectsTextsRu.Count)
                        ? EffectsTextsRu[i] : "";
                    string en = (EffectsTexts   != null && i < EffectsTexts.Count)
                        ? EffectsTexts[i]   : "";
                    merged.Add(!string.IsNullOrEmpty(ru) ? ru : en);
                }
                return merged;
            }
        }
    }

    public static class ItemDatabase
    {
        public static List<ItemEntry> Items  { get; private set; } = new();
        public static bool            Loaded { get; private set; }

        // ──────────────────────────────────────────────────────────────────────
        // Load from windrose_items.db  (SQLite, generated by IconExtractor)
        // ──────────────────────────────────────────────────────────────────────
        public static string LoadFromSqlite(string dbPath)
        {
            if (!File.Exists(dbPath))
                return $"Файл не найден: {dbPath}";

            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                conn.Open();

                // ── Версия схемы ──────────────────────────────────────────────
                // Схема v2 добавила _ru-колонки для локализации.
                int schemaVersion = 1;
                using (var cv = conn.CreateCommand())
                {
                    cv.CommandText = "SELECT value FROM meta WHERE key='version' LIMIT 1";
                    if (cv.ExecuteScalar() is string sv && int.TryParse(sv, out int v))
                        schemaVersion = v;
                }
                bool v2 = schemaVersion >= 2;

                // Индекс id → ItemEntry для заполнения связанных таблиц
                var byId = new Dictionary<long, ItemEntry>();
                var list = new List<ItemEntry>();

                // ── Основные данные предметов ─────────────────────────────────
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = v2
                        ? @"SELECT id,filename,item_params_path,
                                   display_name,   display_name_ru,
                                   description,    description_ru,
                                   vanity_text,    vanity_text_ru,
                                   item_tag,item_type,rarity,category,icon_ref,
                                   weight,keep_on_death,max_level,max_quality_level,max_count_in_slot
                             FROM items WHERE item_params_path != '' ORDER BY id"
                        : @"SELECT id,filename,item_params_path,
                                   display_name,'',description,'',vanity_text,'',
                                   item_tag,item_type,rarity,category,icon_ref,
                                   weight,keep_on_death,max_level,max_quality_level,max_count_in_slot
                             FROM items WHERE item_params_path != '' ORDER BY id";

                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        long id = r.GetInt64(0);
                        var e = new ItemEntry
                        {
                            Filename        = r.GetString(1),
                            ItemParamsPath  = r.GetString(2),
                            DisplayNameEn   = r.GetString(3),
                            DisplayNameRu   = r.GetString(4),
                            DescriptionEn   = r.GetString(5),
                            DescriptionRu   = r.GetString(6),
                            VanityTextEn    = r.GetString(7),
                            VanityTextRu    = r.GetString(8),
                            ItemTag         = r.GetString(9),
                            ItemType        = r.GetString(10),
                            Rarity          = r.GetString(11),
                            Category        = r.GetString(12),
                            IconRef         = r.GetString(13),
                            Weight          = (float)r.GetDouble(14),
                            KeepOnDeath     = r.GetInt32(15) != 0,
                            MaxLevel        = r.GetInt32(16),
                            MaxQualityLevel = r.GetInt32(17),
                            MaxCountInSlot  = r.GetInt32(18),
                        };
                        byId[id] = e;
                        list.Add(e);
                    }
                }

                // ── Stat curves ───────────────────────────────────────────────
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        SELECT item_id,kind,table_path,row_name,curve_level,stat_name
                        FROM stat_curves
                        ORDER BY item_id, kind, sort_order";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        if (!byId.TryGetValue(r.GetInt64(0), out var e)) continue;
                        string kind = r.GetString(1);
                        var sc = new StatCurveRef
                        {
                            Table = r.GetString(2),
                            Row   = r.GetString(3),
                            Level = (float)r.GetDouble(4),
                            Stat  = r.GetString(5),
                        };
                        switch (kind)
                        {
                            case "main":      e.MainStatCurve = sc; break;
                            case "secondary": (e.SecondaryStatCurves ??= new()).Add(sc); break;
                            case "addl":      (e.AddlStatCurves ??= new()).Add(sc); break;
                        }
                    }
                }

                // ── Desc curves ───────────────────────────────────────────────
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        SELECT item_id,table_path,row_name,curve_level,display_type,inverse
                        FROM desc_curves
                        ORDER BY item_id, sort_order";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        if (!byId.TryGetValue(r.GetInt64(0), out var e)) continue;
                        (e.DescCurves ??= new()).Add(new DescCurveRef
                        {
                            Table       = r.GetString(1),
                            Row         = r.GetString(2),
                            Level       = (float)r.GetDouble(3),
                            DisplayType = r.GetString(4),
                            Inverse     = r.GetInt32(5) != 0,
                        });
                    }
                }

                // ── Effects texts ─────────────────────────────────────────────
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = v2
                        ? "SELECT item_id,text,text_ru FROM effects_texts ORDER BY item_id,sort_order"
                        : "SELECT item_id,text,''      FROM effects_texts ORDER BY item_id,sort_order";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        if (!byId.TryGetValue(r.GetInt64(0), out var e)) continue;
                        (e.EffectsTexts   ??= new()).Add(r.GetString(1));
                        (e.EffectsTextsRu ??= new()).Add(r.GetString(2));
                    }
                }

                // ── Set effects ───────────────────────────────────────────────
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = v2
                        ? @"SELECT item_id,name,name_ru,description,description_ru,activation_count
                             FROM set_effects ORDER BY item_id, sort_order"
                        : @"SELECT item_id,name,'',description,'',activation_count
                             FROM set_effects ORDER BY item_id, sort_order";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        if (!byId.TryGetValue(r.GetInt64(0), out var e)) continue;
                        (e.SetEffects ??= new()).Add(new SetEffectEntry
                        {
                            NameEn          = r.GetString(1),
                            NameRu          = r.GetString(2),
                            DescriptionEn   = r.GetString(3),
                            DescriptionRu   = r.GetString(4),
                            ActivationCount = r.GetInt32(5),
                        });
                    }
                }

                Items  = list;
                Loaded = list.Count > 0;
                return "";
            }
            catch (Exception ex)
            {
                return $"Ошибка загрузки windrose_items.db: {ex.Message}";
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Load from item_db.json  (JSON, legacy fallback)
        // ──────────────────────────────────────────────────────────────────────
        public static string LoadFromDb(string dbPath)
        {
            if (!File.Exists(dbPath))
                return $"Файл не найден: {dbPath}";

            try
            {
                var opts = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true,
                };
                var list = JsonSerializer.Deserialize<List<ItemEntry>>(
                    File.ReadAllText(dbPath, System.Text.Encoding.UTF8), opts);

                Items  = list?.Where(i => !string.IsNullOrEmpty(i.ItemParamsPath)).ToList()
                         ?? new List<ItemEntry>();
                Loaded = Items.Count > 0;
                return "";
            }
            catch (Exception ex)
            {
                return $"Ошибка загрузки item_db.json: {ex.Message}";
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Load from legacy Item ID Database.html  (fallback)
        // ──────────────────────────────────────────────────────────────────────
        static readonly Regex ItemsRegex =
            new Regex(@"const ITEMS\s*=\s*(\[[\s\S]*?\]);", RegexOptions.Multiline);

        public static string Load(string htmlPath)
        {
            if (!File.Exists(htmlPath))
                return $"File not found: {htmlPath}";

            string content;
            try { content = File.ReadAllText(htmlPath, System.Text.Encoding.UTF8); }
            catch (Exception ex) { return $"Read error: {ex.Message}"; }

            var match = ItemsRegex.Match(content);
            if (!match.Success)
                return "Could not find ITEMS array in HTML file.";

            try
            {
                using var jdoc = JsonDocument.Parse(match.Groups[1].Value);
                var list = new List<ItemEntry>();

                foreach (var el in jdoc.RootElement.EnumerateArray())
                {
                    var item = new ItemEntry
                    {
                        Filename       = GetStr(el, "filename"),
                        DisplayNameEn  = GetStr(el, "display_name"),
                        DescriptionEn  = GetStr(el, "description"),
                        VanityTextEn   = GetStr(el, "vanity_text"),
                        ItemTag        = GetStr(el, "item_tag"),
                        ItemType       = GetStr(el, "item_type"),
                        Rarity         = GetStr(el, "rarity"),
                        Category       = GetStr(el, "category"),
                        IconRef        = GetStr(el, "icon_ref"),
                        ItemParamsPath = GetStr(el, "item_params_path"),
                        MainStat       = GetStr(el, "main_stat"),
                        KeepOnDeath    = GetBool(el, "keep_on_death"),
                        Weight         = GetFloat(el, "weight"),
                        MaxLevel       = GetNullableInt(el, "max_level") ?? 0,
                    };

                    if (el.TryGetProperty("secondary_stats", out var ss)
                        && ss.ValueKind == JsonValueKind.Array)
                    {
                        item.SecondaryStats = ss.EnumerateArray()
                            .Select(s => s.GetString() ?? "")
                            .Where(s => s.Length > 0)
                            .ToList();
                    }

                    if (!string.IsNullOrEmpty(item.ItemParamsPath))
                        list.Add(item);
                }

                Items  = list;
                Loaded = true;
                return "";
            }
            catch (Exception ex) { return $"JSON parse error: {ex.Message}"; }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Filtering
        // ──────────────────────────────────────────────────────────────────────
        public static IEnumerable<ItemEntry> Filter(
            string search   = "",
            string rarity   = "",
            string category = "")
        {
            IEnumerable<ItemEntry> q = Items;

            if (!string.IsNullOrWhiteSpace(search))
            {
                string s = search.Trim().ToLowerInvariant();
                q = q.Where(i =>
                    i.DisplayName.ToLowerInvariant().Contains(s)    ||
                    i.DisplayNameEn.ToLowerInvariant().Contains(s)  ||
                    i.DisplayNameRu.ToLowerInvariant().Contains(s)  ||
                    i.Filename.ToLowerInvariant().Contains(s)       ||
                    i.ItemTag.ToLowerInvariant().Contains(s)        ||
                    i.Category.ToLowerInvariant().Contains(s));
            }

            if (!string.IsNullOrEmpty(rarity))
                q = q.Where(i => i.Rarity.Equals(rarity, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(category) && category != "All" && category != "Все")
                q = q.Where(i => i.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

            return q;
        }

        public static List<string> GetCategories() =>
            Items.Select(i => i.Category)
                 .Where(c => !string.IsNullOrEmpty(c))
                 .Distinct()
                 .OrderBy(c => c)
                 .ToList();

        public static readonly string[] Rarities =
            { "Legendary", "Epic", "Rare", "Uncommon", "Common" };

        public static int RarityRank(string rarity) => rarity switch
        {
            "Legendary" => 0,
            "Epic"      => 1,
            "Rare"      => 2,
            "Uncommon"  => 3,
            "Common"    => 4,
            _           => 5
        };

        // ──────────────────────────────────────────────────────────────────────
        // Category display names (язык-зависимые)
        // ──────────────────────────────────────────────────────────────────────
        public static string CategoryDisplayName(string cat) =>
            AppLanguage.IsRu ? CategoryDisplayNameRu(cat) : cat;

        static string CategoryDisplayNameRu(string cat) => cat switch
        {
            "Armor"             => "Броня",
            "MeleeWeapon"       => "Оружие ближнего боя",
            "RangeWeapon"       => "Дальнобойное оружие",
            "Ammo"              => "Боеприпасы",
            "Ring"              => "Кольцо",
            "Necklace"          => "Ожерелье",
            "Backpack"          => "Рюкзак",
            "Resource"          => "Ресурс",
            "Misc"              => "Разное",
            "Food"              => "Еда",
            "Alchemy"           => "Алхимия",
            "Medicine"          => "Медицина",
            "Tool"              => "Инструмент",
            "Metal"             => "Металл",
            "ShipWeapon"        => "Корабельное оружие",
            "ShipHullMod"       => "Мод корпуса",
            "ShipSailMod"       => "Мод паруса",
            "ShipCrewEquipment" => "Снаряжение экипажа",
            "ShipCombatOrder"   => "Боевой приказ",
            "Default"           => "По умолчанию",
            _                   => cat
        };

        // ──────────────────────────────────────────────────────────────────────
        // HTML helpers
        // ──────────────────────────────────────────────────────────────────────
        static string GetStr(JsonElement el, string key)
        {
            if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";
            return "";
        }

        static bool GetBool(JsonElement el, string key)
        {
            if (el.TryGetProperty(key, out var v)
                && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                return v.GetBoolean();
            return false;
        }

        static float GetFloat(JsonElement el, string key)
        {
            if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number)
                return v.GetSingle();
            return 0f;
        }

        static int? GetNullableInt(JsonElement el, string key)
        {
            if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number)
                return v.GetInt32();
            return null;
        }
    }
}
