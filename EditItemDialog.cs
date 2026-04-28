using System;
using System.Drawing;
using System.Windows.Forms;

namespace WindroseEditor
{
    /// <summary>
    /// Диалог редактирования уровня, качества и количества предмета в инвентаре.
    /// </summary>
    public class EditItemDialog : Form
    {
        static readonly Color BG     = Color.FromArgb(15,  18,  24);
        static readonly Color BG2    = Color.FromArgb(22,  27,  36);
        static readonly Color BG3    = Color.FromArgb(10,  12,  16);
        static readonly Color BORDER = Color.FromArgb(30,  42,  58);
        static readonly Color TEXT   = Color.FromArgb(200, 216, 232);
        static readonly Color DIM    = Color.FromArgb(90,  112, 144);
        static readonly Color ACCENT = Color.FromArgb(0,   200, 255);

        public int SelectedLevel   { get; private set; }
        public int SelectedQuality { get; private set; }
        public int SelectedCount   { get; private set; }

        readonly NumericUpDown _levelSpin;
        readonly NumericUpDown _qualitySpin;
        readonly NumericUpDown _countSpin;
        readonly Button        _applyBtn;

        public EditItemDialog(InventorySlot slot, ItemEntry? item)
        {
            string name = item?.DisplayName ?? slot.InternalName;

            Text            = $"{AppLanguage.T("Редактировать", "Edit")}  —  {name}";
            Size            = new Size(420, 240);
            MinimumSize     = new Size(360, 210);
            MaximumSize     = new Size(560, 250);
            BackColor       = BG;
            ForeColor       = TEXT;
            Font            = new Font("Segoe UI", 9.5f);
            StartPosition   = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;

            // Диапазоны из item_db, если предмет есть в базе
            int maxLevel   = item != null ? item.MaxLevel   : slot.MaxLevel;
            int maxQuality = item?.MaxQualityLevel ?? 0;
            int maxCount   = item != null && item.MaxCountInSlot > 0
                             ? item.MaxCountInSlot : 9999;

            SelectedLevel   = slot.Level   > 0 ? slot.Level   : (maxLevel   > 0 ? 1 : 0);
            SelectedQuality = slot.Quality;
            SelectedCount   = slot.Count   > 0 ? slot.Count   : 1;

            // ── Header panel ─────────────────────────────────────────────
            var hdr = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = BG2 };

            var iconBox = new PictureBox
            {
                Size      = new Size(36, 36),
                Location  = new Point(8, 5),
                SizeMode  = PictureBoxSizeMode.Zoom,
                BackColor = Color.FromArgb(30, 40, 55),
            };
            if (item != null) iconBox.Image = IconCache.Get(item.IconRef);

            var nameLbl = new Label
            {
                Text      = name,
                Location  = new Point(52, 6),
                AutoSize  = false,
                Width     = hdr.Width - 60,
                Height    = 20,
                Font      = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = TEXT,
                Anchor    = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            var rarLbl = new Label
            {
                Text      = item != null
                    ? $"{item.Rarity}  ·  {ItemDatabase.CategoryDisplayName(item.Category)}"
                    : "",
                Location  = new Point(52, 26),
                AutoSize  = false,
                Width     = hdr.Width - 60,
                Height    = 16,
                Font      = new Font("Segoe UI", 8f),
                ForeColor = item != null ? Theme.Rarity(item.Rarity) : DIM,
                Anchor    = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };

            hdr.Controls.AddRange(new Control[] { iconBox, nameLbl, rarLbl });
            hdr.Resize += (_, _) =>
            {
                nameLbl.Width = hdr.Width - 60;
                rarLbl.Width  = hdr.Width - 60;
            };

            // ── Fields panel ─────────────────────────────────────────────
            var body = new Panel
            {
                Dock      = DockStyle.Fill,
                BackColor = BG,
                Padding   = new Padding(16, 8, 16, 0),
            };

            // Level spinner
            bool hasLevel   = maxLevel > 0;
            var levelLbl = MakeLbl($"{AppLanguage.T("Уровень", "Level")} (1–{(hasLevel ? maxLevel : 0)}):");
            _levelSpin = new NumericUpDown
            {
                Minimum   = hasLevel ? 1 : 0,
                Maximum   = hasLevel ? maxLevel : 0,
                Value     = hasLevel ? Math.Clamp(SelectedLevel, 1, maxLevel) : 0,
                Width     = 70,
                BackColor = BG3,
                ForeColor = TEXT,
                Enabled   = hasLevel,
            };

            // Quality spinner
            bool hasQuality = maxQuality > 0;
            var qualLbl = MakeLbl($"{AppLanguage.T("Качество", "Quality")} (0–{maxQuality}):");
            _qualitySpin = new NumericUpDown
            {
                Minimum   = 0,
                Maximum   = hasQuality ? maxQuality : 0,
                Value     = hasQuality ? Math.Clamp(SelectedQuality, 0, maxQuality) : 0,
                Width     = 70,
                BackColor = BG3,
                ForeColor = TEXT,
                Enabled   = hasQuality,
            };
            qualLbl.Visible     = hasQuality;
            _qualitySpin.Visible = hasQuality;

            // Count spinner
            var countLbl = MakeLbl($"{AppLanguage.T("Кол-во", "Count")} (1–{maxCount}):");
            _countSpin = new NumericUpDown
            {
                Minimum   = 1,
                Maximum   = maxCount,
                Value     = Math.Clamp(SelectedCount, 1, maxCount),
                Width     = 70,
                BackColor = BG3,
                ForeColor = TEXT,
            };

            // Table layout
            int colCount = hasQuality ? 6 : 4;
            var tbl = new TableLayoutPanel
            {
                Dock        = DockStyle.Top,
                ColumnCount = colCount,
                RowCount    = 1,
                AutoSize    = true,
                BackColor   = BG,
            };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            if (hasQuality)
            {
                tbl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            }
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            tbl.Margin = new Padding(0, 12, 0, 0);

            tbl.Controls.Add(levelLbl,  0, 0);
            tbl.Controls.Add(_levelSpin, 1, 0);
            if (hasQuality)
            {
                tbl.Controls.Add(qualLbl,    2, 0);
                tbl.Controls.Add(_qualitySpin, 3, 0);
                tbl.Controls.Add(countLbl,   4, 0);
                tbl.Controls.Add(_countSpin,  5, 0);
            }
            else
            {
                tbl.Controls.Add(countLbl,   2, 0);
                tbl.Controls.Add(_countSpin,  3, 0);
            }

            body.Controls.Add(tbl);

            // ── Bottom bar ────────────────────────────────────────────────
            var bot = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = BG2 };

            _applyBtn = new Button
            {
                Text         = AppLanguage.T("Применить", "Apply"),
                Width        = 110,
                Height       = 30,
                BackColor    = Color.FromArgb(0, 70, 110),
                ForeColor    = ACCENT,
                FlatStyle    = FlatStyle.Flat,
                DialogResult = DialogResult.OK,
            };
            _applyBtn.FlatAppearance.BorderColor = ACCENT;
            _applyBtn.Click += (_, _) => Accept();

            var cancelBtn = new Button
            {
                Text         = AppLanguage.T("Отмена", "Cancel"),
                Width        = 90,
                Height       = 30,
                BackColor    = BG3,
                ForeColor    = TEXT,
                FlatStyle    = FlatStyle.Flat,
                DialogResult = DialogResult.Cancel,
            };
            cancelBtn.FlatAppearance.BorderColor = BORDER;

            bot.Controls.Add(_applyBtn);
            bot.Controls.Add(cancelBtn);
            bot.Layout += (_, _) =>
            {
                int r = bot.ClientSize.Width - 10;
                _applyBtn.Location = new Point(r - _applyBtn.Width, 9);
                cancelBtn.Location = new Point(_applyBtn.Left - cancelBtn.Width - 8, 9);
            };

            Controls.Add(body);
            Controls.Add(hdr);
            Controls.Add(bot);

            AcceptButton = _applyBtn;
            CancelButton = cancelBtn;
        }

        void Accept()
        {
            SelectedLevel   = (int)_levelSpin.Value;
            SelectedQuality = (int)_qualitySpin.Value;
            SelectedCount   = (int)_countSpin.Value;
            DialogResult    = DialogResult.OK;
            Close();
        }

        static Label MakeLbl(string text) => new Label
        {
            Text      = text,
            ForeColor = Color.FromArgb(90, 112, 144),
            AutoSize  = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin    = new Padding(0, 7, 8, 0),
        };
    }
}
