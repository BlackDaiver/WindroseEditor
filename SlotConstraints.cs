namespace WindroseEditor
{
    /// <summary>
    /// Фиксированные слоты снаряжения/аксессуаров/боеприпасов.
    /// Имена слотов зависят от AppLanguage.
    /// </summary>
    public static class SlotConstraints
    {
        // ── Снаряжение: 5 фиксированных слотов ──────────────────────────────
        static readonly string[] _armorRu = { "Шлем", "Нагрудник", "Штаны", "Перчатки", "Сапоги" };
        static readonly string[] _armorEn = { "Helmet", "Chestplate", "Pants", "Gloves", "Boots" };

        // ── Аксессуары: 3 фиксированных слота ───────────────────────────────
        static readonly string[] _accessoryRu = { "Кольцо", "Ожерелье", "Сумка" };
        static readonly string[] _accessoryEn = { "Ring", "Necklace", "Bag" };

        // ── Боеприпасы: 2 фиксированных слота ───────────────────────────────
        static readonly string[] _ammoRu = { "Пули", "Порох" };
        static readonly string[] _ammoEn = { "Bullets", "Powder" };

        // ── Количество слотов ────────────────────────────────────────────────
        public static int ArmorSlotCount     => _armorRu.Length;      // 5
        public static int AccessorySlotCount => _accessoryRu.Length;  // 3
        public static int AmmoSlotCount      => _ammoRu.Length;       // 2

        // ── Имена слотов на текущем языке ────────────────────────────────────
        public static string? GetArmorName    (int i) => i >= 0 && i < _armorRu.Length     ? AppLanguage.T(_armorRu[i],     _armorEn[i])     : null;
        public static string? GetAccessoryName(int i) => i >= 0 && i < _accessoryRu.Length ? AppLanguage.T(_accessoryRu[i], _accessoryEn[i]) : null;
        public static string? GetAmmoName     (int i) => i >= 0 && i < _ammoRu.Length      ? AppLanguage.T(_ammoRu[i],      _ammoEn[i])      : null;
    }
}
