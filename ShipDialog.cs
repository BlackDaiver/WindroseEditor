using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace WindroseEditor
{
    // ─────────────────────────────────────────────────────────────────────────
    // ShipDialog — fleet management dialog
    // ─────────────────────────────────────────────────────────────────────────
    public class ShipDialog : Form
    {
        private readonly SaveFile _save;

        private ListView _list     = null!;
        private Button   _addBtn   = null!;
        private Button   _removeBtn = null!;
        private Button   _flagBtn  = null!;
        private Button   _closeBtn = null!;
        private Label    _hintLbl  = null!;

        public ShipDialog(SaveFile save)
        {
            _save = save;

            Text            = AppLanguage.T("Управление флотом", "Fleet Management");
            Size            = new Size(620, 420);
            MinimumSize     = new Size(520, 340);
            BackColor       = Theme.BG;
            ForeColor       = Theme.Text;
            Font            = new Font("Segoe UI", 9f);
            StartPosition   = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;

            Build();
            PopulateList();
        }

        // ── Build UI ─────────────────────────────────────────────────────────
        void Build()
        {
            // ── Right button panel ───────────────────────────────────────────
            var btnPanel = new Panel
            {
                Dock      = DockStyle.Right,
                Width     = 140,
                BackColor = Theme.BG,
            };

            _addBtn    = MakeBtn(AppLanguage.T("Добавить корабль", "Add Ship"),
                                  Theme.Accent, Color.FromArgb(0, 45, 70));
            _removeBtn = MakeBtn(AppLanguage.T("Удалить", "Remove"),
                                  Theme.Warn,   Color.FromArgb(60, 20, 0));
            _flagBtn   = MakeBtn(AppLanguage.T("Сделать флагманом", "Set Flagship"),
                                  Color.FromArgb(249, 200, 50), Color.FromArgb(45, 30, 0));
            _closeBtn  = MakeBtn(AppLanguage.T("Закрыть", "Close"),
                                  Theme.Dim, Theme.SlotBg);

            _addBtn.Click    += (_, _) => AddShip();
            _removeBtn.Click += (_, _) => RemoveShip();
            _flagBtn.Click   += (_, _) => SetFlagship();
            _closeBtn.Click  += (_, _) => Close();

            btnPanel.Controls.AddRange(new Control[] { _addBtn, _removeBtn, _flagBtn, _closeBtn });
            btnPanel.Layout += (_, _) =>
            {
                int y = 12;
                int w = btnPanel.ClientSize.Width - 16;
                foreach (Button b in new Control[] { _addBtn, _removeBtn, _flagBtn })
                {
                    b.SetBounds(8, y, w, 28);
                    y += 34;
                }
                _closeBtn.SetBounds(8, btnPanel.ClientSize.Height - 36, w, 28);
            };

            // ── List view ────────────────────────────────────────────────────
            _list = new ListView
            {
                Dock          = DockStyle.Fill,
                View          = View.Details,
                FullRowSelect = true,
                MultiSelect   = false,
                BackColor     = Theme.SlotBg,
                ForeColor     = Theme.Text,
                BorderStyle   = BorderStyle.None,
                Font          = new Font("Segoe UI", 9f),
                HeaderStyle   = ColumnHeaderStyle.Nonclickable,
                OwnerDraw     = false,
            };
            _list.Columns.Add(AppLanguage.T("Название",     "Name"),    200);
            _list.Columns.Add(AppLanguage.T("Тип корабля",  "Type"),    155);
            _list.Columns.Add(AppLanguage.T("Статус",       "Status"),   95);
            _list.SelectedIndexChanged += (_, _) => UpdateButtons();

            // ── Hint label at bottom ─────────────────────────────────────────
            _hintLbl = new Label
            {
                Dock      = DockStyle.Bottom,
                Height    = 22,
                ForeColor = Theme.Dim,
                Font      = new Font("Segoe UI", 7.5f),
                Padding   = new Padding(6, 0, 0, 0),
                TextAlign = ContentAlignment.MiddleLeft,
                Text      = AppLanguage.T(
                    "Лодка является стартовым судном и не может быть удалена.",
                    "The boat is the starter vessel and cannot be removed."),
            };

            Controls.Add(_list);
            Controls.Add(btnPanel);
            Controls.Add(_hintLbl);

            UpdateButtons();
        }

        // ── Populate list from current save ships ─────────────────────────────
        void PopulateList()
        {
            _list.Items.Clear();

            foreach (var ship in _save.Ships.Where(s => !s.IsDeleted))
            {
                string typeName = ship.TypeName;

                string status;
                Color  rowColor;
                if (ship.IsBoat)
                {
                    status   = AppLanguage.T("Лодка (нельзя удалить)", "Boat (non-removable)");
                    rowColor = Theme.Dim;
                }
                else if (ship.IsFlagship)
                {
                    status   = AppLanguage.T("★ Флагман", "★ Flagship");
                    rowColor = Color.FromArgb(249, 200, 50);
                }
                else
                {
                    status   = AppLanguage.T("Резерв", "Reserve");
                    rowColor = Theme.Text;
                }

                var li = new ListViewItem(ship.DisplayName) { Tag = ship, ForeColor = rowColor };
                li.SubItems.Add(typeName);
                li.SubItems.Add(status);
                _list.Items.Add(li);
            }

            UpdateButtons();
        }

        ShipInfo? SelectedShip =>
            _list.SelectedItems.Count > 0
                ? _list.SelectedItems[0].Tag as ShipInfo
                : null;

        void UpdateButtons()
        {
            var ship = SelectedShip;
            _removeBtn.Enabled = ship != null && !ship.IsBoat;
            _flagBtn.Enabled   = ship != null && !ship.IsBoat && !ship.IsFlagship;
        }

        // ── Actions ───────────────────────────────────────────────────────────
        void AddShip()
        {
            using var dlg = new AddShipDialog();
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            var ship = _save.AddShip(dlg.SelectedShipParams, dlg.CustomName);
            if (ship == null)
            {
                MessageBox.Show(
                    AppLanguage.T(
                        "Не удалось добавить корабль.\n\nУбедитесь, что в сохранении уже есть хотя бы один корабль-шаблон.",
                        "Failed to add ship.\n\nMake sure at least one ship already exists in the save to use as a template."),
                    AppLanguage.T("Ошибка", "Error"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            PopulateList();
        }

        void RemoveShip()
        {
            var ship = SelectedShip;
            if (ship == null || ship.IsBoat) return;

            string shipLabel = string.IsNullOrEmpty(ship.DisplayName) ? ship.TypeName : ship.DisplayName;
            string msg = AppLanguage.T(
                $"Удалить корабль «{shipLabel}»?\nЭто действие нельзя отменить.",
                $"Remove ship \"{shipLabel}\"?\nThis action cannot be undone.");
            if (MessageBox.Show(msg,
                    AppLanguage.T("Удалить корабль", "Remove Ship"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            if (!_save.RemoveShip(ship.Guid))
            {
                MessageBox.Show(
                    AppLanguage.T(
                        "Нельзя удалить последний небоевой корабль в флоте.",
                        "Cannot remove the last non-boat ship in the fleet."),
                    AppLanguage.T("Ошибка", "Error"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            PopulateList();
        }

        void SetFlagship()
        {
            var ship = SelectedShip;
            if (ship == null || ship.IsBoat || ship.IsFlagship) return;

            _save.SetFlagship(ship.Guid);
            PopulateList();
        }

        static Button MakeBtn(string text, Color fg, Color bg) => new Button
        {
            Text      = text,
            Height    = 28,
            ForeColor = fg,
            BackColor = bg,
            FlatStyle = FlatStyle.Flat,
            Font      = new Font("Segoe UI", 8.5f),
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AddShipDialog — ship type selector + optional custom name
    // ─────────────────────────────────────────────────────────────────────────
    class AddShipDialog : Form
    {
        ComboBox _typeBox   = null!;
        TextBox  _nameBox   = null!;
        Button   _okBtn     = null!;
        Button   _cancelBtn = null!;

        public string SelectedShipParams { get; private set; } = "";
        public string CustomName         { get; private set; } = "";

        public AddShipDialog()
        {
            Text            = AppLanguage.T("Добавить корабль", "Add Ship");
            Size            = new Size(370, 210);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = MinimizeBox = false;
            BackColor       = Theme.BG;
            ForeColor       = Theme.Text;
            Font            = new Font("Segoe UI", 9f);
            StartPosition   = FormStartPosition.CenterParent;

            Build();
        }

        void Build()
        {
            // ── Ship type ────────────────────────────────────────────────────
            var typeLbl = new Label
            {
                Text      = AppLanguage.T("Тип корабля:", "Ship type:"),
                Location  = new Point(12, 14),
                AutoSize  = true,
                ForeColor = Theme.HdrText,
            };

            _typeBox = new ComboBox
            {
                Location      = new Point(12, 34),
                Width         = 328,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor     = Theme.SlotBg,
                ForeColor     = Theme.Text,
                FlatStyle     = FlatStyle.Flat,
            };

            // All non-boat types, sorted by category then name
            foreach (var t in SaveFile.KnownShipTypes.Where(t => !t.IsBoat))
            {
                string display = AppLanguage.IsRu ? t.NameRu : t.NameEn;
                _typeBox.Items.Add(new ShipTypeItem(t, display));
            }
            if (_typeBox.Items.Count > 0) _typeBox.SelectedIndex = 0;

            // ── Custom name ──────────────────────────────────────────────────
            var nameLbl = new Label
            {
                Text      = AppLanguage.T("Имя корабля (необязательно):", "Ship name (optional):"),
                Location  = new Point(12, 70),
                AutoSize  = true,
                ForeColor = Theme.HdrText,
            };

            _nameBox = new TextBox
            {
                Location        = new Point(12, 90),
                Width           = 328,
                BackColor       = Theme.SlotBg,
                ForeColor       = Theme.Text,
                BorderStyle     = BorderStyle.FixedSingle,
                PlaceholderText = AppLanguage.T("Оставьте пустым для имени по умолчанию",
                                                 "Leave empty for default type name"),
            };

            // ── OK / Cancel ──────────────────────────────────────────────────
            _okBtn = new Button
            {
                Text      = "OK",
                Location  = new Point(174, 138),
                Width     = 78,
                Height    = 28,
                BackColor = Color.FromArgb(0, 45, 70),
                ForeColor = Theme.Accent,
                FlatStyle = FlatStyle.Flat,
            };
            _okBtn.Click += OkClicked;

            _cancelBtn = new Button
            {
                Text         = AppLanguage.T("Отмена", "Cancel"),
                Location     = new Point(260, 138),
                Width        = 80,
                Height       = 28,
                BackColor    = Theme.SlotBg,
                ForeColor    = Theme.Dim,
                FlatStyle    = FlatStyle.Flat,
                DialogResult = DialogResult.Cancel,
            };

            AcceptButton = _okBtn;
            CancelButton = _cancelBtn;

            Controls.AddRange(new Control[]
                { typeLbl, _typeBox, nameLbl, _nameBox, _okBtn, _cancelBtn });
        }

        void OkClicked(object? sender, EventArgs e)
        {
            if (_typeBox.SelectedItem is not ShipTypeItem sel) return;
            SelectedShipParams = sel.TypeInfo.Params;
            CustomName         = _nameBox.Text.Trim();
            DialogResult       = DialogResult.OK;
            Close();
        }

        private class ShipTypeItem
        {
            public ShipTypeInfo TypeInfo { get; }
            private readonly string _display;

            public ShipTypeItem(ShipTypeInfo t, string display)
            { TypeInfo = t; _display = display; }

            public override string ToString() => _display;
        }
    }
}
