using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WindroseEditor
{
    // ── Ship type descriptor ───────────────────────────────────────────────────
    public record ShipTypeInfo(
        string Params,
        string InventoryParams,
        string NameRu,
        string NameEn,
        string? NameKey,      // localization key (null = use raw string name)
        string? NameTableId,
        bool IsBoat = false);

    // ── Single ship instance ───────────────────────────────────────────────────
    public class ShipInfo
    {
        public string  Guid            { get; set; } = "";
        public string  BuildingId      { get; set; } = "";
        public string  ShipParams      { get; set; } = "";
        public string  ShipName        { get; set; } = "";
        public float   HealthPercent   { get; set; } = 1f;
        public int     RewardLevel     { get; set; }
        public bool    IsFlagship      { get; set; }
        public bool    IsPossessed     { get; set; }
        public bool    IsDefault       { get; set; }
        public bool    IsNew           { get; set; }   // pending write to WAL
        public bool    IsDeleted       { get; set; }   // pending delete in WAL
        /// <summary>true when the saved ShipName field is a localization document (not a custom string).</summary>
        public bool    IsNameLocalized { get; set; }
        public BsonDocument RawDoc     { get; set; } = new();

        public ShipTypeInfo? ShipTypeInfo =>
            SaveFile.KnownShipTypes.FirstOrDefault(
                t => t.Params.Equals(ShipParams, StringComparison.OrdinalIgnoreCase));

        public bool IsBoat =>
            ShipTypeInfo?.IsBoat ?? ShipParams.Contains("DA_Ship_Boat", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// User-visible name: custom string name when set, otherwise the localised type name.
        /// </summary>
        public string DisplayName
        {
            get
            {
                // If the BSON stored a localization key, show the type name from our table.
                if (IsNameLocalized || string.IsNullOrEmpty(ShipName))
                    return AppLanguage.IsRu
                        ? (ShipTypeInfo?.NameRu ?? "Ship")
                        : (ShipTypeInfo?.NameEn ?? "Ship");
                return ShipName;
            }
        }

        /// <summary>Type display name (always from our table, ignores custom ShipName).</summary>
        public string TypeName =>
            AppLanguage.IsRu ? (ShipTypeInfo?.NameRu ?? ShipName)
                             : (ShipTypeInfo?.NameEn ?? ShipName);
    }

    public class InventorySlot
    {
        public int    ModuleIndex { get; set; }
        public int    SlotIndex   { get; set; }
        public string ItemParams  { get; set; } = "";
        public string ItemId      { get; set; } = "";
        public int    Level       { get; set; }
        public int    MaxLevel    { get; set; } = 15;
        public int    Quality     { get; set; }   // QualityLevel из BSON (0 = нет)
        public int    Count       { get; set; } = 1;

        public bool   IsEmpty     => string.IsNullOrEmpty(ItemParams);

        /// <summary>Short display name derived from the ItemParams path.</summary>
        public string InternalName => ItemParams.Contains('/')
            ? ItemParams.Split('/').Last().Split('.').First()
            : ItemParams;
    }

    public class ModuleInfo
    {
        public int    Index    { get; set; }
        public int    Capacity { get; set; }
        public int    Used     { get; set; }
        public int    Free     => Capacity - Used;
        public string Label    { get; set; } = "";

        // Infer a friendly name from the items in the module
        public string DisplayLabel => !string.IsNullOrEmpty(Label) ? Label : $"Module {Index}";
    }

    public class SaveFile
    {
        private PlayerSaveData _raw;

        public BsonDocument Doc         { get; private set; }
        public string       PlayerName  { get; private set; } = "";
        public string       PlayerGuid  { get; private set; } = "";
        public bool         IsModified  { get; private set; }
        public string       SaveDir     => _raw.SaveDir;

        // ── Known ship types ──────────────────────────────────────────────
        public static readonly ShipTypeInfo[] KnownShipTypes =
        {
            // ── Boat (non-deletable, every player has one) ────────────────
            new("/R5BusinessRules/Ship/Boat/DA_Ship_Boat.DA_Ship_Boat",
                "/R5BusinessRules/Inventory/Ship/DA_ShipInventory_Boat.DA_ShipInventory_Boat",
                "Лодка", "Boat",
                "Ship_BoatDefault_Name", "ShipUI",
                IsBoat: true),

            // ── Cutter (cut content — not in DA_AvailableShipsList, but uses a
            //           valid PlayerShips blueprint and full customization setup) ──
            new("/R5BusinessRules/Ship/Cutter/DA_Ship_Cutter.DA_Ship_Cutter",
                "/R5BusinessRules/Inventory/Ship/DA_ShipInventory_Cutter.DA_ShipInventory_Cutter",
                "Каттер (скрытый)", "Cutter (Hidden)",
                null, null),

            new("/R5BusinessRules/Ship/Cutter/DA_Ship_CutterBlack.DA_Ship_CutterBlack",
                "/R5BusinessRules/Inventory/Ship/DA_ShipInventory_Cutter.DA_ShipInventory_Cutter",
                "Каттер чёрный (скрытый)", "Cutter Black (Hidden)",
                null, null),

            // ── Ketch ─────────────────────────────────────────────────────
            new("/R5BusinessRules/Ship/Ketch/DA_Ship_Ketch.DA_Ship_Ketch",
                "/R5BusinessRules/Inventory/Ship/DA_ShipInventory_Ketch_Stock.DA_ShipInventory_Ketch_Stock",
                "Кеч", "Ketch",
                null, null),

            new("/R5BusinessRules/Ship/Ketch/DA_Ship_Ketch_Blackbeard.DA_Ship_Ketch_Blackbeard",
                "/R5BusinessRules/Inventory/Ship/DA_ShipInventory_Ketch_Blackbeard.DA_ShipInventory_Ketch_Blackbeard",
                "Кеч чернобородцев", "Ketch Blackbeard",
                null, null),

            new("/R5BusinessRules/Ship/Ketch/DA_Ship_Ketch_Brethren.DA_Ship_Ketch_Brethren",
                "/R5BusinessRules/Inventory/Ship/DA_ShipInventory_Ketch_Brethren.DA_ShipInventory_Ketch_Brethren",
                "Кеч Братства", "Ketch Brethren",
                null, null),

            // ── Brig ──────────────────────────────────────────────────────
            new("/R5BusinessRules/Ship/Brig/DA_Ship_Brig.DA_Ship_Brig",
                "/R5BusinessRules/Inventory/Ship/DA_ShipInventory_Brig_Stock.DA_ShipInventory_Brig_Stock",
                "Бриг", "Brig",
                null, null),

            new("/R5BusinessRules/Ship/Brig/DA_Ship_Brig_Blackbeard.DA_Ship_Brig_Blackbeard",
                "/R5BusinessRules/Inventory/Ship/DA_ShipInventory_Brig_Blackbeard.DA_ShipInventory_Brig_Blackbeard",
                "Бриг чернобородцев", "Brig Blackbeard",
                null, null),

            new("/R5BusinessRules/Ship/Brig/DA_Ship_Brig_Brethren.DA_Ship_Brig_Brethren",
                "/R5BusinessRules/Inventory/Ship/DA_ShipInventory_Brig_Brethren.DA_ShipInventory_Brig_Brethren",
                "Бриг Братства", "Brig Brethren",
                null, null),

            // ── Frigate ───────────────────────────────────────────────────
            new("/R5BusinessRules/Ship/Frigate/DA_Ship_Frigate.DA_Ship_Frigate",
                "/R5BusinessRules/Inventory/Ship/DA_ShipInventory_Frigate_Stock.DA_ShipInventory_Frigate_Stock",
                "Фрегат", "Frigate",
                null, null),

            new("/R5BusinessRules/Ship/Frigate/DA_Ship_Frigate_Blackbeard.DA_Ship_Frigate_Blackbeard",
                "/R5BusinessRules/Inventory/Ship/DA_ShipInventory_Frigate_Blackbeard.DA_ShipInventory_Frigate_Blackbeard",
                "Фрегат чернобородцев", "Frigate Blackbeard",
                null, null),

            new("/R5BusinessRules/Ship/Frigate/DA_Ship_Frigate_Brethren.DA_Ship_Frigate_Brethren",
                "/R5BusinessRules/Inventory/Ship/DA_ShipInventory_Frigate_Brethren.DA_ShipInventory_Frigate_Brethren",
                "Фрегат Братства", "Frigate Brethren",
                null, null),
        };

        // ── Ships cache ───────────────────────────────────────────────────
        private List<ShipInfo> _ships = new();
        public  IReadOnlyList<ShipInfo> Ships => _ships;

        private SaveFile(PlayerSaveData raw, BsonDocument doc)
        {
            _raw = raw;
            Doc  = doc;
            if (doc.TryGetValue("PlayerName", out var pn) && pn != null) PlayerName = pn.AsString();
            if (doc.TryGetValue("_guid",       out var pg) && pg != null) PlayerGuid = pg.AsString();
            LoadShips();
        }

        // ──────────────────────────────────────────────────────────────────
        // Ships loading
        // ──────────────────────────────────────────────────────────────────
        void LoadShips()
        {
            _ships.Clear();

            // Flagship / possessed / default IDs from player doc
            string flagshipId  = "";
            string possessedId = "";
            string defaultId   = "";
            var so = Doc.Navigate("ShipOwner");
            if (so != null && so.IsDocument)
            {
                var sod = so.AsDocument();
                if (sod.TryGetValue("FlagshipId",   out var fid) && fid != null) flagshipId  = fid.AsString();
                if (sod.TryGetValue("PossessedShipId", out var pid) && pid != null) possessedId = pid.AsString();
                var dd = sod.Navigate("DefaultShipData.DefaultShipId");
                if (dd != null) defaultId = dd.AsString();
            }

            // ── WAL supersedes SSTs ──────────────────────────────────────
            // Ships edited via this editor are written to the WAL; the SSTs
            // are only updated when the game flushes/compacts on next launch.
            // We must apply WAL changes here so the editor stays consistent
            // without requiring the game to run between editor sessions.
            var (walShipPuts, walShipDeletes) =
                RocksDbAccess.ReadShipWalEntries(_raw.SaveDir);

            // Scan SSTs for ship documents
            var shipRaws = RocksDbAccess.ReadShipsFromSst(_raw.SaveDir,
                string.IsNullOrEmpty(PlayerGuid) ? _raw.SaveDir.Split('\\', '/').Last() : PlayerGuid);

            // Build combined list: SST ships (minus WAL tombstones) + WAL-only ships
            var combined  = new List<(string guid, byte[] bson)>();
            var seenGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (guid, bson) in shipRaws)
            {
                if (walShipDeletes.Contains(guid)) continue; // deleted in WAL
                combined.Add((guid, bson));
                seenGuids.Add(guid);
            }

            // Ships added via WAL that haven't been compacted into an SST yet
            foreach (var kvp in walShipPuts)
            {
                if (!seenGuids.Contains(kvp.Key))
                    combined.Add((kvp.Key, kvp.Value));
            }

            foreach (var (guid, bson) in combined)
            {
                try
                {
                    var doc = BsonParser.Parse(bson);
                    var ship = new ShipInfo
                    {
                        Guid        = guid,
                        BuildingId  = ReadStr(doc, "BuildingId"),
                        ShipParams  = ReadStr(doc, "ShipParams"),
                        HealthPercent = 1f,
                        IsFlagship  = guid.Equals(flagshipId,  StringComparison.OrdinalIgnoreCase),
                        IsPossessed = guid.Equals(possessedId, StringComparison.OrdinalIgnoreCase),
                        IsDefault   = guid.Equals(defaultId,   StringComparison.OrdinalIgnoreCase),
                        RawDoc      = doc,
                    };

                    // Health
                    var attrs = doc.Navigate("Attributes");
                    if (attrs != null && attrs.IsDocument &&
                        attrs.AsDocument().TryGetValue("HealthPercent", out var hp) && hp != null)
                        ship.HealthPercent = (float)hp.AsDouble();

                    // Name
                    if (doc.TryGetValue("ShipName", out var sn) && sn != null)
                    {
                        if (sn.IsDocument)
                        {
                            ship.ShipName        = sn.AsDocument().Navigate("Key")?.AsString() ?? "Ship";
                            ship.IsNameLocalized = true;
                        }
                        else
                        {
                            ship.ShipName        = sn.AsString();
                            ship.IsNameLocalized = false;
                        }
                    }

                    // Progression level
                    var lvl = doc.Navigate("Progression.RewardLevel");
                    if (lvl != null) ship.RewardLevel = (int)lvl.TryAsLong();

                    _ships.Add(ship);
                }
                catch { }
            }

            // Sort: flagship first, then by guid
            _ships = _ships
                .OrderByDescending(s => s.IsFlagship)
                .ThenByDescending(s => s.IsPossessed)
                .ThenBy(s => s.Guid)
                .ToList();
        }

        static string ReadStr(BsonDocument doc, string key)
        {
            if (doc.TryGetValue(key, out var v) && v != null && v.Type == BsonType.String)
                return v.AsString();
            return "";
        }

        // ──────────────────────────────────────────────────────────────────
        // Loading
        // ──────────────────────────────────────────────────────────────────
        public static (SaveFile? file, string error) Load(string path)
        {
            string saveDir = ResolveSaveDir(path);

            if (!Directory.Exists(saveDir))
                return (null, $"Directory not found: {saveDir}");

            if (!File.Exists(Path.Combine(saveDir, "CURRENT")))
                return (null, $"No CURRENT file found in: {saveDir}\nCheck the path points to a RocksDB player folder.");

            // 1. Try WAL files first (fast pure-C# path; works whether the game
            //    was closed cleanly or left a non-empty WAL).
            PlayerSaveData? raw = RocksDbAccess.ReadFromWal(saveDir);

            if (raw == null)
            {
                // 2. Pure-C# SST scanner (no DLL required; works with kNoCompression
                //    databases — which is the game's configuration).
                raw = RocksDbAccess.ReadFromSstDirect(saveDir);
            }

            if (raw == null)
            {
                // 3. Last resort: P/Invoke via rocksdb.dll.  May fail if the bundled
                //    DLL version doesn't match the game's RocksDB version (10.4.2).
                raw = RocksDbAccess.ReadFromSst(saveDir);
            }

            if (raw == null)
                return (null, "Could not find player data in WAL or SST files.\nMake sure the game is fully closed, then try again.");

            BsonDocument doc;
            try { doc = BsonParser.Parse(raw.BsonBytes); }
            catch (Exception ex) { return (null, $"BSON parse error: {ex.Message}"); }

            return (new SaveFile(raw, doc), "");
        }

        // ──────────────────────────────────────────────────────────────────
        // Inventory reading
        // ──────────────────────────────────────────────────────────────────
        public List<ModuleInfo> GetModules()
        {
            var result = new List<ModuleInfo>();
            var modsVal = Doc.Navigate("Inventory.Modules");
            if (modsVal == null || !modsVal.IsDocument) return result;

            foreach (var (key, modVal) in modsVal.AsDocument())
            {
                if (!modVal.IsDocument) continue;
                var mod  = modVal.AsDocument();
                int cap  = GetModuleCapacity(mod);
                int used = CountUsedSlots(mod);
                if (!int.TryParse(key, out int idx)) continue;
                result.Add(new ModuleInfo { Index = idx, Capacity = cap, Used = used,
                                            Label = InferModuleLabel(idx, mod) });
            }
            return result.OrderBy(m => m.Index).ToList();
        }

        static string InferModuleLabel(int idx, BsonDocument mod)
        {
            // Sample the first item's path to infer what kind of module this is
            if (mod.TryGetValue("Slots", out var sv) && sv != null && sv.IsDocument)
            {
                foreach (var (_, slotVal) in sv.AsDocument())
                {
                    if (!slotVal.IsDocument) continue;
                    var path = slotVal.AsDocument().Navigate("ItemsStack.Item.ItemParams");
                    if (path == null || string.IsNullOrEmpty(path.AsString())) continue;
                    string p = path.AsString().ToLowerInvariant();
                    if (p.Contains("/ammo/"))             return $"Mod {idx}: Ammo";
                    if (p.Contains("/armor/"))            return $"Mod {idx}: Armor";
                    if (p.Contains("/weapon/"))           return $"Mod {idx}: Action Bar";
                    if (p.Contains("/ring/") || p.Contains("/necklace/") || p.Contains("/backpack/"))
                                                          return $"Mod {idx}: Accessories";
                    if (p.Contains("shipcustomization"))  return $"Mod {idx}: Ship Cosmetics";
                    if (p.Contains("/npc/"))              return $"Mod {idx}: Crew";
                    if (p.Contains("/resource/") || p.Contains("/misc/"))
                                                          return $"Mod {idx}: Backpack";
                    if (p.Contains("/alchemy/") || p.Contains("/food/"))
                                                          return $"Mod {idx}: Action Bar";
                    break;
                }
            }
            return $"Mod {idx}";
        }

        public List<InventorySlot> GetSlots(int moduleIndex)
        {
            var result = new List<InventorySlot>();
            var modsVal = Doc.Navigate("Inventory.Modules");
            if (modsVal == null || !modsVal.IsDocument) return result;

            var mods = modsVal.AsDocument();
            if (!mods.TryGetValue(moduleIndex.ToString(), out var modVal) || modVal == null || !modVal.IsDocument)
                return result;

            var mod  = modVal.AsDocument();
            int cap  = GetModuleCapacity(mod);

            BsonDocument? slots = null;
            if (mod.TryGetValue("Slots", out var sv) && sv != null && sv.IsDocument)
                slots = sv.AsDocument();

            // Count actually used slots so we can limit display of empty ones.
            // For modules with huge capacity (e.g. backpack = 1016), only show
            // used slots + a reasonable buffer of empty slots after the last used one.
            int lastUsed = -1;
            if (slots != null)
            {
                foreach (var (k, slotVal) in slots)
                {
                    if (!slotVal.IsDocument) continue;
                    var tmpInfo = new InventorySlot();
                    ReadSlotData(slotVal.AsDocument(), tmpInfo);
                    if (!tmpInfo.IsEmpty && int.TryParse(k, out int ki))
                        lastUsed = Math.Max(lastUsed, ki);
                }
            }

            // Show at least 32 slots, or up to lastUsed + 16 empty slots, capped at capacity
            const int EmptyBuffer = 16;
            const int MinDisplay  = 32;
            int display = Math.Min(cap, Math.Max(MinDisplay, lastUsed + 1 + EmptyBuffer));

            for (int i = 0; i < display; i++)
            {
                var info = new InventorySlot { ModuleIndex = moduleIndex, SlotIndex = i };

                if (slots != null && slots.TryGetValue(i.ToString(), out var slotVal)
                    && slotVal != null && slotVal.IsDocument)
                {
                    ReadSlotData(slotVal.AsDocument(), info);
                }

                result.Add(info);
            }
            return result;
        }

        static void ReadSlotData(BsonDocument slotDoc, InventorySlot info)
        {
            if (!slotDoc.TryGetValue("ItemsStack", out var stackVal) || stackVal == null || !stackVal.IsDocument) return;
            var stack = stackVal.AsDocument();

            if (stack.TryGetValue("Count", out var cnt) && cnt != null)
                info.Count = (int)cnt.TryAsLong();

            if (!stack.TryGetValue("Item", out var itemVal) || itemVal == null || !itemVal.IsDocument) return;
            var item = itemVal.AsDocument();

            if (item.TryGetValue("ItemParams", out var ip) && ip != null) info.ItemParams = ip.AsString();
            if (item.TryGetValue("ItemId",     out var id) && id != null) info.ItemId     = id.AsString();

            // QualityLevel — прямое поле в Item
            if (item.TryGetValue("QualityLevel", out var qlv) && qlv != null)
                info.Quality = (int)qlv.TryAsLong();

            if (!item.TryGetValue("Attributes", out var attrsVal) || attrsVal == null || !attrsVal.IsDocument) return;
            foreach (var (_, av) in attrsVal.AsDocument())
            {
                if (!av.IsDocument) continue;
                var attr = av.AsDocument();
                if (!attr.TryGetValue("Tag", out var tagv) || tagv == null || !tagv.IsDocument) continue;
                if (!tagv.AsDocument().TryGetValue("TagName", out var tn) || tn == null) continue;
                string tagName = tn.AsString();
                if (tagName.Contains("Level") && !tagName.Contains("Quality"))
                {
                    if (attr.TryGetValue("Value",    out var lv) && lv != null) info.Level    = (int)lv.TryAsLong();
                    if (attr.TryGetValue("MaxValue", out var mv) && mv != null) info.MaxLevel = (int)mv.TryAsLong();
                }
                else if (tagName.Contains("Quality"))
                {
                    // Качество как атрибут (альтернативный способ хранения)
                    if (attr.TryGetValue("Value", out var qv) && qv != null) info.Quality = (int)qv.TryAsLong();
                }
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Ship operations
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Adds a new ship of the given type. Uses any existing ship as a structural
        /// template (cloned + cleared), so the BSON format is always valid.
        /// </summary>
        public ShipInfo? AddShip(string shipParams, string customName = "")
        {
            var typeInfo = KnownShipTypes.FirstOrDefault(
                t => t.Params.Equals(shipParams, StringComparison.OrdinalIgnoreCase));
            if (typeInfo == null) return null;

            // Pick a template — prefer same type, else use any non-deleted ship
            var templateShip = _ships
                .Where(s => !s.IsDeleted)
                .FirstOrDefault(s => s.ShipParams.Equals(shipParams, StringComparison.OrdinalIgnoreCase))
                ?? _ships.FirstOrDefault(s => !s.IsDeleted);

            if (templateShip == null) return null; // no template available

            BsonDocument template     = templateShip.RawDoc;
            string templateFolder     = ShipTypeFolder(templateShip.ShipParams);
            string newTypeFolder      = ShipTypeFolder(typeInfo.Params);
            bool   sameTypeTemplate   = templateFolder.Equals(newTypeFolder, StringComparison.OrdinalIgnoreCase);

            string newGuid       = Guid.NewGuid().ToString("N").ToUpper();
            string newBuildingId = Guid.NewGuid().ToString("N").ToUpper();

            // Deep-clone via serialize/parse
            var doc = BsonParser.Parse(BsonSerializer.Serialize(template));

            // ── Patch identifiers ────────────────────────────────────────
            doc["_guid"]       = BsonValue.FromString(newGuid);
            doc["BuildingId"]  = BsonValue.FromString(newBuildingId);
            doc["ShipParams"]  = BsonValue.FromString(typeInfo.Params);
            doc["PlayerId"]    = BsonValue.FromString(PlayerGuid);

            // ── Ship name ────────────────────────────────────────────────
            string finalName = !string.IsNullOrWhiteSpace(customName)
                ? customName
                : (AppLanguage.IsRu ? typeInfo.NameRu : typeInfo.NameEn);

            if (typeInfo.NameKey != null)
            {
                var nameDoc = new BsonDocument();
                nameDoc["Key"]     = BsonValue.FromString(typeInfo.NameKey);
                nameDoc["TableId"] = BsonValue.FromString(typeInfo.NameTableId ?? "ShipUI");
                doc["ShipName"] = BsonValue.FromDocument(nameDoc);
            }
            else
            {
                doc["ShipName"] = BsonValue.FromString(finalName);
            }

            // ── Patch Attributes ─────────────────────────────────────────
            if (doc.TryGetValue("Attributes", out var attrsV) && attrsV != null && attrsV.IsDocument)
            {
                attrsV.AsDocument()["HealthPercent"] = BsonValue.FromDouble(1.0);
                attrsV.AsDocument()["ShipParams"]    = BsonValue.FromString(typeInfo.Params);
            }

            // ── Patch Inventory ──────────────────────────────────────────
            if (doc.TryGetValue("Inventory", out var invV) && invV != null && invV.IsDocument)
            {
                var invDoc = invV.AsDocument();
                // NOTE: InventoryParams is intentionally NOT changed.
                // The template's module structure must match its InventoryParams;
                // switching InventoryParams to a different ship type while keeping
                // the old module layout causes a game crash on load.

                // Clear module slots, but PRESERVE the Customization module.
                // The Customization module holds ship-type-specific visual assets
                // (sail, flag, hull color).  Clearing it removes required assets and
                // causes a game crash when the player clicks the ship in the fleet UI.
                if (invDoc.TryGetValue("Modules", out var modsV) && modsV != null && modsV.IsDocument)
                {
                    foreach (var (_, mv) in modsV.AsDocument())
                    {
                        if (!mv.IsDocument) continue;
                        var modDoc = mv.AsDocument();

                        if (IsCustomizationModule(modDoc))
                        {
                            // Keep slots intact, but if the template is a different ship type,
                            // patch the asset paths so they reference the correct type's items.
                            if (!sameTypeTemplate
                                && !string.IsNullOrEmpty(templateFolder)
                                && !string.IsNullOrEmpty(newTypeFolder))
                            {
                                PatchCustomizationModuleItems(modDoc, templateFolder, newTypeFolder);
                            }
                            continue; // do NOT clear this module
                        }

                        // Slots is a BSON array (type 0x04) — must use FromArray, not FromDocument.
                        if (modDoc.ContainsKey("Slots"))
                            modDoc["Slots"] = BsonValue.FromArray(new BsonDocument());
                    }
                }

                // Update DropInventoryPath[1] = new ship guid
                if (invDoc.TryGetValue("DropInventoryPath", out var dipV) && dipV != null && dipV.IsDocument)
                    if (dipV.AsDocument().TryGetValue("1", out var idx1) && idx1 != null && idx1.IsDocument)
                        idx1.AsDocument()["$data"] = BsonValue.FromString(newGuid);
            }

            // ── Patch ScenarioSave ───────────────────────────────────────
            // A brand-new ship has ExecutorId="" (the game fills it in on first use).
            // Setting it to a GUID that has never been registered crashes the game
            // when the player clicks the ship in the fleet UI.
            if (doc.TryGetValue("ScenarioSave", out var ssV) && ssV != null && ssV.IsDocument)
            {
                ssV.AsDocument()["ExecutorId"] = BsonValue.FromString("");
                // Do NOT touch Crew — the template already has the correct Headcount /
                // MaxHeadcount (the game initialises these to 50/50 for a new ship).
                // Resetting them to 0 leaves the ship with no crew, which the game
                // treats as invalid and crashes on first click.
            }

            // ── Reset Progression ────────────────────────────────────────
            if (doc.TryGetValue("Progression", out var progV) && progV != null && progV.IsDocument)
            {
                var p = progV.AsDocument();
                p["RewardLevel"] = BsonValue.FromInt32(0);
                p["TotalExp"]    = BsonValue.FromInt32(0);
                foreach (var tree in new[] { "TalentTree", "StatTree" })
                    if (p.TryGetValue(tree, out var tv) && tv != null && tv.IsDocument)
                        if (tv.AsDocument().ContainsKey("Nodes"))
                        {
                            // Nodes is a BSON array — must use FromArray, not FromDocument.
                            tv.AsDocument()["Nodes"]             = BsonValue.FromArray(new BsonDocument());
                            tv.AsDocument()["ProgressionPoints"] = BsonValue.FromInt32(0);
                        }
            }

            // ── Reset location / damage / IslandId ───────────────────────
            // New ships have no world position (not yet spawned).
            // Keep the template's WorldLocation/Rotation would leave the new ship
            // at the template ship's map coordinates, confusing the game.
            if (doc.TryGetValue("WorldLocation", out var wlV) && wlV != null && wlV.IsDocument)
            {
                var wl = wlV.AsDocument();
                wl["X"] = BsonValue.FromDouble(0.0);
                wl["Y"] = BsonValue.FromDouble(0.0);
                wl["Z"] = BsonValue.FromDouble(0.0);
            }
            if (doc.TryGetValue("Rotation", out var rotV) && rotV != null && rotV.IsDocument)
            {
                var rot = rotV.AsDocument();
                foreach (var axis in rot.Select(kv => kv.Key).ToList())
                    rot[axis] = BsonValue.FromDouble(0.0);
            }

            // VisualArmorDamages is a BSON array — must use FromArray, not FromDocument.
            doc["VisualArmorDamages"] = BsonValue.FromArray(new BsonDocument());
            doc["IslandId"]           = BsonValue.FromString("");

            var ship = new ShipInfo
            {
                Guid        = newGuid,
                BuildingId  = newBuildingId,
                ShipParams  = typeInfo.Params,
                ShipName    = finalName,
                HealthPercent = 1f,
                IsNew       = true,
                RawDoc      = doc,
            };
            _ships.Add(ship);
            IsModified = true;
            return ship;
        }

        /// <summary>
        /// Marks a ship as the Flagship and PossessedShip in the player document.
        /// </summary>
        public bool SetFlagship(string shipGuid)
        {
            var ship = _ships.FirstOrDefault(
                s => s.Guid.Equals(shipGuid, StringComparison.OrdinalIgnoreCase));
            if (ship == null || ship.IsDeleted || ship.IsBoat) return false;

            // Update player BSON
            EnsureShipOwnerDoc();
            var so = Doc.Navigate("ShipOwner")!.AsDocument();
            so["FlagshipId"]   = BsonValue.FromString(ship.Guid);
            so["PossessedShipId"] = BsonValue.FromString(ship.Guid);

            // Update in-memory state
            foreach (var s in _ships)
            {
                s.IsFlagship  = s.Guid.Equals(ship.Guid, StringComparison.OrdinalIgnoreCase);
                s.IsPossessed = s.IsFlagship;
            }
            IsModified = true;
            return true;
        }

        /// <summary>
        /// Marks a ship for deletion. It will be removed from R5BLBuilding CF on Save().
        /// Cannot remove the last ship.
        /// </summary>
        public bool RemoveShip(string shipGuid)
        {
            var ship = _ships.FirstOrDefault(
                s => s.Guid.Equals(shipGuid, StringComparison.OrdinalIgnoreCase));
            if (ship == null) return false;
            if (ship.IsBoat) return false;   // boat is non-deletable

            int remaining = _ships.Count(s => !s.IsDeleted && !s.IsBoat);
            if (remaining <= 1) return false; // keep at least one non-boat ship

            ship.IsDeleted = true;

            // If it was flagship, reassign to first non-deleted ship
            if (ship.IsFlagship)
            {
                var next = _ships.FirstOrDefault(s => !s.IsDeleted);
                if (next != null) SetFlagship(next.Guid);
            }

            IsModified = true;
            return true;
        }

        void EnsureShipOwnerDoc()
        {
            if (!Doc.ContainsKey("ShipOwner"))
            {
                var so = new BsonDocument();
                var dd = new BsonDocument();
                dd["DefaultShipId"] = BsonValue.FromString(_ships.FirstOrDefault()?.Guid ?? "");
                dd["bIsUnlocked"]   = BsonValue.FromBool(true);
                so["DefaultShipData"]  = BsonValue.FromDocument(dd);
                so["FlagshipId"]       = BsonValue.FromString("");
                so["PossessedShipId"]  = BsonValue.FromString("");
                Doc["ShipOwner"] = BsonValue.FromDocument(so);
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Adding items
        // ──────────────────────────────────────────────────────────────────
        public bool AddItem(int moduleIndex, int slotIndex, string itemParams, int level, int count, int quality = 0)
        {
            var modsVal = Doc.Navigate("Inventory.Modules");
            if (modsVal == null || !modsVal.IsDocument) return false;

            var mods = modsVal.AsDocument();
            string modKey = moduleIndex.ToString();
            if (!mods.TryGetValue(modKey, out var modVal) || modVal == null || !modVal.IsDocument) return false;

            var mod = modVal.AsDocument();
            if (!mod.ContainsKey("Slots"))
                mod["Slots"] = BsonValue.FromArray(new BsonDocument()); // Slots is BSON array

            var slotsVal = mod["Slots"];
            if (!slotsVal.IsDocument) return false;
            var slots = slotsVal.AsDocument();

            string guid = Guid.NewGuid().ToString("N").ToUpper();

            // Attributes — BSON Array (type 0x04), same as game writes.
            // Only add the Level element when level > 0 (consumables/ammo have no level).
            // Field order: MaxValue → Tag → Value  (matches game format exactly).
            var attrsArr = new BsonDocument();
            if (level > 0)
            {
                var tagDoc = new BsonDocument();
                tagDoc["TagName"] = BsonValue.FromString("Inventory.Item.Attribute.Level");

                var attr0 = new BsonDocument();
                attr0["MaxValue"] = BsonValue.FromInt32(15);          // MaxValue first
                attr0["Tag"]      = BsonValue.FromDocument(tagDoc);   // then Tag
                attr0["Value"]    = BsonValue.FromInt32(level);       // Value last

                attrsArr["0"] = BsonValue.FromDocument(attr0);
            }

            // Item document — field order matches what the game writes.
            var itemDoc = new BsonDocument();
            itemDoc["Attributes"] = BsonValue.FromArray(attrsArr);            // Array, not Document
            itemDoc["Effects"]    = BsonValue.FromArray(new BsonDocument());  // empty Array
            itemDoc["ItemId"]     = BsonValue.FromString(guid);
            itemDoc["ItemParams"] = BsonValue.FromString(itemParams);
            if (quality > 0)
                itemDoc["QualityLevel"] = BsonValue.FromInt32(quality);

            // ItemsStack
            var stackDoc = new BsonDocument();
            stackDoc["Count"] = BsonValue.FromInt32(count);
            stackDoc["Item"]  = BsonValue.FromDocument(itemDoc);

            // Slot document
            var slotDoc = new BsonDocument();
            slotDoc["IsPersonalSlot"] = BsonValue.FromBool(false);
            slotDoc["ItemsStack"]     = BsonValue.FromDocument(stackDoc);
            slotDoc["SlotId"]         = BsonValue.FromInt32(slotIndex);
            slotDoc["SlotParams"]     = BsonValue.FromString(
                "/R5BusinessRules/Inventory/SlotsParams/DA_BL_Slot_Default.DA_BL_Slot_Default");

            slots[slotIndex.ToString()] = BsonValue.FromDocument(slotDoc);
            IsModified = true;
            return true;
        }

        // ──────────────────────────────────────────────────────────────────
        // Editing items
        // ──────────────────────────────────────────────────────────────────
        public bool EditItem(int moduleIndex, int slotIndex, int level, int count, int quality = 0)
        {
            var modsVal = Doc.Navigate("Inventory.Modules");
            if (modsVal == null || !modsVal.IsDocument) return false;

            var mods = modsVal.AsDocument();
            if (!mods.TryGetValue(moduleIndex.ToString(), out var modVal)
                || modVal == null || !modVal.IsDocument) return false;

            var mod = modVal.AsDocument();
            if (!mod.TryGetValue("Slots", out var sv) || sv == null || !sv.IsDocument) return false;
            var slots = sv.AsDocument();

            if (!slots.TryGetValue(slotIndex.ToString(), out var slotVal)
                || slotVal == null || !slotVal.IsDocument) return false;

            var slotDoc = slotVal.AsDocument();
            if (!slotDoc.TryGetValue("ItemsStack", out var stackVal)
                || stackVal == null || !stackVal.IsDocument) return false;

            var stack = stackVal.AsDocument();

            // Update count
            stack["Count"] = BsonValue.FromInt32(Math.Max(1, count));

            // Update level attribute
            if (stack.TryGetValue("Item", out var itemVal) && itemVal != null && itemVal.IsDocument)
            {
                var item = itemVal.AsDocument();

                // Записываем качество как прямое поле
                if (quality > 0)
                    item["QualityLevel"] = BsonValue.FromInt32(quality);

                if (item.TryGetValue("Attributes", out var attrsVal)
                    && attrsVal != null && attrsVal.IsDocument)
                {
                    foreach (var (_, av) in attrsVal.AsDocument())
                    {
                        if (!av.IsDocument) continue;
                        var attr = av.AsDocument();
                        if (!attr.TryGetValue("Tag", out var tagv) || tagv == null || !tagv.IsDocument) continue;
                        if (!tagv.AsDocument().TryGetValue("TagName", out var tn) || tn == null) continue;
                        string tagName = tn.AsString();
                        if (tagName.Contains("Level") && !tagName.Contains("Quality"))
                        {
                            attr["Value"] = BsonValue.FromInt32(level);
                        }
                        else if (tagName.Contains("Quality") && quality > 0)
                        {
                            attr["Value"] = BsonValue.FromInt32(quality);
                        }
                    }
                }
            }

            IsModified = true;
            return true;
        }

        // ──────────────────────────────────────────────────────────────────
        // Removing items
        // ──────────────────────────────────────────────────────────────────
        public bool RemoveItem(int moduleIndex, int slotIndex)
        {
            var modsVal = Doc.Navigate("Inventory.Modules");
            if (modsVal == null || !modsVal.IsDocument) return false;

            var mods = modsVal.AsDocument();
            if (!mods.TryGetValue(moduleIndex.ToString(), out var modVal) || modVal == null || !modVal.IsDocument)
                return false;
            var mod = modVal.AsDocument();
            if (!mod.TryGetValue("Slots", out var sv) || sv == null || !sv.IsDocument) return false;
            var slots    = sv.AsDocument();
            string slotKey = slotIndex.ToString();

            // Не удаляем запись слота из BSON — игра использует её наличие,
            // чтобы знать, что слот существует и должен отображаться в UI.
            // Вместо этого оставляем пустой ItemsStack (ItemParams = "").
            if (!slots.TryGetValue(slotKey, out var slotBsonVal) || slotBsonVal == null || !slotBsonVal.IsDocument)
                return false;

            var emptyItem = new BsonDocument();
            emptyItem["Attributes"] = BsonValue.FromArray(new BsonDocument());
            emptyItem["Effects"]    = BsonValue.FromArray(new BsonDocument());
            emptyItem["ItemId"]     = BsonValue.FromString("");
            emptyItem["ItemParams"] = BsonValue.FromString("");

            var emptyStack = new BsonDocument();
            emptyStack["Count"] = BsonValue.FromInt32(0);
            emptyStack["Item"]  = BsonValue.FromDocument(emptyItem);

            slotBsonVal.AsDocument()["ItemsStack"] = BsonValue.FromDocument(emptyStack);
            IsModified = true;
            return true;
        }

        // ──────────────────────────────────────────────────────────────────
        // Saving
        // ──────────────────────────────────────────────────────────────────
        public (bool ok, string error) Save()
        {
            byte[] newBson;
            try { newBson = BsonSerializer.Serialize(Doc); }
            catch (Exception ex) { return (false, $"Serialization error: {ex.Message}"); }

            var entries = new List<WalEntry>();
            entries.Add(new WalEntry(_raw.CfId, _raw.PlayerKey, newBson));

            foreach (var ship in _ships)
            {
                byte[] shipKey = System.Text.Encoding.ASCII.GetBytes(ship.Guid.ToUpperInvariant());
                if (ship.IsDeleted)
                {
                    entries.Add(new WalEntry(RocksDbAccess.CF_SHIP, shipKey, null));
                }
                else if (ship.IsNew)
                {
                    try
                    {
                        byte[] shipBson = BsonSerializer.Serialize(ship.RawDoc);
                        entries.Add(new WalEntry(RocksDbAccess.CF_SHIP, shipKey, shipBson));
                    }
                    catch (Exception ex) { return (false, $"Ship serialization error: {ex.Message}"); }
                }
            }

            var (manifestSeq, nextFileNum, _) = RocksDbAccess.ParseManifest(_raw.SaveDir);

            // Use the sequence from where we actually read the data (WAL batch header or
            // SST placeholder), not the manifest's LastSequence — the manifest is only
            // updated on compaction/flush and can lag behind the current WAL.
            // _raw.Sequence == 99999 means we loaded from SST; fall back to manifest seq.
            long baseSeq  = (_raw.Sequence > 0 && _raw.Sequence != 99999)
                            ? _raw.Sequence
                            : (manifestSeq > 0 ? manifestSeq : 50000);
            long writeSeq = baseSeq + 1;

            // nextFileNum is the MANIFEST's global counter — guaranteed not to conflict
            // with any existing SST or WAL file numbers.  Pass 0 only as a last resort
            // (ParseManifest returns 0 when it can't read the manifest), in which case
            // WriteWal falls back to scanning ALL numbered files in the directory.
            bool ok = RocksDbAccess.WriteWalMulti(_raw.SaveDir, writeSeq, nextFileNum, entries);
            if (!ok) return (false, "Failed to write WAL file. Check permissions.");

            // Reset ship flags
            _ships.RemoveAll(s => s.IsDeleted);
            foreach (var ship in _ships) ship.IsNew = false;

            IsModified = false;
            return (true, "");
        }

        public string CreateBackup()
        {
            // Walk up to find a Steam ID folder (numeric, ≥10 digits).
            // Safeguard: never back up a folder fewer than 3 path segments from root
            // (e.g. never backup C:\ or C:\Users).
            string root      = _raw.SaveDir;
            string foundRoot = root;   // fallback = SaveDir itself

            for (int i = 0; i < 6; i++)
            {
                string? parent = Path.GetDirectoryName(root);
                if (parent == null || parent == root) break;

                // Don't climb higher than 3 components from drive root
                // (Path.GetPathRoot("C:\\a\\b\\c") == "C:\\", so depth = segments after root)
                string driveRoot = Path.GetPathRoot(parent) ?? "";
                string relative  = Path.GetRelativePath(driveRoot, parent);
                int    depth     = relative == "." ? 0
                                   : relative.Split(Path.DirectorySeparatorChar).Length;
                if (depth < 2) break;  // refuse to back up C:\, C:\Users, etc.

                root = parent;
                string name = Path.GetFileName(root) ?? "";
                if (name.Length >= 10 && name.All(char.IsDigit))
                {
                    foundRoot = root;  // found a Steam ID folder
                    break;
                }
            }

            string ts        = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string rootName  = Path.GetFileName(foundRoot);
            if (string.IsNullOrEmpty(rootName)) rootName = "backup";
            string parentDir = Path.GetDirectoryName(foundRoot) ?? foundRoot;
            string backup    = Path.Combine(parentDir, $"{rootName}_backup_{ts}");

            try
            {
                int copied = 0, skipped = 0;
                CopyDirectory(foundRoot, backup, ref copied, ref skipped);
                string info = skipped > 0 ? $" ({skipped} файл(ов) пропущено)" : "";
                return backup + info;
            }
            catch (Exception ex) { return $"[BACKUP FAILED: {ex.Message}]"; }
        }

        static void CopyDirectory(string src, string dst, ref int copied, ref int skipped)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
            {
                string name = Path.GetFileName(f);
                // Skip RocksDB lock and Windows system temp files
                if (name == "LOCK" || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    File.Copy(f, Path.Combine(dst, name), overwrite: true);
                    copied++;
                }
                catch { skipped++; }  // locked / access denied → skip silently
            }
            foreach (var d in Directory.GetDirectories(src))
            {
                try { CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)), ref copied, ref skipped); }
                catch { skipped++; }
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Helpers
        // ──────────────────────────────────────────────────────────────────
        static int GetModuleCapacity(BsonDocument mod)
        {
            int total = 0;

            if (mod.TryGetValue("AdditionalSlotsData", out var asd) && asd != null && asd.IsDocument)
            {
                foreach (var (_, v) in asd.AsDocument())
                {
                    if (!v.IsDocument) continue;
                    if (v.AsDocument().TryGetValue("CountSlots", out var cs) && cs != null)
                        total += (int)cs.TryAsLong();
                }
            }

            if (mod.TryGetValue("ExtendCountSlots", out var ext) && ext != null)
                total += (int)ext.TryAsLong();

            if (mod.TryGetValue("Slots", out var sv) && sv != null && sv.IsDocument)
            {
                var keys = sv.AsDocument()
                             .Select(kv => int.TryParse(kv.Key, out int k) ? k : -1)
                             .Where(k => k >= 0)
                             .ToList();
                if (keys.Count > 0) total = Math.Max(total, keys.Max() + 1);
            }

            return total > 0 ? total : 8;
        }

        static int CountUsedSlots(BsonDocument mod)
        {
            if (!mod.TryGetValue("Slots", out var sv) || sv == null || !sv.IsDocument) return 0;
            int used = 0;
            foreach (var (_, slotVal) in sv.AsDocument())
            {
                if (!slotVal.IsDocument) continue;
                var st = slotVal.AsDocument().Navigate("ItemsStack.Item.ItemParams");
                if (st != null && !string.IsNullOrEmpty(st.AsString())) used++;
            }
            return used;
        }

        /// <summary>
        /// Extracts the ship type folder from a ship params path.
        /// "/R5BusinessRules/Ship/Ketch/DA_Ship_Ketch.DA_Ship_Ketch" → "Ketch"
        /// </summary>
        static string ShipTypeFolder(string shipParams)
        {
            var parts = shipParams.Split('/');
            for (int i = 0; i < parts.Length - 1; i++)
                if (string.Equals(parts[i], "Ship", StringComparison.OrdinalIgnoreCase))
                    return parts[i + 1];
            return "";
        }

        /// <summary>
        /// Returns true if the module contains customization items (sail / flag / hull color).
        /// These slots must be kept so the game can render the ship.
        /// </summary>
        static bool IsCustomizationModule(BsonDocument mod)
        {
            if (!mod.TryGetValue("Slots", out var sv) || sv == null || !sv.IsDocument) return false;
            foreach (var (_, slotVal) in sv.AsDocument())
            {
                if (!slotVal.IsDocument) continue;
                var path = slotVal.AsDocument().Navigate("ItemsStack.Item.ItemParams");
                if (path != null && path.Type == BsonType.String
                    && path.AsString().Contains("ShipCustomization", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Replaces the ship-type segment inside customization item asset paths so the
        /// new ship references its own type's assets instead of the template's.
        /// E.g. "DA_EID_ShipCustomization_Ketch_Sail_Stock_Origin"
        ///   → "DA_EID_ShipCustomization_Frigate_Sail_Stock_Origin"
        /// </summary>
        static void PatchCustomizationModuleItems(BsonDocument mod, string fromType, string toType)
        {
            if (!mod.TryGetValue("Slots", out var sv) || sv == null || !sv.IsDocument) return;
            foreach (var (_, slotVal) in sv.AsDocument())
            {
                if (!slotVal.IsDocument) continue;
                var itemDoc = slotVal.AsDocument().Navigate("ItemsStack.Item")?.AsDocument();
                if (itemDoc == null) continue;
                if (!itemDoc.TryGetValue("ItemParams", out var ipv) || ipv == null
                    || ipv.Type != BsonType.String) continue;
                string path = ipv.AsString();
                if (!path.Contains("ShipCustomization", StringComparison.OrdinalIgnoreCase)) continue;

                // Replace both the folder segment and the DA_ asset name segment.
                // Folder:  "/ShipCustomization/Ketch/"  → "/ShipCustomization/Frigate/"
                // Asset name: "_Ketch_" → "_Frigate_"
                string newPath = path
                    .Replace($"/ShipCustomization/{fromType}/",
                             $"/ShipCustomization/{toType}/",
                             StringComparison.OrdinalIgnoreCase)
                    .Replace($"ShipCustomization_{fromType}_",
                             $"ShipCustomization_{toType}_",
                             StringComparison.OrdinalIgnoreCase);

                if (newPath != path)
                    itemDoc["ItemParams"] = BsonValue.FromString(newPath);
            }
        }

        static string ResolveSaveDir(string path)
        {
            path = Path.GetFullPath(path);
            if (File.Exists(Path.Combine(path, "CURRENT"))) return path;

            try
            {
                // Search for Players/<GUID> pattern
                foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
                {
                    string parentName = Path.GetFileName(Path.GetDirectoryName(dir) ?? "");
                    if (parentName.Equals("Players", StringComparison.OrdinalIgnoreCase)
                        && File.Exists(Path.Combine(dir, "CURRENT")))
                        return dir;
                }

                // Any dir with CURRENT + .log files
                foreach (var cur in Directory.EnumerateFiles(path, "CURRENT", SearchOption.AllDirectories))
                {
                    string dir = Path.GetDirectoryName(cur)!;
                    if (Directory.GetFiles(dir, "*.log").Length > 0) return dir;
                }
            }
            catch { }

            return path;
        }
    }
}
