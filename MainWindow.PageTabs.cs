using System.Windows;
using System.Windows.Controls;

// beta0.0.6: FastFlags 画面と Mods 画面も、設定画面と同じようにタブで分けました。
namespace AZQLAUNCHER
{
    public partial class MainWindow
    {
        private (Button tab, FrameworkElement[] panels)[]? ffTabs, modTabs;

        private void BuildPageTabs()
        {
            ffTabs = new[]
            {
                (TabFfPerf, new FrameworkElement[] { FfCardPerf, FfCardLarge }),
                (TabFfGfx, new FrameworkElement[] { FfCardGfx }),
                (TabFfFlags, new FrameworkElement[] { FfCardFlags }),
            };
            modTabs = new[]
            {
                (TabModCursor, new FrameworkElement[] { ModCardCursor }),
                (TabModSound, new FrameworkElement[] { ModCardSound }),
                (TabModLook, new FrameworkElement[] { ModCardSky, ModCardFont }),
                (TabModFolder, new FrameworkElement[] { ModCardFolder }),
            };
        }

        private void FfTab_Click(object sender, RoutedEventArgs e) { if (sender is Button b) ShowPageTab(ffTabs, b, FfScroll); }
        private void ModTab_Click(object sender, RoutedEventArgs e) { if (sender is Button b) ShowPageTab(modTabs, b, PageMods); }

        private void ShowPageTab((Button tab, FrameworkElement[] panels)[]? tabs, Button active, ScrollViewer scroll)
        {
            if (tabs == null) return;
            foreach (var (tab, panels) in tabs)
            {
                bool on = tab == active;
                tab.Tag = on ? "Active" : null;
                foreach (var p in panels)
                {
                    p.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                    if (on) FadeIn(p);
                }
            }
            scroll.ScrollToVerticalOffset(0);
        }

        private void ApplyPageTabsLanguage()
        {
            TabFfPerf.Content = T("性能", "Performance");
            TabFfGfx.Content = T("グラフィックス", "Graphics");
            TabFfFlags.Content = T("フラグ管理", "Flag manager");
            TabModCursor.Content = T("カーソル", "Cursor");
            TabModSound.Content = T("効果音", "Sound");
            TabModLook.Content = T("スカイボックス・フォント", "Skybox & Font");
            TabModFolder.Content = T("Modsフォルダ", "Mods folder");
        }
    }
}
