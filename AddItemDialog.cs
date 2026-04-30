using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace WindroseEditor
{
    public partial class AddItemDialog : Form
    {
        // ── Colors ───────────────────────────────────────────────────────
        static readonly Color BG      = Color.FromArgb(15,  18,  24);
        static readonly Color BG2     = Color.FromArgb(22,  27,  36);
        static readonly Color BG3     = Color.FromArgb(10,  12,  16);
        static readonly Color BORDER  = Color.FromArgb(30,  42,  58);
        static readonly Color TEXT    = Color.FromArgb(200, 216, 232);
        static readonly Color ACCENT  = Color.FromArgb(0,   200, 255);
        static readonly Color TEXTDIM = Color.FromArgb(90,  112, 144);

        // ── Result ───────────────────────────────────────────────────────
        public ItemEntry?  SelectedItem    { get; private set; }
        public int         SelectedLevel   { get; private set; } = 1;
        public int         SelectedQuality { get; private set; } = 0;
        public int         SelectedCount   { get; private set; } = 1;

        // ── UI Controls ──────────────────────────────────────────────────
        TextBox       _searchBox   = null!;
        ComboBox      _rarityBox   = null!;
        ComboBox      _catBox      = null!;
        DataGridView  _grid        = null!;
        NumericUpDown _levelSpin   = null!;
        NumericUpDown _qualitySpin = null!;
        NumericUpDown _countSpin   = null!;
        Label         _lvlLabel    = null!;
        Label         _qualLabel   = null!;
        Label         _cntLabel    = null!;
        Label         _infoLabel   = null!;
        Button        _addBtn      = null!;

        List<ItemEntry> _filteredItems   = new();
        string[]?       _allowedCategories;
        string[]?       _allowedItemTypes;
        Panel? bottomPanel;

        // ── Constructor ──────────────────────────────────────────────────
        /// <param name="allowedCategories">
        /// Если задан — показывать только предметы этих категорий.
        /// Комбо категорий будет заблокировано.
        /// </param>
        /// <param name="allowedItemTypes">
        /// Если задан — показывать только предметы с одним из этих ItemType-тегов.
        /// Имеет приоритет над allowedCategories для фильтрации.
        /// </param>
        public AddItemDialog(int moduleIndex, int slotIndex,
                             string[]? allowedCategories = null,
                             string[]? allowedItemTypes  = null)
        {
            _allowedCategories = allowedCategories;
            _allowedItemTypes  = allowedItemTypes;

            Text = AppLanguage.T($"Добавить предмет  —  слот {slotIndex}",
                                  $"Add item  —  slot {slotIndex}");

            Size            = new Size(900, 640);
            MinimumSize     = new Size(700, 480);
            BackColor       = BG;
            ForeColor       = TEXT;
            Font            = new Font("Segoe UI", 9f);
            StartPosition   = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;

            BuildLayout();
            RebuildCatCodes();

            // Если фильтр по категории задан — предвыбрать и заблокировать комбо
            if (_allowedCategories != null && _allowedCategories.Length == 1)
            {
                int idx = _catCodes.IndexOf(_allowedCategories[0]);
                if (idx >= 0)
                {
                    _catBox.SelectedIndex = idx;
                    _catBox.Enabled       = false;
                }
            }

            // Если фильтр по ItemType задан — комбо категорий не имеет смысла
            if (_allowedItemTypes != null && _allowedItemTypes.Length > 0)
                _catBox.Enabled = false;

            ApplyFilter();
        }

        void BuildLayout()
        {
            // ── Filter bar ───────────────────────────────────────────────
            var filterPanel = new Panel
            {
                Dock        = DockStyle.Top,
                Height      = 52,
                BackColor   = BG2,
                Padding     = new Padding(8, 8, 8, 8),
            };

            _searchBox = new TextBox
            {
                PlaceholderText = AppLanguage.T("Поиск по названию, тегу, категории…", "Search by name, tag, category…"),
                Width           = 260,
                BackColor       = BG3,
                ForeColor       = TEXT,
                BorderStyle     = BorderStyle.FixedSingle,
                Font            = new Font("Segoe UI", 9.5f),
            };
            _searchBox.TextChanged += (_, _) => ApplyFilter();

            _rarityBox = CreateCombo(130);
            _rarityBox.Items.Add(AppLanguage.T("Все редкости", "All rarities"));
            foreach (var r in ItemDatabase.Rarities) _rarityBox.Items.Add(r);
            _rarityBox.SelectedIndex = 0;
            _rarityBox.SelectedIndexChanged += (_, _) => ApplyFilter();

            _catBox = CreateCombo(170);
            _catBox.Items.Add(AppLanguage.T("Все", "All"));
            foreach (var c in ItemDatabase.GetCategories())
                _catBox.Items.Add(ItemDatabase.CategoryDisplayName(c));
            _catBox.SelectedIndex = 0;
            _catBox.SelectedIndexChanged += (_, _) => ApplyFilter();

            var searchLabel = MakeLabel(AppLanguage.T("Поиск:", "Search:"));
            var rarLabel    = MakeLabel(AppLanguage.T("Редкость:", "Rarity:"));
            var catLabel    = MakeLabel(AppLanguage.T("Категория:", "Category:"));

            filterPanel.Controls.AddRange(new Control[]
                { searchLabel, _searchBox, rarLabel, _rarityBox, catLabel, _catBox });

            int x = 8;
            LayoutRow(filterPanel.Controls, ref x, 10,
                (searchLabel, 0), (_searchBox, 265),
                (rarLabel, 0), (_rarityBox, 125),
                (catLabel, 0), (_catBox, 155));

            // ── Grid ────────────────────────────────────────────────────
            _grid = new DataGridView
            {
                Dock                          = DockStyle.Fill,
                BackgroundColor               = BG,
                GridColor                     = BORDER,
                BorderStyle                   = BorderStyle.None,
                RowHeadersVisible             = false,
                AllowUserToAddRows            = false,
                AllowUserToResizeRows         = false,
                AllowUserToDeleteRows         = false,
                SelectionMode                 = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect                   = false,
                ReadOnly                      = true,
                ColumnHeadersHeightSizeMode   = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight           = 30,
                RowTemplate                   = { Height = 42 },
                DefaultCellStyle              = { BackColor = BG, ForeColor = TEXT, SelectionBackColor = BG2, SelectionForeColor = ACCENT },
                ColumnHeadersDefaultCellStyle = { BackColor = BG3, ForeColor = TEXTDIM, Font = new Font("Segoe UI", 8f, FontStyle.Bold) },
                AlternatingRowsDefaultCellStyle = { BackColor = Color.FromArgb(18, 22, 30) },
                EnableHeadersVisualStyles     = false,
            };

            // Icon column (owner-drawn)
            var iconCol = new DataGridViewImageColumn
            {
                HeaderText  = "",
                Width       = 48,
                Resizable   = DataGridViewTriState.False,
                ImageLayout = DataGridViewImageCellLayout.Zoom,
            };
            _grid.Columns.Add(iconCol);
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = AppLanguage.T("Название",  "Name"),     Width = 230 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = AppLanguage.T("Редкость",  "Rarity"),   Width = 90  });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = AppLanguage.T("Категория", "Category"), Width = 150 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = AppLanguage.T("Макс.ур.",  "Max lv."),  Width = 65  });

            _grid.CellPainting    += Grid_CellPainting;
            _grid.SelectionChanged += Grid_SelectionChanged;
            _grid.CellDoubleClick  += (_, _) => AcceptItem();

            // ── Bottom bar ───────────────────────────────────────────────
            bottomPanel = new Panel
            {
                Dock      = DockStyle.Bottom,
                Height    = 68,
                BackColor = BG2,
                Padding   = new Padding(12, 0, 12, 0),
            };

            _infoLabel = new Label
            {
                Text      = AppLanguage.T("Выберите предмет из списка", "Select an item from the list"),
                ForeColor = TEXTDIM,
                AutoSize  = false,
                Width     = 320,
                Height    = 40,
                TextAlign = ContentAlignment.MiddleLeft,
                Font      = new Font("Segoe UI", 8.5f),
            };

            _lvlLabel = MakeLabel(AppLanguage.T("Уровень:", "Level:"));
            _levelSpin = new NumericUpDown
            {
                Minimum   = 0,
                Maximum   = 15,
                Value     = 1,
                Width     = 55,
                BackColor = BG3,
                ForeColor = TEXT,
            };

            _qualLabel = MakeLabel(AppLanguage.T("Качество:", "Quality:"));
            _qualitySpin = new NumericUpDown
            {
                Minimum   = 0,
                Maximum   = 0,
                Value     = 0,
                Width     = 55,
                BackColor = BG3,
                ForeColor = TEXT,
                Enabled   = false,
                Visible   = false,
            };
            _qualLabel.Visible = false;

            _cntLabel = MakeLabel(AppLanguage.T("Кол-во:", "Count:"));
            _countSpin = new NumericUpDown
            {
                Minimum   = 1,
                Maximum   = 9999,
                Value     = 1,
                Width     = 65,
                BackColor = BG3,
                ForeColor = TEXT,
            };

            var cancelBtn = new Button
            {
                Text         = AppLanguage.T("Отмена", "Cancel"),
                Width        = 90,
                Height       = 32,
                BackColor    = BG3,
                ForeColor    = TEXT,
                FlatStyle    = FlatStyle.Flat,
                DialogResult = DialogResult.Cancel,
            };
            cancelBtn.FlatAppearance.BorderColor = BORDER;

            _addBtn = new Button
            {
                Text      = AppLanguage.T("Добавить", "Add"),
                Width     = 100,
                Height    = 32,
                BackColor = Color.FromArgb(0, 80, 120),
                ForeColor = ACCENT,
                FlatStyle = FlatStyle.Flat,
                Enabled   = false,
            };
            _addBtn.FlatAppearance.BorderColor = ACCENT;
            _addBtn.Click += (_, _) => AcceptItem();

            bottomPanel.Controls.AddRange(new Control[]
            {
                _infoLabel,
                _lvlLabel, _levelSpin,
                _qualLabel, _qualitySpin,
                _cntLabel, _countSpin,
                cancelBtn, _addBtn,
            });

            // Manual layout: кнопки справа, спиннеры правее инфо-метки
            bottomPanel.Layout += (_, _) =>
            {
                _infoLabel.Location = new Point(12, 14);
                int right = bottomPanel.ClientSize.Width - 12;
                _addBtn.Location   = new Point(right - _addBtn.Width, 18);
                cancelBtn.Location = new Point(_addBtn.Left - cancelBtn.Width - 8, 18);

                int x = cancelBtn.Left - 8;

                _countSpin.Location = new Point(x - _countSpin.Width, 22); x = _countSpin.Left;
                _cntLabel.Location  = new Point(x - _cntLabel.Width - 4, 25); x = _cntLabel.Left;

                if (_qualitySpin.Visible)
                {
                    _qualitySpin.Location = new Point(x - _qualitySpin.Width - 4, 22); x = _qualitySpin.Left;
                    _qualLabel.Location   = new Point(x - _qualLabel.Width - 4, 25);   x = _qualLabel.Left;
                }

                _levelSpin.Location = new Point(x - _levelSpin.Width - 4, 22); x = _levelSpin.Left;
                _lvlLabel.Location  = new Point(x - _lvlLabel.Width - 4, 25);
            };

            // ── Assemble ─────────────────────────────────────────────────
            Controls.Add(_grid);
            Controls.Add(filterPanel);
            Controls.Add(bottomPanel);

            AcceptButton = _addBtn;
            CancelButton = cancelBtn;
        }

        // ── Filtering ────────────────────────────────────────────────────
        // category raw codes, matching the combo order
        readonly List<string> _catCodes = new();

        void RebuildCatCodes()
        {
            _catCodes.Clear();
            _catCodes.Add(""); // "Все"
            foreach (var c in ItemDatabase.GetCategories()) _catCodes.Add(c);
        }

        void ApplyFilter()
        {
            string search = _searchBox.Text;
            string rarity = _rarityBox.SelectedIndex > 0
                ? _rarityBox.SelectedItem?.ToString() ?? "" : "";

            string cat = "";
            int ci = _catBox.SelectedIndex;
            if (ci > 0 && ci < _catCodes.Count) cat = _catCodes[ci];

            var filtered = ItemDatabase.Filter(search, rarity, cat);

            // Ограничитель по ItemType (приоритет — снаряжение/боеприпасы)
            if (_allowedItemTypes != null && _allowedItemTypes.Length > 0)
                filtered = filtered.Where(i =>
                    _allowedItemTypes.Contains(i.ItemType, StringComparer.OrdinalIgnoreCase));
            // Ограничитель по категории (аксессуары)
            else if (_allowedCategories != null && _allowedCategories.Length > 0)
                filtered = filtered.Where(i =>
                    _allowedCategories.Contains(i.Category, StringComparer.OrdinalIgnoreCase));

            _filteredItems = filtered.ToList();
            PopulateGrid();
        }

        void PopulateGrid()
        {
            _grid.Rows.Clear();
            foreach (var item in _filteredItems)
            {
                int row = _grid.Rows.Add();
                var r   = _grid.Rows[row];
                r.Cells[0].Value = new Bitmap(1, 1);          // drawn via CellPainting
                r.Cells[1].Value = item.DisplayName;
                r.Cells[2].Value = item.Rarity;
                r.Cells[3].Value = AppLanguage.IsRu
                ? ItemDatabase.CategoryDisplayName(item.Category)
                : item.Category;
                r.Cells[4].Value = item.MaxLevel == 0 ? "—" : item.MaxLevel.ToString();
                r.Tag            = item;
            }
            _infoLabel.Text = $"{_filteredItems.Count} предметов";
            _addBtn.Enabled = false;
        }

        // ── Cell painting — icon (or rarity square fallback) ─────────────
        void Grid_CellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.ColumnIndex != 0 || e.RowIndex < 0) return;
            e.Paint(e.ClipBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.Border);

            var item = _grid.Rows[e.RowIndex].Tag as ItemEntry;
            if (item == null || e.Graphics == null) { e.Handled = true; return; }

            Color rc = RarityColor(item.Rarity);
            int   sz = 34;
            var   r  = new Rectangle(e.CellBounds.X + (e.CellBounds.Width  - sz) / 2,
                                     e.CellBounds.Y + (e.CellBounds.Height - sz) / 2, sz, sz);

            // Rarity tint background
            using var bg = new SolidBrush(Color.FromArgb(50, rc));
            e.Graphics.FillRectangle(bg, r);

            var icon = string.IsNullOrEmpty(item.IconRef) ? null : IconCache.Get(item.IconRef);
            if (icon != null)
            {
                e.Graphics.InterpolationMode =
                    System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                e.Graphics.DrawImage(icon, r);
            }
            else
            {
                // Fallback: rarity border + initial letter
                using var border = new Pen(Color.FromArgb(160, rc), 1.5f);
                e.Graphics.DrawRectangle(border, r);
                string init = item.DisplayName.Length > 0
                    ? item.DisplayName[0].ToString().ToUpper() : "?";
                using var font  = new Font("Segoe UI", 10f, FontStyle.Bold);
                using var brush = new SolidBrush(rc);
                var sf = new StringFormat
                    { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                e.Graphics.DrawString(init, font, brush, r, sf);
            }

            e.Handled = true;
        }

        // ── Selection change ─────────────────────────────────────────────
        void Grid_SelectionChanged(object? sender, EventArgs e)
        {
            if (_grid.SelectedRows.Count == 0) { _addBtn.Enabled = false; return; }
            var item = _grid.SelectedRows[0].Tag as ItemEntry;
            if (item == null) { _addBtn.Enabled = false; return; }

            _addBtn.Enabled = true;
            _infoLabel.Text = $"{item.DisplayName}  ·  {item.Rarity}";
            if (!string.IsNullOrEmpty(item.Description))
                _infoLabel.Text += $"\n{TruncateAt(item.Description, 80)}";

            // ── Уровень ──────────────────────────────────────────────
            int maxLv = item.MaxLevel;
            _levelSpin.Maximum = maxLv > 0 ? maxLv : 0;
            if (maxLv == 0)
            {
                _levelSpin.Value   = 0;
                _levelSpin.Enabled = false;
            }
            else
            {
                _levelSpin.Enabled = true;
                if (_levelSpin.Value < 1) _levelSpin.Value = 1;
            }

            // ── Качество ─────────────────────────────────────────────
            bool hasQuality = item.MaxQualityLevel > 0;
            _qualitySpin.Maximum = item.MaxQualityLevel;
            _qualitySpin.Value   = 0;
            _qualitySpin.Enabled = hasQuality;
            _qualitySpin.Visible = hasQuality;
            _qualLabel.Visible   = hasQuality;

            // ── Количество ───────────────────────────────────────────
            int maxCnt = item.MaxCountInSlot > 0 ? item.MaxCountInSlot : 9999;
            _countSpin.Maximum = maxCnt;
            if (_countSpin.Value > maxCnt) _countSpin.Value = maxCnt;
            _cntLabel.Text = AppLanguage.T($"Кол-во (1–{maxCnt}):", $"Count (1–{maxCnt}):");

            bottomPanel?.PerformLayout();
        }

        void AcceptItem()
        {
            if (_grid.SelectedRows.Count == 0) return;
            SelectedItem    = _grid.SelectedRows[0].Tag as ItemEntry;
            if (SelectedItem == null) return;
            SelectedLevel   = (int)_levelSpin.Value;
            SelectedQuality = (int)_qualitySpin.Value;
            SelectedCount   = (int)_countSpin.Value;
            DialogResult    = DialogResult.OK;
            Close();
        }

        // ── Helpers ──────────────────────────────────────────────────────
        static Color RarityColor(string rarity) => rarity switch
        {
            "Legendary" => Color.FromArgb(249, 115,  22),
            "Epic"      => Color.FromArgb(168,  85, 247),
            "Rare"      => Color.FromArgb( 59, 130, 246),
            "Uncommon"  => Color.FromArgb( 34, 197,  94),
            "Common"    => Color.FromArgb(148, 163, 184),
            _           => Color.FromArgb(100, 100, 100),
        };

        static Bitmap GetRarityIcon(string rarity) => new Bitmap(1, 1); // placeholder, we draw in CellPainting

        static string TruncateAt(string s, int max) =>
            s.Length <= max ? s : s[..max] + "…";

        static Label MakeLabel(string text) => new Label
        {
            Text      = text,
            ForeColor = TEXTDIM,
            AutoSize  = true,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        static ComboBox CreateCombo(int width) => new ComboBox
        {
            Width         = width,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor     = BG3,
            ForeColor     = TEXT,
            FlatStyle     = FlatStyle.Flat,
        };

        static void LayoutRow(Control.ControlCollection ctrls, ref int x, int y,
                               params (Control ctrl, int advance)[] items)
        {
            foreach (var (ctrl, adv) in items)
            {
                ctrl.Location = new Point(x, y + (30 - ctrl.Height) / 2);
                x += (adv > 0 ? adv : ctrl.Width) + 8;
            }
        }
    }
}
