using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace WindroseEditor
{
    // ─────────────────────────────────────────────────────────────────────────
    // Theme
    // ─────────────────────────────────────────────────────────────────────────
    static class Theme
    {
        public static readonly Color BG        = Color.FromArgb(22, 25, 31);
        public static readonly Color BG2       = Color.FromArgb(15, 17, 22);
        public static readonly Color SlotBg    = Color.FromArgb(28, 32, 40);
        public static readonly Color SlotBgHov = Color.FromArgb(36, 41, 52);
        public static readonly Color SlotBord  = Color.FromArgb(48, 55, 68);
        public static readonly Color SlotBordH = Color.FromArgb(75, 90, 110);
        public static readonly Color HdrBg     = Color.FromArgb(18, 21, 27);
        public static readonly Color HdrText   = Color.FromArgb(160, 175, 195);
        public static readonly Color Text      = Color.FromArgb(210, 220, 235);
        public static readonly Color Dim       = Color.FromArgb(90, 105, 125);
        public static readonly Color Accent    = Color.FromArgb(0,  200, 255);
        public static readonly Color Warn      = Color.FromArgb(255, 180, 50);

        public const int SlotSz  = 68;
        public const int SlotGap = 3;
        public const int Pad     = 8;
        public const int HdrH    = 24;

        public static Color Rarity(string r) => r switch
        {
            "Legendary" => Color.FromArgb(249, 115,  22),
            "Epic"      => Color.FromArgb(168,  85, 247),
            "Rare"      => Color.FromArgb( 59, 130, 246),
            "Uncommon"  => Color.FromArgb( 34, 197,  94),
            "Common"    => Color.FromArgb(148, 163, 184),
            _           => Color.FromArgb( 60,  80, 110),
        };

        // Section width given N columns
        public static int SecW(int cols) =>
            cols * SlotSz + (cols - 1) * SlotGap + Pad * 2;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Main window
    // ─────────────────────────────────────────────────────────────────────────
    public class MainForm : Form
    {
        TextBox _pathBox   = null!;
        Button  _loadBtn   = null!;
        Button  _saveBtn   = null!;
        Button  _dbBtn     = null!;
        Button  _langBtn   = null!;
        Label   _statusLbl = null!;

        // inventory panels
        InvSection _actionBarSec  = null!;
        InvSection _backpackSec   = null!;
        InvSection _equipSec      = null!;
        InvSection _accessorySec  = null!;
        InvSection _ammoSec       = null!;

        SaveFile? _save;
        readonly ToolTip _tip = new ToolTip { AutoPopDelay = 6000, InitialDelay = 400 };
        // Используется только для заголовков/кнопок; для предметов — ItemTooltip

        int _modBackpack = 0, _modActionBar = 3;
        int _modArmor    = 2, _modAccessory = 4, _modAmmo = 1;

        public MainForm()
        {
            Text          = "Windrose — Редактор инвентаря  v1.0.0";
            Size          = new Size(1000, 750);
            MinimumSize   = new Size(820, 560);
            BackColor     = Theme.BG2;
            ForeColor     = Theme.Text;
            Font          = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterScreen;

            Build();
            TryAutoLoadDb();
        }

        // ── UI ───────────────────────────────────────────────────────────────
        void Build()
        {
            // Top bar
            var top = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Theme.BG2 };

            _pathBox = new TextBox
            {
                PlaceholderText = @"Путь к папке Players\<GUID>",
                BackColor = Theme.SlotBg, ForeColor = Theme.Text,
                BorderStyle = BorderStyle.FixedSingle, Font = Font,
            };

            var browseBtn = Btn("…", 28, Theme.Dim, Theme.SlotBg);
            browseBtn.Click += (_, _) => Browse();

            _loadBtn = Btn("Загрузить", 90, Theme.Accent, Color.FromArgb(0, 45, 70));
            _loadBtn.Click += (_, _) => Load();

            _saveBtn = Btn("Сохранить", 90, Theme.Accent, Color.FromArgb(0, 55, 85));
            _saveBtn.Enabled = false;
            _saveBtn.Click   += (_, _) => Save();

            // Кнопка базы данных предметов (правый край)
            _dbBtn = Btn("База предметов…", 130, Theme.Dim, Theme.SlotBg);
            _dbBtn.Click += (_, _) => LoadDb();

            // Кнопка переключения языка
            _langBtn = Btn("RU", 36, Theme.Accent, Color.FromArgb(0, 45, 70));
            _langBtn.Click += (_, _) => ToggleLanguage();

            top.Controls.AddRange(new Control[] { _pathBox, browseBtn, _loadBtn, _saveBtn, _langBtn, _dbBtn });
            top.Layout += (_, _) =>
            {
                int y = (top.ClientSize.Height - 24) / 2;
                int r = top.ClientSize.Width - 10;
                _dbBtn.Location     = new Point(r - _dbBtn.Width, y);
                _langBtn.Location   = new Point(_dbBtn.Left - _langBtn.Width - 6, y);
                _saveBtn.Location   = new Point(_langBtn.Left - _saveBtn.Width - 8, y);
                _loadBtn.Location   = new Point(_saveBtn.Left - _loadBtn.Width - 4, y);
                browseBtn.Location  = new Point(_loadBtn.Left - browseBtn.Width - 6, y);
                _pathBox.Location   = new Point(10, y + 1);
                _pathBox.Width      = browseBtn.Left - 14;
                _pathBox.Height     = 24;
            };

            // Status bar
            var bot = new Panel { Dock = DockStyle.Bottom, Height = 24, BackColor = Theme.BG2 };
            _statusLbl = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Theme.Dim, Font = new Font("Segoe UI", 8f),
                Padding = new Padding(8, 0, 0, 0),
            };
            bot.Controls.Add(_statusLbl);

            // Scrollable content
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.BG2 };

            // Sections (заголовки обновляются через UpdateUiLanguage)
            _actionBarSec = new InvSection("");
            _backpackSec  = new InvSection("");
            _equipSec     = new InvSection("");
            _accessorySec = new InvSection("");
            _ammoSec      = new InvSection("");

            // Right column
            var right = new Panel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.BG2 };
            right.Controls.Add(_ammoSec);
            right.Controls.Add(_accessorySec);
            right.Controls.Add(_equipSec);
            right.Layout += (_, _) =>
            {
                int y = 0;
                foreach (Control c in new Control[] { _equipSec, _accessorySec, _ammoSec })
                {
                    c.Location = new Point(0, y);
                    y += c.Height + 6;
                }
                right.Height = y;
                right.Width  = Math.Max(_equipSec.Width, Math.Max(_accessorySec.Width, _ammoSec.Width));
            };

            // Inventory canvas inside scroll
            var canvas = new Panel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.BG2 };
            canvas.Controls.Add(right);
            canvas.Controls.Add(_backpackSec);
            canvas.Controls.Add(_actionBarSec);
            canvas.Layout += (_, _) => LayoutCanvas(canvas, right);

            scroll.Controls.Add(canvas);

            Controls.Add(scroll);
            Controls.Add(top);
            Controls.Add(bot);

            UpdateUiLanguage();   // проставить заголовки/кнопки при старте
        }

        void LayoutCanvas(Panel canvas, Panel right)
        {
            int pad = 10;
            _actionBarSec.Location  = new Point(pad, pad);

            int bagY = _actionBarSec.Bottom + 6;
            _backpackSec.Location   = new Point(pad, bagY);

            right.Location = new Point(_backpackSec.Right + 6, bagY);

            canvas.Width  = right.Right + pad;
            canvas.Height = Math.Max(_backpackSec.Bottom, right.Bottom) + pad;
        }

        // ── DB auto-load ─────────────────────────────────────────────────────
        void TryAutoLoadDb()
        {
            string[] searchDirs =
            {
                AppDomain.CurrentDomain.BaseDirectory,
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..")),
            };

            // 1. windrose_items.db — SQLite, основной формат
            foreach (string dir in searchDirs)
            {
                string dbPath = Path.Combine(dir, "windrose_items.db");
                if (!File.Exists(dbPath)) continue;

                ApplySqliteDb(dbPath);
                return;
            }

            // 2. item_db.json — JSON, устаревший формат
            foreach (string dir in searchDirs)
            {
                string dbPath = Path.Combine(dir, "item_db.json");
                if (!File.Exists(dbPath)) continue;

                ApplyJsonDb(dbPath, dir);
                return;
            }

            // 3. Старый HTML
            foreach (string dir in searchDirs)
            {
                string htmlPath = Path.Combine(dir, "Item ID Database.html");
                if (!File.Exists(htmlPath)) continue;

                string e = ItemDatabase.Load(htmlPath);
                IconCache.Load(dir);
                string iconInfo = IconCache.MappedCount > 0
                    ? $" · {IconCache.MappedCount} иконок" : "";
                SetStatus(string.IsNullOrEmpty(e)
                    ? $"База (HTML): {ItemDatabase.Items.Count} предметов{iconInfo}"
                    : e, warn: !string.IsNullOrEmpty(e));
                return;
            }

            // Ничего не нашли — объясняем где взять
            string hint = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "windrose_items.db"));
            SetStatus(
                $"База предметов не найдена. Нажми «База предметов…» или запусти IconExtractor.exe" +
                $"  (ожидается: {hint})",
                warn: true);
        }

        // Открывает диалог выбора windrose_items.db / item_db.json
        void LoadDb()
        {
            string startDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (string d in new[]
            {
                AppDomain.CurrentDomain.BaseDirectory,
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..")),
            })
            {
                if (File.Exists(Path.Combine(d, "windrose_items.db")) ||
                    File.Exists(Path.Combine(d, "item_db.json")))
                { startDir = d; break; }
            }

            using var ofd = new OpenFileDialog
            {
                Title            = "Выберите windrose_items.db или item_db.json",
                Filter           = "База предметов|windrose_items.db;item_db.json|SQLite|*.db|JSON|*.json|Все файлы|*.*",
                InitialDirectory = startDir,
            };

            if (ofd.ShowDialog(this) != DialogResult.OK) return;

            string ext   = Path.GetExtension(ofd.FileName).ToLowerInvariant();
            string dbDir = Path.GetDirectoryName(ofd.FileName) ?? startDir;

            if (ext == ".db")
                ApplySqliteDb(ofd.FileName);
            else
                ApplyJsonDb(ofd.FileName, dbDir);

            if (_save != null) Rebuild();
        }

        // ── Загрузка из windrose_items.db (SQLite) ────────────────────────
        void ApplySqliteDb(string dbPath)
        {
            string e  = ItemDatabase.LoadFromSqlite(dbPath);
            string ce = CurveDb.LoadFromSqlite(dbPath);
            IconCache.LoadFromSqlite(dbPath);

            string iconInfo  = IconCache.MappedCount > 0
                ? $" · {IconCache.MappedCount} иконок" : " · иконок нет";
            string curveInfo = CurveDb.Loaded
                ? $" · {CurveDb.TableCount} CT ({CurveDb.RowCount} строк)" : "";

            string err = !string.IsNullOrEmpty(e) ? e : ce;
            SetStatus(string.IsNullOrEmpty(err)
                ? $"База: {ItemDatabase.Items.Count} предметов{iconInfo}{curveInfo}"
                : err, warn: !string.IsNullOrEmpty(err));
        }

        // ── Загрузка из item_db.json + curves_db.json (JSON, legacy) ──────
        void ApplyJsonDb(string dbPath, string baseDir)
        {
            string e  = ItemDatabase.LoadFromDb(dbPath);
            string ce = CurveDb.Load(Path.Combine(baseDir, "curves_db.json"));
            IconCache.Load(baseDir);

            string iconInfo  = IconCache.MappedCount > 0
                ? $" · {IconCache.MappedCount} иконок"
                : " · иконок нет (нажми «База предметов…» чтобы выбрать windrose_items.db)";
            string curveInfo = CurveDb.Loaded
                ? $" · {CurveDb.TableCount} CT" : "";

            SetStatus(string.IsNullOrEmpty(e)
                ? $"База (JSON): {ItemDatabase.Items.Count} предметов{iconInfo}{curveInfo}"
                : e, warn: !string.IsNullOrEmpty(e));
        }

        void Browse()
        {
            using var d = new FolderBrowserDialog
            {
                Description = "Выберите папку Players\\<GUID>",
                UseDescriptionForTitle = true,
            };
            if (_pathBox.Text.Length > 3) d.InitialDirectory = _pathBox.Text;
            if (d.ShowDialog() == DialogResult.OK) _pathBox.Text = d.SelectedPath;
        }

        new void Load()
        {
            string path = _pathBox.Text.Trim();
            if (string.IsNullOrEmpty(path)) { SetStatus("Введите путь к сохранению.", warn: true); return; }

            SetStatus("Загружаю…");
            Cursor = Cursors.WaitCursor;
            try
            {
                var (file, err) = SaveFile.Load(path);
                if (file == null) { SetStatus(err, warn: true); return; }
                _save = file;
                SetStatus($"Загружено  ·  {_save.PlayerGuid}");
                DetectRoles();
                Rebuild();
                _saveBtn.Enabled = true;
            }
            finally { Cursor = Cursors.Default; }
        }

        void Save()
        {
            if (_save == null) return;
            if (!_save.IsModified) { SetStatus("Нет изменений."); return; }

            if (MessageBox.Show(
                    AppLanguage.T(
                        "Игра должна быть закрыта!\n\nСоздать резервную копию и записать изменения?",
                        "The game must be closed!\n\nCreate a backup and save changes?"),
                    AppLanguage.T("Сохранить", "Save"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            string bk = _save.CreateBackup();
            var (ok, err) = _save.Save();
            SetStatus(ok ? $"Сохранено. Бэкап: {bk}" : $"Ошибка: {err}", warn: !ok);
            if (!ok) MessageBox.Show(err, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        // ── Module role detection ─────────────────────────────────────────────
        void DetectRoles()
        {
            if (_save == null) return;
            var mods = _save.GetModules();
            _modBackpack = _modActionBar = _modArmor = _modAccessory = _modAmmo = -1;

            // Sample the first non-empty slot of each module once
            var samples = mods.ToDictionary(
                m => m.Index,
                m => _save.GetSlots(m.Index)
                          .FirstOrDefault(s => !s.IsEmpty)?.ItemParams.ToLowerInvariant() ?? "");

            // Pass 1 — path-certain roles (only assign equipment roles to SMALL modules;
            //          large capacity = backpack even if it contains ammo/armor items)
            const int MaxEquipCap = 20;   // ammo/armor/accessories are always tiny (2–8 slots)
            foreach (var m in mods)
            {
                string s = samples[m.Index];
                if      (_modAmmo      < 0 && m.Capacity <= MaxEquipCap && s.Contains("/ammo/"))                              _modAmmo      = m.Index;
                else if (_modArmor     < 0 && m.Capacity <= MaxEquipCap && s.Contains("/armor/"))                             _modArmor     = m.Index;
                else if (_modAccessory < 0 && m.Capacity <= MaxEquipCap && (s.Contains("/ring/") || s.Contains("/necklace/"))) _modAccessory = m.Index;
                else if (_modActionBar < 0 && m.Capacity == 8 &&
                         (s.Contains("/weapon/") || s.Contains("/alchemy/") || s.Contains("/food/")
                          || s.Contains("/consumable/") || s == ""))                                                           _modActionBar = m.Index;
            }

            // Pass 2 — backpack = largest capacity module not already assigned to another role
            var taken = new HashSet<int> { _modAmmo, _modArmor, _modAccessory, _modActionBar };
            // Exclude modules that clearly are NOT the player backpack
            static bool LooksLikeNonBackpack(string s) =>
                s.Contains("recipe") || s.Contains("/npc/") || s.Contains("shipcustom") ||
                s.Contains("/note") || s.Contains("/quest") || s.Contains("/journal") ||
                s.Contains("/letter") || s.Contains("/map") || s.Contains("/cosmetic");

            var candidate = mods
                .Where(m => !taken.Contains(m.Index) && !LooksLikeNonBackpack(samples[m.Index]))
                .OrderByDescending(m => m.Capacity)
                .FirstOrDefault();
            if (candidate != null) _modBackpack = candidate.Index;

            // Pass 3 — if action bar still missing, pick capacity-8 module closest to index 3
            if (_modActionBar < 0)
            {
                var abCandidate = mods
                    .Where(m => m.Capacity == 8 && m.Index != _modBackpack)
                    .OrderBy(m => Math.Abs(m.Index - 3))
                    .FirstOrDefault();
                if (abCandidate != null) _modActionBar = abCandidate.Index;
            }

            // Hard fallbacks (keep existing save structure guesses)
            if (_modBackpack  < 0 && mods.Count > 0) _modBackpack  = mods[0].Index;
            if (_modActionBar < 0 && mods.Count > 3) _modActionBar = mods[3].Index;
            if (_modArmor     < 0 && mods.Count > 2) _modArmor     = mods[2].Index;
            if (_modAccessory < 0 && mods.Count > 4) _modAccessory = mods[4].Index;
            if (_modAmmo      < 0 && mods.Count > 1) _modAmmo      = mods[1].Index;
        }

        void Rebuild()
        {
            if (_save == null) return;
            Fill(_actionBarSec, _modActionBar, 8);
            Fill(_backpackSec,  _modBackpack,  8);
            // Фиксированные секции: запрещены добавление и редактирование, только удаление
            Fill(_equipSec,     _modArmor,     2,
                 fixedCount: SlotConstraints.ArmorSlotCount,     readOnly: true,
                 getSlotName: SlotConstraints.GetArmorName);
            Fill(_accessorySec, _modAccessory, 2,
                 fixedCount: SlotConstraints.AccessorySlotCount, readOnly: true,
                 getSlotName: SlotConstraints.GetAccessoryName);
            Fill(_ammoSec,      _modAmmo,      2,
                 fixedCount: SlotConstraints.AmmoSlotCount,      readOnly: true,
                 getSlotName: SlotConstraints.GetAmmoName);

            // trigger layout refresh
            _backpackSec.Parent?.PerformLayout();
        }

        /// <summary>
        /// Заполняет секцию инвентаря слотами из модуля <paramref name="mod"/>.
        /// <para>Если <paramref name="fixedCount"/> &gt; 0, всегда отображается ровно такое
        /// количество ячеек (0…fixedCount-1), независимо от того, сколько слотов вернул
        /// GetSlots — решает проблему «лишних» пустых слотов после удаления предмета.</para>
        /// <para>Если <paramref name="readOnly"/> = true — добавление и редактирование
        /// заблокированы, только удаление.</para>
        /// </summary>
        void Fill(InvSection sec, int mod, int cols,
                  int fixedCount = 0, bool readOnly = false,
                  Func<int, string?>? getSlotName = null)
        {
            InventorySlot[] slots;

            if (mod < 0 || _save == null)
            {
                // Нет модуля — создаём пустые заглушки нужного количества
                int n = fixedCount > 0 ? fixedCount : 0;
                slots = new InventorySlot[n];
                int safeMod = mod < 0 ? 0 : mod;
                for (int i = 0; i < n; i++)
                    slots[i] = new InventorySlot { ModuleIndex = safeMod, SlotIndex = i };
            }
            else if (fixedCount > 0)
            {
                // Фиксированный размер: берём данные сохранения, но ровно fixedCount ячеек
                var saveSlots = _save.GetSlots(mod)
                    .Where(s => s.SlotIndex < fixedCount)
                    .ToDictionary(s => s.SlotIndex);
                slots = new InventorySlot[fixedCount];
                for (int i = 0; i < fixedCount; i++)
                    slots[i] = saveSlots.TryGetValue(i, out var s)
                        ? s
                        : new InventorySlot { ModuleIndex = mod, SlotIndex = i };
            }
            else
            {
                slots = _save.GetSlots(mod).ToArray();
            }

            sec.SetSlots(slots, cols, _tip,
                readOnly ? null          : (Action<InventorySlot>)(s => AddItem(s)),
                s => RemoveItem(s),
                readOnly ? null          : (Action<InventorySlot>)(s => EditItem(s)),
                getSlotName);
        }

        void AddItem(InventorySlot slot)
        {
            if (_save == null) return;
            if (!ItemDatabase.Loaded)
            {
                using var ofd = new OpenFileDialog
                {
                    Title = "Выберите 'Item ID Database.html'",
                    Filter = "HTML|*.html|Все|*.*",
                };
                if (ofd.ShowDialog() != DialogResult.OK) return;
                string e = ItemDatabase.Load(ofd.FileName);
                if (!string.IsNullOrEmpty(e)) { SetStatus(e, warn: true); return; }
            }

            using var dlg = new AddItemDialog(slot.ModuleIndex, slot.SlotIndex);
            if (dlg.ShowDialog(this) != DialogResult.OK || dlg.SelectedItem == null) return;

            if (!_save.AddItem(slot.ModuleIndex, slot.SlotIndex,
                               dlg.SelectedItem.ItemParamsPath,
                               dlg.SelectedLevel, dlg.SelectedCount, dlg.SelectedQuality))
            { SetStatus("Не удалось добавить предмет.", warn: true); return; }

            string qualInfo = dlg.SelectedQuality > 0 ? $"  кач.{dlg.SelectedQuality}" : "";
            SetStatus($"Добавлено: {dlg.SelectedItem.DisplayName}  ур.{dlg.SelectedLevel}{qualInfo}  ×{dlg.SelectedCount}");
            DetectRoles();
            Rebuild();
        }

        void RemoveItem(InventorySlot slot)
        {
            if (_save == null) return;
            string msg = AppLanguage.T(
                $"Удалить '{slot.InternalName}' из слота {slot.SlotIndex}?",
                $"Remove '{slot.InternalName}' from slot {slot.SlotIndex}?");
            string cap = AppLanguage.T("Удалить", "Remove");
            if (MessageBox.Show(msg, cap, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            _save.RemoveItem(slot.ModuleIndex, slot.SlotIndex);
            SetStatus(AppLanguage.T($"Удалено: слот {slot.SlotIndex}", $"Removed: slot {slot.SlotIndex}"));
            DetectRoles();
            Rebuild();
        }

        void EditItem(InventorySlot slot)
        {
            if (_save == null) return;
            var item = ItemDatabase.Items.FirstOrDefault(i =>
                i.ItemParamsPath.Equals(slot.ItemParams, StringComparison.OrdinalIgnoreCase));

            using var dlg = new EditItemDialog(slot, item);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            if (!_save.EditItem(slot.ModuleIndex, slot.SlotIndex,
                                dlg.SelectedLevel, dlg.SelectedCount, dlg.SelectedQuality))
            {
                SetStatus("Не удалось изменить предмет.", warn: true);
                return;
            }

            string name     = item?.DisplayName ?? slot.InternalName;
            string qualInfo = dlg.SelectedQuality > 0 ? $"  кач.{dlg.SelectedQuality}" : "";
            SetStatus($"Изменено: {name}  ур.{dlg.SelectedLevel}{qualInfo}  ×{dlg.SelectedCount}");
            Rebuild();
        }

        void ToggleLanguage()
        {
            AppLanguage.IsRu = !AppLanguage.IsRu;

            // Скрываем живой тултип (содержимое изменится)
            ItemTooltip.Instance.Hide();

            UpdateUiLanguage();

            // Обновляем инвентарь (пересоздаёт SlotCell с актуальными именами/меню)
            if (_save != null) Rebuild();
        }

        /// <summary>
        /// Обновляет все статические строки интерфейса под текущий язык.
        /// Вызывается при старте и при каждом переключении языка.
        /// </summary>
        void UpdateUiLanguage()
        {
            // ── Кнопка языка ──────────────────────────────────────────────────
            _langBtn.Text      = AppLanguage.Current;
            _langBtn.BackColor = AppLanguage.IsRu
                ? Color.FromArgb(0, 45, 70)
                : Color.FromArgb(50, 35, 0);
            _langBtn.ForeColor = AppLanguage.IsRu ? Theme.Accent : Theme.Warn;

            // ── Заголовок окна ───────────────────────────────────────────────
            Text = $"Windrose — {AppLanguage.T("Редактор инвентаря", "Inventory Editor")}  v1.0.0";

            // ── Кнопки тулбара ────────────────────────────────────────────────
            _loadBtn.Text = AppLanguage.T("Загрузить", "Load");
            _saveBtn.Text = AppLanguage.T("Сохранить", "Save");
            _dbBtn.Text   = AppLanguage.T("База предметов…", "Item Database…");

            // ── Placeholder путевого поля ─────────────────────────────────────
            _pathBox.PlaceholderText = AppLanguage.T(
                @"Путь к папке Players\<GUID>",
                @"Path to Players\<GUID> folder");

            // ── Заголовки секций инвентаря ────────────────────────────────────
            _actionBarSec.Title  = AppLanguage.T("Панель действий",  "Action Bar");
            _backpackSec.Title   = AppLanguage.T("Рюкзак",           "Backpack");
            _equipSec.Title      = AppLanguage.T("Снаряжение",       "Equipment");
            _accessorySec.Title  = AppLanguage.T("Аксессуары",       "Accessories");
            _ammoSec.Title       = AppLanguage.T("Боеприпасы",       "Ammo");
        }

        void SetStatus(string msg, bool warn = false)
        {
            _statusLbl.ForeColor = warn ? Theme.Warn : Theme.Dim;
            _statusLbl.Text      = msg;
        }

        static Button Btn(string t, int w, Color fg, Color bg) => new Button
        {
            Text = t, Width = w, Height = 24,
            ForeColor = fg, BackColor = bg,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8.5f),
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // InvSection — labelled block containing a grid of slot cells
    // ─────────────────────────────────────────────────────────────────────────
    class InvSection : Panel
    {
        readonly Label _lbl;
        Panel? _grid;
        int _cols;

        public string Title { get => _lbl.Text; set => _lbl.Text = value; }

        public InvSection(string title)
        {
            AutoSize     = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor    = Theme.BG;
            Padding      = new Padding(0);

            _lbl = new Label
            {
                Text      = title,
                Dock      = DockStyle.Top,
                Height    = Theme.HdrH,
                BackColor = Theme.HdrBg,
                ForeColor = Theme.HdrText,
                Font      = new Font("Segoe UI", 8f),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding   = new Padding(Theme.Pad, 0, 0, 0),
            };
            Controls.Add(_lbl);
        }

        public void SetSlots(InventorySlot[] slots, int cols, ToolTip tip,
                             Action<InventorySlot>? onAdd,
                             Action<InventorySlot> onRemove,
                             Action<InventorySlot>? onEdit = null,
                             Func<int, string?>? getSlotName = null)
        {
            _cols = cols;
            if (_grid != null) { Controls.Remove(_grid); _grid.Dispose(); }

            _grid = new Panel { BackColor = Theme.BG };

            int step = Theme.SlotSz + Theme.SlotGap;

            for (int i = 0; i < slots.Length; i++)
            {
                int col      = i % cols;
                int row      = i / cols;
                string? name = getSlotName?.Invoke(slots[i].SlotIndex);
                var sp       = new SlotCell(slots[i], tip,
                                   slotName: name,
                                   addable:  onAdd  != null,
                                   editable: onEdit != null);
                sp.Location = new Point(Theme.Pad + col * step, Theme.Pad + row * step);
                if (onAdd  != null) sp.OnAdd  += onAdd;
                sp.OnRemove += onRemove;
                if (onEdit != null) sp.OnEdit += onEdit;
                _grid.Controls.Add(sp);
            }

            int rows = slots.Length == 0 ? 1 : (int)Math.Ceiling(slots.Length / (double)cols);
            _grid.Width  = Theme.Pad * 2 + cols * step - Theme.SlotGap;
            _grid.Height = Theme.Pad * 2 + rows * step - Theme.SlotGap;
            _grid.Location = new Point(0, Theme.HdrH);

            Controls.Add(_grid);
            Width  = _grid.Width;
            Height = Theme.HdrH + _grid.Height;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var p = new Pen(Color.FromArgb(40, 50, 65), 1f);
            e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SlotCell — single inventory square
    // ─────────────────────────────────────────────────────────────────────────
    class SlotCell : Panel
    {
        public event Action<InventorySlot>? OnAdd;
        public event Action<InventorySlot>? OnRemove;
        public event Action<InventorySlot>? OnEdit;

        readonly InventorySlot _slot;
        readonly ItemEntry?    _item;
        readonly string        _name;
        readonly Color         _rarCol;
        readonly Image?        _icon;
        readonly string?       _slotName;  // отображаемое имя типа слота (для пустых ячеек)
        readonly bool          _addable;   // разрешено ли добавление (false = ячейка readonly)
        bool _hover;

        public SlotCell(InventorySlot slot, ToolTip tip,
                        string? slotName = null,
                        bool addable     = true,
                        bool editable    = true)
        {
            _slot          = slot;
            _slotName      = slotName;
            _addable       = addable;
            Size           = new Size(Theme.SlotSz, Theme.SlotSz);
            DoubleBuffered = true;

            if (!slot.IsEmpty)
            {
                _item   = ItemDatabase.Items.FirstOrDefault(i =>
                    i.ItemParamsPath.Equals(slot.ItemParams, StringComparison.OrdinalIgnoreCase));
                _name   = _item?.DisplayName ?? slot.InternalName;
                _rarCol = Theme.Rarity(_item?.Rarity ?? "");
                _icon   = _item != null ? IconCache.Get(_item.IconRef) : null;

                var ctx = new ContextMenuStrip { BackColor = Theme.SlotBg, ForeColor = Theme.Text };
                if (editable)
                    ctx.Items.Add(AppLanguage.T("Редактировать", "Edit")).Click   += (_, _) => OnEdit?.Invoke(_slot);
                ctx.Items.Add(AppLanguage.T("Удалить", "Delete")).Click += (_, _) => OnRemove?.Invoke(_slot);
                ContextMenuStrip = ctx;
            }
            else
            {
                _name   = "";
                _rarCol = Color.Empty;
                if (addable) Cursor = Cursors.Hand;
            }

            MouseEnter += (_, e) =>
            {
                _hover = true;
                Invalidate();
                var screenPt = PointToScreen(new Point(Width, 0));
                ItemTooltip.Instance.ShowFor(_slot, _item, screenPt);
            };
            MouseLeave += (_, _) =>
            {
                _hover = false;
                Invalidate();
                ItemTooltip.Instance.Hide();
            };
            Click       += (_, _) => { if (_slot.IsEmpty && _addable) OnAdd?.Invoke(_slot); };
            DoubleClick += (_, _) => { if (!_slot.IsEmpty) OnEdit?.Invoke(_slot); };
            Paint       += OnPaint;
        }

        void OnPaint(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;

            int w = Theme.SlotSz, h = Theme.SlotSz;
            Color bg   = _hover ? Theme.SlotBgHov : Theme.SlotBg;
            Color bord = _hover ? Theme.SlotBordH  : Theme.SlotBord;

            // Background
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, 0, 0, w, h);
            // Border
            using (var p = new Pen(bord)) g.DrawRectangle(p, 0, 0, w - 1, h - 1);

            if (_slot.IsEmpty)
            {
                if (_slotName != null)
                {
                    // Название типа слота — по центру ячейки
                    using var dimBr = new SolidBrush(Color.FromArgb(_hover ? 80 : 45, Theme.Text));
                    using var fnt   = new Font("Segoe UI", 7.5f);
                    var sf = new StringFormat
                        { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString(_slotName, fnt, dimBr,
                        new System.Drawing.RectangleF(2, 0, w - 4, h - 4), sf);
                }

                if (_hover && _addable)
                {
                    // «+» иконка при наведении (только для редактируемых секций)
                    using var plusBr = new SolidBrush(Color.FromArgb(130, Theme.Accent));
                    using var plusF  = new Font("Segoe UI", 18f, FontStyle.Bold);
                    var sf2 = new StringFormat
                        { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString("+", plusF, plusBr,
                        new System.Drawing.RectangleF(0, 0, w, h), sf2);
                }
            }
            else
            {
                // Icon area
                int  isz = 38;
                int  ix  = (w - isz) / 2;
                int  iy  = 6;
                var  ir  = new Rectangle(ix, iy, isz, isz);

                // Rarity tint behind icon
                using (var ib = new SolidBrush(Color.FromArgb(35, _rarCol)))
                    g.FillRectangle(ib, ir);

                if (_icon != null)
                {
                    // Реальная PNG иконка из игры
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(_icon, ir);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                }
                else
                {
                    // Fallback: первая буква названия
                    string ltr = _name.Length > 0 ? _name[0].ToString().ToUpper() : "?";
                    using var fnt = new Font("Segoe UI", 15f, FontStyle.Bold);
                    using var fb  = new SolidBrush(Color.FromArgb(200, _rarCol));
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString(ltr, fnt, fb, ir, sf);
                }

                // Item name (truncated, bottom)
                if (_name.Length > 0)
                {
                    string nm = _name.Length > 10 ? _name[..10] + "…" : _name;
                    using var nfnt = new Font("Segoe UI", 6.5f);
                    using var nbr  = new SolidBrush(Color.FromArgb(160, Theme.Text));
                    var nr = new RectangleF(1, h - 14, w - 2, 13);
                    var nsf = new StringFormat { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
                    g.DrawString(nm, nfnt, nbr, nr, nsf);
                }

                // Count badge (top-right)
                if (_slot.Count > 1)
                {
                    string ct = _slot.Count.ToString();
                    using var cf = new Font("Segoe UI", 7f, FontStyle.Bold);
                    var csz = TextRenderer.MeasureText(ct, cf);
                    int cx  = w - csz.Width - 1;
                    int cy  = 1;
                    using var cb = new SolidBrush(Color.FromArgb(180, Theme.BG));
                    g.FillRectangle(cb, new Rectangle(cx - 1, cy, csz.Width + 2, csz.Height));
                    using var ct2 = new SolidBrush(Theme.Text);
                    g.DrawString(ct, cf, ct2, new PointF(cx - 1, cy));
                }

                // Rarity bar (2px bottom)
                using var rb = new SolidBrush(Color.FromArgb(140, _rarCol));
                g.FillRectangle(rb, 1, h - 3, w - 2, 2);
            }
        }
    }
}
