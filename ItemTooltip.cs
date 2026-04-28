using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace WindroseEditor
{
    /// <summary>
    /// Стилизованная всплывающая подсказка для предметов инвентаря.
    /// </summary>
    public sealed class ItemTooltip : Form
    {
        // ─── Singleton ────────────────────────────────────────────────────────
        static ItemTooltip? _inst;
        public static ItemTooltip Instance
        {
            get
            {
                if (_inst == null || _inst.IsDisposed)
                    _inst = new ItemTooltip();
                return _inst;
            }
        }

        ItemEntry?     _item;
        InventorySlot? _slot;

        // ─── Константы ───────────────────────────────────────────────────────
        const int W      = 295;
        const int Pad    = 12;
        const int IconSz = 52;
        const int LH     = 19;   // высота строки стат
        const int SH     = 15;   // высота малой строки (описание)

        // ─── Цвета ────────────────────────────────────────────────────────────
        static readonly Color BgColor     = Color.FromArgb(14, 16, 22);
        static readonly Color BorderColor = Color.FromArgb(55, 65, 85);
        static readonly Color TextColor   = Color.FromArgb(210, 220, 235);
        static readonly Color DimColor    = Color.FromArgb(100, 115, 135);
        static readonly Color VanityColor = Color.FromArgb(150, 160, 180);
        static readonly Color DescColor   = Color.FromArgb(175, 185, 205);
        static readonly Color SepColor    = Color.FromArgb(45, 55, 70);
        static readonly Color EffectColor = Color.FromArgb(160, 210, 170);
        static readonly Color SetColor    = Color.FromArgb(230, 200, 110);

        // ─── Шрифты ───────────────────────────────────────────────────────────
        static readonly Font FntName    = new("Segoe UI", 10f, FontStyle.Bold);
        static readonly Font FntSub     = new("Segoe UI", 8f);
        static readonly Font FntStat    = new("Segoe UI", 9f);
        static readonly Font FntStatVal = new("Segoe UI", 9f, FontStyle.Bold);
        static readonly Font FntDesc    = new("Segoe UI", 8.5f);
        static readonly Font FntVanity  = new("Segoe UI", 8f, FontStyle.Italic);
        static readonly Font FntSet     = new("Segoe UI", 8.5f, FontStyle.Bold);
        static readonly Font FntSetDesc = new("Segoe UI", 8.5f);

        ItemTooltip()
        {
            FormBorderStyle  = FormBorderStyle.None;
            ShowInTaskbar    = false;
            TopMost          = true;
            DoubleBuffered   = true;
            BackColor        = BgColor;
            StartPosition    = FormStartPosition.Manual;
            Size             = new Size(W, 100);
            Paint           += OnPaint;
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000; return cp; }
        }

        // ─── API ──────────────────────────────────────────────────────────────
        public void ShowFor(InventorySlot slot, ItemEntry? item, Point screenPos)
        {
            _slot = slot;
            _item = item;

            int h = ComputeHeight();
            Size = new Size(W, h);

            var screen = Screen.FromPoint(screenPos).WorkingArea;
            int x = screenPos.X + 14;
            int y = screenPos.Y + 4;
            if (x + W > screen.Right)  x = screenPos.X - W - 4;
            if (y + h > screen.Bottom) y = screen.Bottom - h - 4;
            if (x < screen.Left) x = screen.Left + 2;
            if (y < screen.Top)  y = screen.Top  + 2;

            Location = new Point(x, y);
            Invalidate();
            if (!Visible) Show();
        }

        public new void Hide()
        {
            if (Visible) base.Hide();
        }

        // ─── Вычисление высоты ────────────────────────────────────────────────
        int ComputeHeight()
        {
            using var g = Graphics.FromHwnd(IntPtr.Zero);
            int h = Pad + IconSz + 6;  // заголовок (иконка + имя + редкость + уровень)

            if (_item == null) { h += SH + Pad; return h; }

            var stats   = BuildStatLines();
            var effects = BuildEffectLines(out var formattedVals);

            if (stats.Count > 0)
            {
                h += 8;  // разделитель + зазор
                foreach (var s in stats) h += LH;
            }

            bool hasDesc = !string.IsNullOrEmpty(_item.Description);
            bool hasVan  = !string.IsNullOrEmpty(_item.VanityText);
            bool hasEff  = effects.Count > 0;
            bool hasSet  = _item.SetEffects != null && _item.SetEffects.Count > 0;

            if (hasDesc || hasVan || hasEff || hasSet)
            {
                h += 8;  // разделитель
            }

            if (hasEff)
                foreach (var ef in effects) h += MeasureWrapped(g, ef.Text, W - Pad * 2, FntSetDesc) + 2;

            if (hasSet)
            {
                var setLines = BuildSetEffectLines();
                foreach (var sl in setLines)
                {
                    h += LH;  // заголовок сета
                    h += MeasureWrapped(g, sl.Desc, W - Pad * 2 - 10, FntSetDesc) + 2;
                }
            }

            if (hasDesc)
            {
                string descText = TryFormat(_item.Description, formattedVals);
                h += MeasureWrapped(g, descText, W - Pad * 2, FntDesc) + 6;
            }

            if (hasVan)
                h += MeasureWrapped(g, _item.VanityText, W - Pad * 2, FntVanity) + 4;

            h += Pad;
            return Math.Max(h, 80);
        }

        static int MeasureWrapped(Graphics g, string text, int width, Font font)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var sz = g.MeasureString(text, font, width, StringFormat.GenericTypographic);
            return (int)Math.Ceiling(sz.Height) + 2;
        }

        // ─── Строки характеристик ─────────────────────────────────────────────
        record StatLine(string Symbol, Color SymColor, string Value, string Name);

        List<StatLine> BuildStatLines()
        {
            var result = new List<StatLine>();
            if (_item == null) return result;

            int itemLevel = _slot?.Level ?? 0;

            void AddRef(StatCurveRef? r)
            {
                if (r == null || string.IsNullOrEmpty(r.Stat) || r.Stat == "None") return;
                string statName = CurveDb.StatDisplayName(r.Stat);
                if (string.IsNullOrEmpty(statName)) return;

                // CurveLevel == 0 → используем уровень предмета; иначе — фиксированный
                float lv = r.Level > 0 ? r.Level : Math.Max(1f, itemLevel);

                string valStr = "";
                if (CurveDb.Loaded)
                {
                    float? val = CurveDb.GetValue(r.Table, r.Row, lv);
                    if (val.HasValue) valStr = CurveDb.FormatNumber(val.Value);
                }

                result.Add(new StatLine(
                    CurveDb.StatSymbol(r.Stat),
                    StatColor(r.Stat),
                    valStr,
                    statName));
            }

            // Порядок: главная → вторичные → дополнительные
            AddRef(_item.MainStatCurve);
            if (_item.SecondaryStatCurves != null)
                foreach (var r in _item.SecondaryStatCurves) AddRef(r);
            if (_item.AddlStatCurves != null)
                foreach (var r in _item.AddlStatCurves) AddRef(r);

            return result;
        }

        // ─── Строки эффектов ──────────────────────────────────────────────────
        record EffectLine(string Text, Color Color);

        /// <summary>
        /// Вычисляет пул форматированных значений из DescCurves,
        /// затем:
        /// - если есть EffectsTexts → подставляет значения в шаблоны {0},{1},...
        /// - если EffectsTexts пуст и SetEffects пуст → показывает значения напрямую
        ///   ("Действует X мин." для SecondsAsMinutes, "+X%" для RatioToPercent и т.д.)
        /// </summary>
        List<EffectLine> BuildEffectLines(out string[] formattedVals)
        {
            formattedVals = Array.Empty<string>();
            var result = new List<EffectLine>();
            if (_item == null) return result;

            int itemLevel = _slot?.Level ?? 0;

            // Строим пул строковых значений {0},{1},...
            var vals = new List<string>();
            if (_item.DescCurves != null)
            {
                foreach (var d in _item.DescCurves)
                {
                    float lv = d.Level > 0 ? d.Level : Math.Max(1f, itemLevel);
                    float? v = CurveDb.Loaded ? CurveDb.GetValue(d.Table, d.Row, lv) : null;
                    vals.Add(v.HasValue ? CurveDb.FormatValue(v.Value, d.DisplayType, d.Inverse) : "?");
                }
                formattedVals = vals.ToArray();
            }

            var  locTexts        = _item.EffectsTextsLocalized;
            bool hasEffectsTexts = locTexts != null && locTexts.Count > 0;
            bool hasSetEffects   = _item.SetEffects != null && _item.SetEffects.Count > 0;

            if (hasEffectsTexts)
            {
                // Текстовые шаблоны с {0},{1},... → подставляем весь пул
                foreach (var tmpl in locTexts!)
                {
                    string text = TryFormat(tmpl, formattedVals);
                    if (!string.IsNullOrEmpty(text))
                        result.Add(new EffectLine(text, EffectColor));
                }
            }
            else if (!hasSetEffects && vals.Count > 0 && CurveDb.Loaded)
            {
                // Нет текстов эффектов и сетовых бонусов → показываем значения напрямую
                // (характерно для еды: Duration, AddlStats без EffectsDescriptions)
                for (int i = 0; i < (_item.DescCurves?.Count ?? 0); i++)
                {
                    var d = _item.DescCurves![i];
                    string formatted = vals[i];

                    string line = d.DisplayType == "SecondsAsMinutes"
                        ? $"{AppLanguage.T("Действует", "Duration:")} {formatted}"
                        : formatted;

                    if (!string.IsNullOrEmpty(line))
                        result.Add(new EffectLine(line, DurColor));
                }
            }

            return result;
        }

        static readonly Color DurColor = Color.FromArgb(130, 200, 130);

        // ─── Строки сетовых бонусов ───────────────────────────────────────────
        record SetLine(string Header, Color HeaderColor, string Desc);

        List<SetLine> BuildSetEffectLines()
        {
            var result = new List<SetLine>();
            if (_item?.SetEffects == null) return result;

            // Получаем пул значений
            BuildEffectLines(out var vals);

            foreach (var se in _item.SetEffects)
            {
                string header = string.IsNullOrEmpty(se.Name)
                    ? $"{AppLanguage.T("Бонус набора", "Set bonus")} (×{se.ActivationCount})"
                    : $"{se.Name} (×{se.ActivationCount})";
                string desc = TryFormat(se.Description, vals);
                result.Add(new SetLine(header, SetColor, desc));
            }
            return result;
        }

        // ─── Безопасное string.Format ─────────────────────────────────────────
        static string TryFormat(string template, string[] args)
        {
            if (string.IsNullOrEmpty(template)) return template;
            if (args.Length == 0)               return template;
            try
            {
                // Передаём весь пул как object[], чтобы работали {0},{1},... в любом порядке
                return string.Format(template, args.Cast<object>().ToArray());
            }
            catch
            {
                // Если шаблон не совпадает с количеством аргументов — возвращаем как есть
                return template;
            }
        }

        // ─── Цвета статов ────────────────────────────────────────────────────
        static Color StatColor(string stat) => stat switch
        {
            "Vitality"             => Color.FromArgb(255, 100, 100),
            "Defence"              => Color.FromArgb(100, 160, 255),
            "AttackPower"          => Color.FromArgb(255, 190,  60),
            "Strength"             => Color.FromArgb(255, 140,  60),
            "Agility"              => Color.FromArgb(100, 230, 130),
            "Endurance"            => Color.FromArgb(160, 130, 255),
            "Precision"            => Color.FromArgb( 80, 200, 255),
            "Mastery"              => Color.FromArgb(255, 220,  80),
            "Slash"                => Color.FromArgb(220, 180, 100),
            "Pierce"               => Color.FromArgb(180, 220, 255),
            "Blunt" or "Crude"     => Color.FromArgb(200, 160, 120),
            _                      => Color.FromArgb(190, 200, 210),
        };

        // ─── Отрисовка ───────────────────────────────────────────────────────
        void OnPaint(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.SmoothingMode     = SmoothingMode.AntiAlias;
            int w = Width, h = Height;

            // Фон
            using (var bb = new SolidBrush(BgColor)) g.FillRectangle(bb, 0, 0, w, h);
            using (var bp = new Pen(BorderColor))     g.DrawRectangle(bp, 0, 0, w - 1, h - 1);

            Color rarCol = _item != null ? Theme.Rarity(_item.Rarity) : Theme.Dim;

            // Полоса редкости (3px сверху)
            using (var rb = new SolidBrush(rarCol)) g.FillRectangle(rb, 0, 0, w, 3);

            int y = Pad;

            // ── Иконка + имя ─────────────────────────────────────────────────
            var iconRect = new Rectangle(Pad, y, IconSz, IconSz);
            using (var ib = new SolidBrush(Color.FromArgb(40, rarCol))) g.FillRectangle(ib, iconRect);
            using (var ip = new Pen(Color.FromArgb(80, rarCol)))         g.DrawRectangle(ip, iconRect);

            var icon = _item != null ? IconCache.Get(_item.IconRef) : null;
            if (icon != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(icon, iconRect);
                g.InterpolationMode = InterpolationMode.Default;
            }
            else if (_item != null)
            {
                string ltr = _item.DisplayName.Length > 0 ? _item.DisplayName[..1].ToUpper() : "?";
                using var lf = new Font("Segoe UI", 18f, FontStyle.Bold);
                using var lb = new SolidBrush(Color.FromArgb(180, rarCol));
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(ltr, lf, lb, iconRect, sf);
            }

            int tx = Pad + IconSz + 8;
            int tw = w - tx - Pad;

            // Имя
            if (_item != null)
            {
                using var nb = new SolidBrush(rarCol);
                g.DrawString(_item.DisplayName, FntName, nb,
                    new RectangleF(tx, y + 2, tw, LH + 4),
                    new StringFormat { Trimming = StringTrimming.EllipsisCharacter });
            }
            else
            {
                using var nb = new SolidBrush(DimColor);
                g.DrawString(AppLanguage.T(
                    $"Слот {_slot?.SlotIndex} (пустой)",
                    $"Slot {_slot?.SlotIndex} (empty)"), FntDesc, nb, tx, y + 8);
            }

            // Редкость · Категория
            int subY = y + LH + 5;
            if (_item != null)
            {
                string rarName = AppLanguage.IsRu
                    ? _item.Rarity switch
                    {
                        "Legendary" => "Легендарный", "Epic"     => "Эпический",
                        "Rare"      => "Редкий",      "Uncommon" => "Необычный",
                        "Common"    => "Обычный",     _          => _item.Rarity,
                    }
                    : _item.Rarity;
                string catName = ItemDatabase.CategoryDisplayName(_item.Category);
                string sub = string.IsNullOrEmpty(catName) ? rarName : $"{rarName}  ·  {catName}";
                using var sb = new SolidBrush(Color.FromArgb(170, rarCol));
                g.DrawString(sub, FntSub, sb, tx, subY);
            }

            // Уровень / количество
            string slotInfo = "";
            if (_slot != null && _slot.Level > 0) slotInfo += $"{AppLanguage.T("Ур.", "Lv.")} {_slot.Level}";
            if (_slot != null && _slot.Count > 1) slotInfo += (slotInfo.Length > 0 ? "  " : "") + $"×{_slot.Count}";
            if (slotInfo.Length > 0)
            {
                using var dimBr = new SolidBrush(DimColor);
                g.DrawString(slotInfo, FntSub, dimBr, tx, subY + SH);
            }

            y = Pad + IconSz + 6;

            if (_item == null) return;

            // ── Статы ────────────────────────────────────────────────────────
            var statLines = BuildStatLines();
            if (statLines.Count > 0)
            {
                y += 2;
                using (var sp = new Pen(SepColor)) g.DrawLine(sp, Pad, y, w - Pad, y);
                y += 6;

                foreach (var sl in statLines)
                {
                    // Символ
                    using (var symBr = new SolidBrush(sl.SymColor))
                        g.DrawString(sl.Symbol, FntStatVal, symBr, Pad, y);

                    int vx = Pad + 18;

                    if (!string.IsNullOrEmpty(sl.Value))
                    {
                        // Числовое значение
                        using (var vb = new SolidBrush(TextColor))
                            g.DrawString(sl.Value, FntStatVal, vb, vx, y);
                        float vw = g.MeasureString(sl.Value + " ", FntStatVal).Width;

                        // Название характеристики
                        using (var nb = new SolidBrush(DimColor))
                            g.DrawString(sl.Name, FntStat, nb, vx + vw, y);
                    }
                    else
                    {
                        // CT не загружены — только название
                        using (var nb = new SolidBrush(DimColor))
                            g.DrawString(sl.Name, FntStat, nb, vx, y);
                    }
                    y += LH;
                }
            }

            // ── Раздел текста ─────────────────────────────────────────────────
            var effectLines = BuildEffectLines(out var formattedVals);
            var setLines    = BuildSetEffectLines();

            bool hasDesc = !string.IsNullOrEmpty(_item.Description);
            bool hasVan  = !string.IsNullOrEmpty(_item.VanityText);
            bool hasText = effectLines.Count > 0 || setLines.Count > 0 || hasDesc || hasVan;

            if (hasText)
            {
                y += 4;
                using (var sp = new Pen(SepColor)) g.DrawLine(sp, Pad, y, w - Pad, y);
                y += 6;
            }

            // Тексты эффектов
            foreach (var el in effectLines)
            {
                int dh = DrawWrapped(g, el.Text, Pad, y, w - Pad * 2, FntSetDesc, el.Color);
                y += dh + 2;
            }

            // Сетовые бонусы
            foreach (var sl in setLines)
            {
                // Заголовок: "◈ Имя набора (×2):"
                using (var hb = new SolidBrush(sl.HeaderColor))
                    g.DrawString("◈ " + sl.Header + ":", FntSet, hb, Pad, y);
                y += LH;

                // Описание (с отступом)
                int dh = DrawWrapped(g, sl.Desc, Pad + 10, y, w - Pad * 2 - 10, FntSetDesc, TextColor);
                y += dh + 2;
            }

            // Описание предмета (подставляем {0},{1},... если есть DescCurves)
            if (hasDesc)
            {
                if (effectLines.Count > 0 || setLines.Count > 0) { y += 2; }
                string descText = TryFormat(_item.Description, formattedVals);
                int dh = DrawWrapped(g, descText, Pad, y, w - Pad * 2, FntDesc, DescColor);
                y += dh + 4;
            }

            // VanityText
            if (hasVan)
                DrawWrapped(g, _item.VanityText, Pad, y, w - Pad * 2, FntVanity, VanityColor);
        }

        // Рисует многострочный текст, возвращает высоту
        static int DrawWrapped(Graphics g, string text, int x, int y, int maxW, Font font, Color color)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var rf = new RectangleF(x, y, maxW, 2000);
            using var br = new SolidBrush(color);
            g.DrawString(text, font, br, rf, StringFormat.GenericTypographic);
            var sz = g.MeasureString(text, font, maxW, StringFormat.GenericTypographic);
            return (int)Math.Ceiling(sz.Height);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _inst == this) _inst = null;
            base.Dispose(disposing);
        }
    }
}
