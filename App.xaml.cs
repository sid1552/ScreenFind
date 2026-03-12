using System;
using System.Windows;

namespace ScreenFind
{
    public partial class App : Application
    {
        /// <summary>
        /// Swaps the theme resource dictionary at runtime.
        /// DynamicResource references in XAML automatically pick up the new colors.
        /// </summary>
        public static void ApplyTheme(bool isDark, bool highContrast = false)
        {
            string file;
            if (isDark)
                file = highContrast ? "DarkHighContrastTheme.xaml" : "DarkTheme.xaml";
            else
                file = highContrast ? "LightHighContrastTheme.xaml" : "LightTheme.xaml";

            var dict = new ResourceDictionary
            {
                Source = new Uri(file, UriKind.Relative)
            };

            var mergedDicts = Current.Resources.MergedDictionaries;
            mergedDicts.Clear();
            mergedDicts.Add(dict);
        }
    }
}
