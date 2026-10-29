using System.Windows;

namespace HotelKeeper.App.Services;

public static class ThemeManager
{
    static bool isDark = true;

    public static bool IsDark => isDark;

    public static void Initialize()
    {
        ApplyTheme(true);
    }

    public static void ToggleTheme()
    {
        ApplyTheme(!isDark);
    }

    public static void ApplyTheme(bool dark)
    {
        isDark = dark;
        var themeSource = dark ? "Resources/Styles/Theme.Dark.xaml" : "Resources/Styles/Theme.Light.xaml";
        var dict = new ResourceDictionary { Source = new Uri(themeSource, UriKind.Relative) };

        var appDicts = Application.Current.Resources.MergedDictionaries;
        for (int i = 0; i < appDicts.Count; i++)
        {
            var src = appDicts[i].Source?.OriginalString ?? "";
            if (src.Contains("Theme.xaml") || src.Contains("Theme.Dark.xaml") || src.Contains("Theme.Light.xaml"))
            {
                appDicts[i] = dict;
                return;
            }
        }
        appDicts.Insert(0, dict);
    }
}
