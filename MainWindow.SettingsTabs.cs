using System;
using System.Windows;
using System.Windows.Controls;

// beta0.0.7: 設定画面をカテゴリ (タブ) ごとに分けました。
//   一般 / 起動 / 複数起動と接続 / メンテナンス
namespace AZQLAUNCHER
{
    public partial class MainWindow
    {
        private void SettingsTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b) ShowSettingsTab(b);
        }

        // 押されたタブの内容だけを表示する
        private void ShowSettingsTab(Button active)
        {
            var tabs = new (Button tab, FrameworkElement panel)[]
            {
                (TabSetGeneral, SetGeneral),
                (TabSetLaunch, SetLaunch),
                (TabSetNet, SetNet),
                (TabSetAccounts, SetAccounts),
                (TabSetMaint, SetMaint),
            };

            FrameworkElement? shown = null;
            foreach (var (tab, panel) in tabs)
            {
                bool on = tab == active;
                tab.Tag = on ? "Active" : null;
                panel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                if (on) shown = panel;
            }

            PageSettings.ScrollToVerticalOffset(0);
            if (active == TabSetAccounts) RefreshAccountList();
            if (shown != null) FadeIn(shown);
        }

        // タブ名・カード見出し・説明文の表示言語を合わせる
        private void ApplySettingsLayoutLanguage()
        {
            TabSetGeneral.Content = T("一般", "General");
            TabSetLaunch.Content = T("起動", "Launch");
            TabSetNet.Content = T("複数起動と接続", "Multi-launch & Connection");
            TabSetMaint.Content = T("メンテナンス", "Maintenance");

            LblWindowSectionTitle.Text = T("ウィンドウの動作", "Window behavior");
            LblWindowSectionSub.Text = T("Robloxを起動したあとや最小化したときに、ランチャーのウィンドウをどうするかを決めます。",
                "Choose what the launcher window does after Roblox starts and when you minimize it.");
            LblLaunchSectionSub.Text = T("Robloxを起動するたびに、ランチャーが行う処理を選びます。",
                "Choose what the launcher does each time it starts Roblox.");
            LblReconnectTitle.Text = T("自動再接続", "Auto-reconnect");
            LblBackupTitle.Text = T("設定のバックアップ", "Settings backup");
            LblBackupSub.Text = T("ランチャーの設定をファイルに保存したり、別のPCや再インストールのあとに読み込んだりできます。",
                "Save the launcher's settings to a file, or load them on another PC or after reinstalling.");
            LblMaintenanceSub.Text = T("Robloxの調子が悪いときや、Modsで置き換えたファイルを元に戻したいときに使います。",
                "Use these when Roblox misbehaves or when you want to put back files that Mods replaced.");
            LblAboutTitle.Text = T("ガイド", "Guide");

            UpdateAutoCloseNote();
            ApplyAccountsLanguage();
            BuildPageTabs();
            ApplyPageTabsLanguage();
        }

        // マルチインスタンス中は「起動後にランチャーを閉じる」を使わない (ランチャーが開いたままになる) ことを、設定画面にも示す
        private void UpdateAutoCloseNote()
        {
            if (ChkAutoClose == null || DescAutoClose == null) return;

            string text = T("起動後はウィンドウを隠してトレイに常駐し、Roblox の終了と同時にランチャーも終了します。",
                "Hides to the tray after launch and exits together with Roblox.");
            if (config.MultiInstance)
            {
                text += "\n" + T("マルチインスタンスがオンの間は、この設定は使われません。ランチャーは開いたままで、Robloxが終了しても自動では終了しません。",
                    "While Multi-instance is on, this setting is not used: the launcher stays open and does not exit when Roblox closes.");
            }
            DescAutoClose.Text = text;
            ChkAutoClose.IsEnabled = !config.MultiInstance;
        }
    }
}
