using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace NiuMa
{
    internal static class Palette
    {
        private static readonly Dictionary<string, Brush> cache = new Dictionary<string, Brush>();
        private static readonly Dictionary<string, string> dark = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            {"#F6F5EF", "#1B241F"}, {"#24352E", "#E5ECD9"}, {"#243D32", "#D3DFBB"}, {"#E1E4D9", "#38453A"}, {"#E1E4D8", "#38453A"},
            {"#546157", "#ACBBA2"}, {"#E9EBE2", "#354236"}, {"#E5E9DE", "#354535"}, {"#2B4737", "#D2DEBB"}, {"#223F35", "#28483D"},
            {"#EFEEE5", "#29372A"}, {"#F2EADF", "#3B342B"}, {"#ECEEE4", "#2E3C2F"}, {"#EEEFE7", "#29372A"}, {"#EBEDE2", "#2D3A2B"},
            {"#ECEFE3", "#2B382A"}, {"#E8EBDE", "#344432"}, {"#E7EADF", "#354133"}, {"#DCE1D1", "#3F4C38"}, {"#E9EDE0", "#2C3A29"},
            {"#EEEFE6", "#2E3A2C"}, {"#ECEEE3", "#2D3A2B"}, {"#52624D", "#C4D0B5"}, {"#52674A", "#C4D0B5"},
            {"#65765B", "#B8C7AA"}, {"#737F6D", "#A5B698"}, {"#947B67", "#CFB497"}, {"#7E8875", "#B1BDA3"},
            {"#2F513E", "#426C4F"}
        };
        public static bool IsDark(Settings settings)
        {
            if (settings.Theme != "System") return settings.Theme == "Dark";
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"))
                return key != null && Convert.ToInt32(key.GetValue("AppsUseLightTheme", 1)) == 0;
        }
        public static Brush Get(string value, bool isDark)
        {
            string key = (isDark ? "D" : "L") + value; Brush brush;
            if (cache.TryGetValue(key, out brush)) return brush;
            string mapped;
            if (isDark && dark.TryGetValue(value, out mapped)) value = mapped;
            brush = (Brush)new BrushConverter().ConvertFromString(value); brush.Freeze(); cache[key] = brush; return brush;
        }
        public static void Apply(Window window, bool isDark)
        {
            var keys = new List<object>(); foreach (object key in window.Resources.Keys) if (key is string && ((string)key).StartsWith("C_")) keys.Add(key);
            foreach (string key in keys) window.Resources[key] = Get("#" + key.Substring(2), isDark);
        }
    }
}
