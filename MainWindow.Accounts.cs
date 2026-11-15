using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;

// beta0.0.6: アカウントスイッチャー
//   Roblox のログイン情報ファイル (RobloxCookies.dat) をアカウントごとに保存し、差し替えて切り替えます。
//   このファイルは Windows のユーザーごとに暗号化 (DPAPI) されているため、このPCのこのユーザーでしか使えません。
//   パスワードやクッキーの中身をランチャーが読むことはなく、通信もしません。
namespace AZQLAUNCHER
{
    public class SavedAccount
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    public partial class MainWindow
    {
        private static string RobloxCookiePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Roblox\LocalStorage\RobloxCookies.dat");
        private static string AccountsDir => Path.Combine(dataDir, "accounts");
        private static string AccountFile(string id) => Path.Combine(AccountsDir, id + ".dat");

        private SavedAccount? ActiveAccount() => config.Accounts.Find(a => a.Id == config.ActiveAccountId);

        private bool RobloxBusyForAccounts()
        {
            if (GetRobloxPids().Count == 0) return false;
            SetAccountStatus(T("Roblox が起動中です。すべて終了してから切り替えてください。",
                "Roblox is running. Close all Roblox windows before switching accounts."));
            return true;
        }

        private void SetAccountStatus(string text)
        {
            if (LblAccountStatus != null) LblAccountStatus.Text = text;
            AddLog(text);
        }

        // 現在ログイン中のアカウントの最新状態を、保存済みデータに書き戻す (Roblox がログイン情報を更新していることがあるため)
        private void SnapshotActiveAccount()
        {
            var a = ActiveAccount();
            if (a == null || !File.Exists(RobloxCookiePath)) return;
            try
            {
                Directory.CreateDirectory(AccountsDir);
                File.Copy(RobloxCookiePath, AccountFile(a.Id), true);
            }
            catch (Exception ex) { AddLog("Account snapshot failed: " + ex.Message); }
        }

        private void SaveAccount_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtAccountName.Text.Trim();
            if (name.Length == 0)
            {
                SetAccountStatus(T("アカウントの名前を入力してください (例: メイン / サブ)。", "Enter a name for the account (e.g. Main / Alt)."));
                return;
            }
            if (!File.Exists(RobloxCookiePath))
            {
                SetAccountStatus(T("Roblox にログインしていません。先に Roblox を起動してログインし、終了してから保存してください。",
                    "You are not logged in to Roblox. Launch Roblox, log in, close it, then save."));
                return;
            }
            if (RobloxBusyForAccounts()) return;
            try
            {
                SnapshotActiveAccount();
                var acc = new SavedAccount { Id = Guid.NewGuid().ToString("N"), Name = name };
                Directory.CreateDirectory(AccountsDir);
                File.Copy(RobloxCookiePath, AccountFile(acc.Id), true);
                config.Accounts.Add(acc);
                config.ActiveAccountId = acc.Id;
                SaveSettings();
                TxtAccountName.Text = "";
                SetAccountStatus(TF($"「{name}」として保存しました。", $"Saved the current login as \"{name}\"."));
            }
            catch (Exception ex) { SetAccountStatus(T("保存に失敗しました: ", "Save failed: ") + ex.Message); }
            RefreshAccountList();
        }

        private void SwitchAccount(SavedAccount acc, bool launch)
        {
            if (RobloxBusyForAccounts()) return;
            string src = AccountFile(acc.Id);
            if (!File.Exists(src))
            {
                SetAccountStatus(T("保存データが見つかりません。削除して登録し直してください。", "Saved data is missing. Remove it and register again."));
                return;
            }
            try
            {
                if (acc.Id != config.ActiveAccountId) SnapshotActiveAccount();
                Directory.CreateDirectory(Path.GetDirectoryName(RobloxCookiePath)!);
                File.Copy(src, RobloxCookiePath, true);
                config.ActiveAccountId = acc.Id;
                SaveSettings();
                SetAccountStatus(TF($"「{acc.Name}」に切り替えました。", $"Switched to \"{acc.Name}\"."));
            }
            catch (Exception ex) { SetAccountStatus(T("切り替えに失敗しました: ", "Switch failed: ") + ex.Message); return; }
            RefreshAccountList();
            if (launch) LaunchRobloxClient("", bypass: false);
        }

        // ログアウトして、別のアカウントでログインできる状態にする (現在のアカウントは保存済みなら先に更新)
        private void NewLogin_Click(object sender, RoutedEventArgs e)
        {
            if (RobloxBusyForAccounts()) return;
            try
            {
                SnapshotActiveAccount();
                if (File.Exists(RobloxCookiePath)) File.Delete(RobloxCookiePath);
                config.ActiveAccountId = "";
                SaveSettings();
                SetAccountStatus(T("ログアウトしました。Roblox を起動して別のアカウントでログインし、終了後に名前を付けて保存してください。",
                    "Logged out. Launch Roblox, log in with another account, close it, then save it with a name."));
            }
            catch (Exception ex) { SetAccountStatus(T("失敗しました: ", "Failed: ") + ex.Message); }
            RefreshAccountList();
        }

        private void RemoveAccount(SavedAccount acc)
        {
            try { File.Delete(AccountFile(acc.Id)); } catch { }
            config.Accounts.Remove(acc);
            if (config.ActiveAccountId == acc.Id) config.ActiveAccountId = "";
            SaveSettings();
            SetAccountStatus(TF($"「{acc.Name}」を削除しました (Robloxのログインは変わりません)。", $"Removed \"{acc.Name}\" (your current Roblox login is unchanged)."));
            RefreshAccountList();
        }

        private void RefreshAccountList()
        {
            var active = ActiveAccount();
            if (TitleAccountBadge != null)
            {
                TitleAccountBadge.Visibility = active != null ? Visibility.Visible : Visibility.Collapsed;
                TitleAccountText.Text = active != null ? "👤 " + active.Name : "";
            }
            if (AccountList == null) return;
            AccountList.Children.Clear();
            LblAccountCurrent.Text = active != null
                ? TF($"現在のアカウント: {active.Name}", $"Current account: {active.Name}")
                : T("現在のアカウント: 未登録 (または未ログイン)", "Current account: not registered (or logged out)");

            if (config.Accounts.Count == 0)
            {
                AccountList.Children.Add(new TextBlock
                {
                    Text = T("まだアカウントがありません。Roblox にログインして終了したあと、名前を付けて保存してください。",
                        "No accounts yet. Log in to Roblox, close it, then save it with a name."),
                    Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush"),
                    FontSize = 12, TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            foreach (var acc in config.Accounts.ToArray())
            {
                bool isActive = acc.Id == config.ActiveAccountId;
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };

                var del = new Button { Content = "🗑", Style = (Style)FindResource("DangerButton"), Height = 34, Width = 40, Margin = new Thickness(8, 0, 0, 0),
                    ToolTip = T("このアカウントの保存データを削除", "Remove this saved account") };
                del.Click += (_, _) => RemoveAccount(acc);
                DockPanel.SetDock(del, Dock.Right); row.Children.Add(del);

                var go = new Button { Content = T("切り替えて起動", "Switch & launch"), Style = (Style)FindResource("SecondaryButton"), Height = 34, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 0, 12, 0) };
                go.Click += (_, _) => SwitchAccount(acc, launch: true);
                DockPanel.SetDock(go, Dock.Right); row.Children.Add(go);

                var sw = new Button { Content = isActive ? T("使用中", "In use") : T("切り替え", "Switch"), Style = (Style)FindResource("SecondaryButton"), Height = 34, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 0, 12, 0), IsEnabled = !isActive };
                sw.Click += (_, _) => SwitchAccount(acc, launch: false);
                DockPanel.SetDock(sw, Dock.Right); row.Children.Add(sw);

                row.Children.Add(new TextBlock
                {
                    Text = (isActive ? "● " : "○ ") + acc.Name,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
                    FontSize = 14, FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
                });
                AccountList.Children.Add(row);
            }
        }

        private void ApplyAccountsLanguage()
        {
            TabSetAccounts.Content = T("アカウント", "Accounts");
            LblAccountsTitle.Text = T("アカウントスイッチャー", "Account switcher");
            LblAccountsSub.Text = T("Roblox のログイン情報をアカウントごとに保存し、ワンクリックで切り替えます。パスワードは保存・送信しません。切り替えは Roblox を完全に終了してから行います。",
                "Save each Roblox login and switch with one click. Passwords are never stored or sent. Close Roblox completely before switching.");
            BtnSaveAccount.Content = T("現在のログインを保存", "Save current login");
            BtnNewLogin.Content = T("別のアカウントでログイン", "Log in with another account");
            LblAccountHow.Text = T("追加の手順: ①「別のアカウントでログイン」→ ② Roblox を起動してログイン → ③ Roblox を終了 → ④ 名前を入れて「現在のログインを保存」",
                "To add one: ① \"Log in with another account\" → ② launch Roblox and log in → ③ close Roblox → ④ enter a name and \"Save current login\"");
            RefreshAccountList();
        }
    }
}
