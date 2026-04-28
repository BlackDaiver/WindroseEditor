namespace WindroseEditor
{
    /// <summary>
    /// Текущий язык отображения предметов.
    /// EN = только R5DevCulture.
    /// RU = русская локаль, где есть; иначе R5DevCulture.
    /// </summary>
    public static class AppLanguage
    {
        /// <summary>true = русский (RU), false = английский (EN).</summary>
        public static bool IsRu { get; set; } = true;

        public static string Current => IsRu ? "RU" : "EN";

        /// <summary>Выбирает строку предмета: RU если IsRu и не пусто, иначе EN.</summary>
        internal static string Loc(string en, string ru) =>
            IsRu && !string.IsNullOrEmpty(ru) ? ru : en;

        /// <summary>Выбирает строку интерфейса: ru если IsRu, иначе en.</summary>
        public static string T(string ru, string en) => IsRu ? ru : en;
    }
}
