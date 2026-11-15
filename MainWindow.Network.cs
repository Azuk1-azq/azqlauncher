using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

// beta0.0.7: マルチインスタンス / サーバー地域の選択 / 自動再接続
// MainWindow.xaml.cs が大きいため、新機能はこのファイルにまとめています。
namespace AZQLAUNCHER
{
    // 「起動1回ぶん」の引き継ぎ情報 (再接続・地域の入り直しで、前の起動の状態を次へ渡す)
    internal sealed class LaunchContext
    {
        public string PlaceId = "";
        public string LinkCode = "";
        public bool UserPinned;                 // ユーザーがサーバーを指定した参加 (地域の入り直しはしない)
        public bool NoRejoin;                   // 予約サーバーなど、自動では入り直せない参加
        public int RegionAttemptsLeft = -1;     // 地域の入り直しの残り回数 (-1 = まだ始まっていない)
        public bool RegionDone;                 // 地域の判定が終わった (一致した / 打ち切った)
        public HashSet<string> TriedJobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public List<DateTime> ReconnectTimes = new List<DateTime>();
        public DateTime LastReconnect = DateTime.MinValue;

        // 起動引数 (roblox-player:... / roblox://...) から、サーバー指定の有無などを読み取る
        public static LaunchContext FromArgs(string? args)
        {
            string d = args ?? "";
            for (int i = 0; i < 2; i++)
            {
                try { d = Uri.UnescapeDataString(d); } catch { break; }
            }

            var c = new LaunchContext();
            var pm = Regex.Match(d, @"placeId=(\d+)", RegexOptions.IgnoreCase);
            if (pm.Success) c.PlaceId = pm.Groups[1].Value;

            var lm = Regex.Match(d, @"(?:privateServerLinkCode|linkCode)=([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase);
            if (lm.Success) c.LinkCode = lm.Groups[1].Value;

            bool hasAccessCode = Regex.IsMatch(d, @"accessCode=", RegexOptions.IgnoreCase);
            bool hasJob = Regex.IsMatch(d, @"(?:gameInstanceId|jobId)=", RegexOptions.IgnoreCase);
            bool plain = Regex.IsMatch(d, @"request=RequestGame(?![A-Za-z])", RegexOptions.IgnoreCase)
                      || Regex.IsMatch(d, @"^roblox://experiences/start\?placeId=\d+$", RegexOptions.IgnoreCase);

            // 見分けのつかない起動は「ユーザーが選んだサーバー」として扱い、勝手に入り直さない
            c.UserPinned = d.Length > 0 && (!plain || hasJob || c.LinkCode.Length > 0 || hasAccessCode);
            c.NoRejoin = hasAccessCode && c.LinkCode.Length == 0;
            return c;
        }
    }

    internal sealed class GeoInfo
    {
        public string Ip = "";
        public string CountryCode = "";
        public string City = "";
        public string Group = "Other";   // JP / Asia / NA / EU / SA / OC / Other
        public string Display => string.IsNullOrEmpty(City) ? CountryCode : City + ", " + CountryCode;
    }

    // 動いているRobloxクライアント1つぶん
    internal sealed class RobloxSession
    {
        public Process Proc = null!;
        public LaunchContext Ctx = null!;
        public DateTime Created = DateTime.Now;
        public DateTime Started = DateTime.Now;
        public string PlaceId = "";
        public string JobId = "";
        public string ServerIp = "";
        public int JoinSerial;                  // 参加するたびに増える (再接続が済んだかの判定用)
        public int ReconnectScheduledSerial = -1;
        public bool Joined;
        public bool HopTimeoutHandled;
        public GeoInfo? Geo;
        public bool HasExited
        {
            get { try { return Proc.HasExited; } catch { return true; } }
        }
    }

    public partial class MainWindow
    {
        private const int ReconnectDelaySec = 8;
        private const int MaxReconnectsPer30Min = 5;
        private const string SingletonMutexName = "ROBLOX_singletonMutex";
        private const string ChannelRegPath = @"Software\ROBLOX Corporation\Environments\RobloxPlayer\Channel";

        private readonly List<RobloxSession> sessions = new List<RobloxSession>();
        private readonly HashSet<string> claimedLogs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int reconnectInFlight;
        private Mutex? singletonMutex;
        private GeoInfo? myGeo;
        private static readonly Dictionary<string, GeoInfo> GeoCache = new Dictionary<string, GeoInfo>();

        // Roblox のログ (Bloxstrap の ActivityWatcher と同じ行を読む)
        private static readonly Regex JoiningGameRx = new Regex(@"! Joining game '([0-9a-fA-F\-]{36})' place (\d+) at ([0-9\.]+)", RegexOptions.Compiled);
        private static readonly Regex UdmuxRx = new Regex(@"UDMUX Address = ([0-9\.]+), Port = \d+ \| RCC Server Address = ([0-9\.]+), Port = \d+", RegexOptions.Compiled);
        private static readonly Regex ServerIdRx = new Regex(@"\[FLog::Network\] serverId: ([0-9\.]+)\|\d+", RegexOptions.Compiled);
        private static readonly Regex DisconnectReasonRx = new Regex(@"disconnect(?:ion)?[^\r\n]{0,80}?(?:reason|code)\D{0,6}(\d{3})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ConnectionLostRx = new Regex(@"Connection lost|lost connection to the server", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // =====================================================================
        //  設定画面との橋渡し
        // =====================================================================

        private static void SelectComboByTag(ComboBox combo, string? tag, int fallbackIndex)
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
            combo.SelectedIndex = fallbackIndex;
        }

        private void LoadNetworkSettingsToUi()
        {
            ChkMultiInstance.IsChecked = config.MultiInstance;
            ChkAutoReconnect.IsChecked = config.AutoReconnect;
            ChkReconnectOnDrop.IsChecked = config.ReconnectOnDrop;
            ChkReconnectOnDrop.IsEnabled = config.AutoReconnect;
            SelectComboByTag(CmbServerRegion, config.ServerRegion, 0);

            if (config.MultiInstance) EnsureSingletonMutex();
            UpdateAutoCloseNote();
        }

        private void SaveNetworkSettingsFromUi()
        {
            config.MultiInstance = ChkMultiInstance.IsChecked == true;
            config.AutoReconnect = ChkAutoReconnect.IsChecked == true;
            config.ReconnectOnDrop = ChkReconnectOnDrop.IsChecked == true;
            ChkReconnectOnDrop.IsEnabled = config.AutoReconnect;

            if (CmbServerRegion.SelectedItem is ComboBoxItem region)
                config.ServerRegion = region.Tag?.ToString() ?? "Auto";

            if (config.MultiInstance) EnsureSingletonMutex(); else ReleaseSingletonMutex();
            UpdateAutoCloseNote();
            UpdateNetworkSummary();
        }

        private void CmbServerRegion_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!isLoaded) return;
            SaveSettings();
        }

        private void OpenNetSettings_Click(object sender, RoutedEventArgs e)
        {
            Nav_Click(NavSettings, e);
            ShowSettingsTab(TabSetNet);
            Dispatcher.BeginInvoke(new Action(() => CardNetwork.BringIntoView()), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private string RegionName(string? tag)
        {
            switch (tag)
            {
                case "Auto": return T("自動", "Automatic");
                case "JP": return T("日本", "Japan");
                case "Asia": return T("アジア", "Asia");
                case "NA": return T("北米", "North America");
                case "EU": return T("ヨーロッパ", "Europe");
                case "SA": return T("南米", "South America");
                case "OC": return T("オセアニア", "Oceania");
                default: return T("その他", "Other");
            }
        }

        private void UpdateNetworkSummary()
        {
            if (DashNetText == null) return;
            string on = T("オン", "On");
            string off = T("オフ", "Off");

            string reconnect = !config.AutoReconnect
                ? off
                : (config.ReconnectOnDrop ? T("放置・回線切断", "Idle and drops") : T("放置キックのみ", "Idle kick only"));

            string multi = config.MultiInstance ? on : off;
            string region = RegionName(config.ServerRegion);

            var parts = new List<string>
            {
                TF($"マルチ起動: {multi}", $"Multi-instance: {multi}"),
                TF($"地域: {region}", $"Region: {region}"),
                TF($"自動再接続: {reconnect}", $"Auto-reconnect: {reconnect}")
            };
            DashNetText.Text = string.Join("   ・   ", parts);
        }

        private void ApplyNetworkLanguage()
        {
            LblLaunchHint.Text = T("「起動」はFastFlagsとModsを適用して起動します。「標準起動」は何も適用せず、そのままのRobloxを起動します。",
                "“Launch” applies your FastFlags and Mods. “Vanilla Launch” starts Roblox as-is, with nothing applied.");
            LblDashNetTitle.Text = T("接続の設定", "Connection settings");
            BtnDashNetSettings.Content = T("設定を開く", "Open settings");

            LblNetSectionTitle.Text = T("マルチインスタンス", "Multi-instance");
            LblNetSectionSub.Text = T("Robloxを複数同時に動かしたり、接続先や切断されたときの動作を設定します。変更はすぐ保存され、次の起動から使われます。",
                "Run several Roblox clients at once and choose where you connect and what happens when you get disconnected. Changes are saved right away and used from the next launch.");

            ChkMultiInstance.Content = T("マルチインスタンス (Robloxを複数同時に起動する)", "Multi-instance (run several Roblox clients at once)");
            DescMultiInstance.Text = T("ランチャーから起動するたびに、別のRobloxを新しく開きます。Robloxが1つも動いていない状態で、ランチャーから起動してください (普通に起動済みのRobloxがあると効きません)。同じアカウントで同じサーバーに入ると、先に入っていた方が切断されます。",
                "Every launch from this launcher opens a new Roblox client. Start from here while no Roblox is running (a client that was started the normal way blocks this). Joining the same server with the same account disconnects the first one.")
                + "\n" + T("オンの間は、Robloxを起動してもランチャーは閉じず (隠れず)、Robloxがすべて終了しても自動では終了しません。",
                "While this is on, the launcher stays open after Roblox starts and does not exit when Roblox closes.");

            LblServerRegionTitle.Text = T("接続するサーバー地域", "Preferred server region");
            CmbRegionAuto.Content = T("自動 (Robloxにおまかせ)", "Automatic (let Roblox choose)");
            CmbRegionJP.Content = T("日本", "Japan");
            CmbRegionAsia.Content = T("アジア全体 (日本を含む)", "All of Asia (including Japan)");
            CmbRegionNA.Content = T("北米", "North America");
            CmbRegionEU.Content = T("ヨーロッパ", "Europe");
            CmbRegionSA.Content = T("南米", "South America");
            CmbRegionOC.Content = T("オセアニア", "Oceania");
            int attempts = Math.Clamp(config.RegionMaxAttempts, 0, 10);
            DescServerRegion.Text = TF($"Robloxは公式には接続地域を指定できません。そのため、入ったサーバーの場所をIPアドレスから調べ、希望と違うときは別のサーバーに入り直します (最大{attempts}回)。サーバーを指定した参加やプライベートサーバーでは何もしません。場所の確認には外部の位置情報サービス (ipwho.is など) を使います。",
                $"Roblox has no official way to pick a region, so the launcher looks up where the server you joined is (from its IP address) and re-joins another server if it is not in your region (up to {attempts} times). Nothing happens for joins to a specific or private server. A third-party location service (ipwho.is etc.) is used for the lookup.");

            ChkAutoReconnect.Content = T("放置キックされたら自動で再接続する", "Reconnect automatically when kicked for being idle");
            DescAutoReconnect.Text = T("Robloxは20分間操作しないと切断されます (エラー278)。そのときは同じサーバー (入れなければ別のサーバー) に自動で入り直します。短い間に何度も切断されるときは、延々と繰り返さずに止めます。",
                "Roblox disconnects you after 20 minutes without input (error 278). The launcher then re-joins the same server (or another one if that fails). If it keeps happening in a short time, it stops instead of looping forever.");
            ChkReconnectOnDrop.Content = T("回線の切断でも自動で再接続する", "Also reconnect when the connection drops");
            DescReconnectOnDrop.Text = T("回線の切断 (エラー277など) でも入り直します。BANやキックなど、ゲーム側が意図して切断した場合は再接続しません。",
                "Also re-joins after a dropped connection (error 277 etc.). It never reconnects after a ban or a kick by the game itself.");

            LblCursorSectionTitle.Text = T("マウスカーソル", "Mouse cursor");
            BtnRepairRoblox.ToolTip = T("RobloxのClientSettingsフォルダの読み取り専用を外し、設定を書き込めるようにします", "Removes the read-only flag from Roblox's ClientSettings folder so settings can be written");
            BtnRestoreMods.ToolTip = T("Modsで置き換えたRobloxの元のファイルを戻します", "Puts back the original Roblox files that Mods replaced");

            UpdateNetworkSummary();
        }

        // =====================================================================
        //  マルチインスタンス
        // =====================================================================

        // Roblox は "ROBLOX_singletonMutex" があると2つ目の起動を断る。ランチャーが先に持っておくことで、複数同時に起動できる。
        private void EnsureSingletonMutex()
        {
            if (!config.MultiInstance || singletonMutex != null) return;
            try
            {
                singletonMutex = new Mutex(true, SingletonMutexName, out bool createdNew);
                if (createdNew)
                {
                    AddLog(T("マルチインスタンスを有効にしました。", "Multi-instance is on."));
                }
                else
                {
                    AddLog(T("注意: Roblox がすでに動いているため、マルチインスタンスが効かない可能性があります。Robloxをすべて閉じてから、ランチャーで起動し直してください。",
                        "Note: Roblox is already running, so multi-instance may not work. Close every Roblox window and launch again from the launcher."));
                }
            }
            catch (Exception ex)
            {
                AddLog(TF($"マルチインスタンスを有効にできませんでした: {ex.Message}", $"Could not turn on multi-instance: {ex.Message}"));
            }
        }

        private void ReleaseSingletonMutex()
        {
            if (singletonMutex == null) return;
            try { singletonMutex.Dispose(); } catch { }
            singletonMutex = null;
            AddLog(T("マルチインスタンスを無効にしました。", "Multi-instance is off."));
        }

        // =====================================================================
        //  Roblox の更新チャンネル (機能は廃止)
        // =====================================================================

        // 以前のバージョンでランチャーが書き込んだ更新チャンネルが Roblox の設定 (レジストリ) に残っていたら、
        // 一度だけ消して通常版 (LIVE) に戻す。ランチャーが書いた値がなければ何もしない。
        private void RestoreRobloxChannel()
        {
            if (string.IsNullOrEmpty(config.AppliedChannel)) return;
            try
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(ChannelRegPath, true))
                {
                    if (key != null)
                    {
                        key.DeleteValue("www.roblox.com", false);
                        key.DeleteValue("", false);
                    }
                }
                config.AppliedChannel = "";
                try { WriteConfigFile(); } catch { }
                AddLog(T("Robloxの更新チャンネルを LIVE (通常版) に戻しました。", "Switched the Roblox update channel back to LIVE (stable)."));
            }
            catch (Exception ex)
            {
                AddLog(TF($"更新チャンネルを元に戻せませんでした: {ex.Message}", $"Could not restore the update channel: {ex.Message}"));
            }
        }

        // =====================================================================
        //  接続の監視 (サーバー地域の確認・自動再接続)
        // =====================================================================

        private void StartSessionMonitor(Process beta, string launchArgs, LaunchContext? ctx)
        {
            var s = new RobloxSession { Proc = beta, Ctx = ctx ?? LaunchContext.FromArgs(launchArgs) };
            try { s.Started = beta.StartTime; } catch { s.Started = DateTime.Now; }
            sessions.Add(s);
            _ = MonitorSessionAsync(s);
        }

        private string? FindPlayerLog(RobloxSession s)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Roblox\logs");
                if (!Directory.Exists(dir)) return null;
                DateTime earliest = s.Started.AddSeconds(-10);
                return new DirectoryInfo(dir).GetFiles("*_Player_*.log")
                    .Where(f => f.CreationTime >= earliest && !claimedLogs.Contains(f.FullName))
                    .OrderBy(f => f.CreationTime)
                    .Select(f => f.FullName)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        private sealed class LogTail
        {
            private readonly string path;
            private long pos;
            private string partial = "";

            public LogTail(string path) { this.path = path; }

            public List<string> ReadNew()
            {
                var result = new List<string>();
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (fs.Length < pos) pos = 0;
                    if (fs.Length == pos) return result;

                    fs.Seek(pos, SeekOrigin.Begin);
                    var buf = new byte[fs.Length - pos];
                    int total = 0;
                    while (total < buf.Length)
                    {
                        int n = fs.Read(buf, total, buf.Length - total);
                        if (n <= 0) break;
                        total += n;
                    }
                    pos += total;

                    string[] parts = (partial + Encoding.UTF8.GetString(buf, 0, total)).Split('\n');
                    partial = parts[parts.Length - 1];
                    for (int i = 0; i < parts.Length - 1; i++) result.Add(parts[i].TrimEnd('\r'));
                }
                catch { }
                return result;
            }
        }

        private async Task MonitorSessionAsync(RobloxSession s)
        {
            string? logPath = null;
            try
            {
                var sw = Stopwatch.StartNew();
                while (!s.HasExited && sw.Elapsed < TimeSpan.FromSeconds(90))
                {
                    logPath = FindPlayerLog(s);
                    if (logPath != null) break;
                    await Task.Delay(1000);
                }
                if (logPath == null)
                {
                    if (!s.HasExited)
                        AddLog(T("Robloxのログファイルが見つからないため、接続の監視はできません。", "Could not find Roblox's log file, so the connection cannot be monitored."));
                    return;
                }

                claimedLogs.Add(logPath);
                var tail = new LogTail(logPath);

                while (!s.HasExited)
                {
                    foreach (string line in tail.ReadNew())
                    {
                        try { await HandleLogLineAsync(s, line); }
                        catch (Exception ex) { AddLog(TF($"接続の監視でエラー: {ex.Message}", $"Connection monitor error: {ex.Message}")); }
                    }

                    // 地域の入り直しで入ったサーバーに入れなかった (満員など) ときは、次の候補へ進む
                    if (!s.Joined && !s.HopTimeoutHandled && s.Ctx.RegionAttemptsLeft >= 0 && !s.Ctx.RegionDone
                        && (DateTime.Now - s.Created) > TimeSpan.FromSeconds(80))
                    {
                        s.HopTimeoutHandled = true;
                        AddLog(T("サーバーに入れなかったようです。別のサーバーを試します。", "Could not get into that server. Trying another one."));
                        await HopRegionAsync(s, null, config.ServerRegion);
                    }

                    await Task.Delay(1000);
                }
            }
            catch (Exception ex)
            {
                AddLog(TF($"接続の監視でエラー: {ex.Message}", $"Connection monitor error: {ex.Message}"));
            }
            finally
            {
                if (logPath != null) claimedLogs.Remove(logPath);
                sessions.Remove(s);
            }
        }

        private async Task HandleLogLineAsync(RobloxSession s, string line)
        {
            Match m = JoiningGameRx.Match(line);
            if (m.Success)
            {
                s.JobId = m.Groups[1].Value;
                s.PlaceId = m.Groups[2].Value;
                s.ServerIp = m.Groups[3].Value;
                s.Joined = false;
                s.JoinSerial++;
                return;
            }

            m = UdmuxRx.Match(line);
            if (m.Success)
            {
                s.ServerIp = m.Groups[2].Value;   // 実際のゲームサーバー側のアドレス
                return;
            }

            m = ServerIdRx.Match(line);
            if (m.Success)
            {
                if (s.ServerIp.Length == 0) s.ServerIp = m.Groups[1].Value;
                await OnJoinedAsync(s);
                return;
            }

            if (!s.Joined) return;

            int reason = 0;
            bool lost = false;
            Match dm = DisconnectReasonRx.Match(line);
            if (dm.Success) int.TryParse(dm.Groups[1].Value, out reason);
            else if (ConnectionLostRx.IsMatch(line)) lost = true;
            else return;

            if (reason == 278 && config.AutoReconnect)
            {
                ScheduleReconnect(s, T("放置キック", "idle kick"));
            }
            else if ((reason == 277 || lost) && config.AutoReconnect && config.ReconnectOnDrop)
            {
                ScheduleReconnect(s, T("回線切断", "connection drop"));
            }
            else if (reason != 0)
            {
                AddLog(TF($"切断を検出しました (コード {reason})。自動再接続の対象ではないため、何もしません。", $"Detected a disconnect (code {reason}). It is not covered by auto-reconnect, so nothing was done."));
            }
        }

        private async Task OnJoinedAsync(RobloxSession s)
        {
            if (s.Joined) return;
            s.Joined = true;
            int serial = s.JoinSerial;
            AddLog(TF($"サーバーに接続しました (PlaceId {s.PlaceId})", $"Connected to a server (PlaceId {s.PlaceId})"));
            if (s.ServerIp.Length == 0) return;

            GeoInfo? geo = await LookupGeoAsync(s.ServerIp);
            if (geo == null)
            {
                AddLog(T("サーバーの場所を調べられませんでした。", "Could not look up the server's location."));
                return;
            }
            s.Geo = geo;
            string where = geo.Display;
            string group = RegionName(geo.Group);
            AddLog(TF($"サーバーの場所: {where} ({group})", $"Server location: {where} ({group})"));

            string want = config.ServerRegion;
            if (want == "Auto" || s.Ctx.UserPinned || s.Ctx.RegionDone) return;
            if (s.HasExited || s.JoinSerial != serial) return;

            if (GroupMatches(geo, want))
            {
                s.Ctx.RegionDone = true;
                string wantName = RegionName(want);
                AddLog(TF($"希望の地域 ({wantName}) のサーバーに入れました。", $"You are in a server in your preferred region ({wantName})."));
                return;
            }

            if (s.Ctx.RegionAttemptsLeft < 0) s.Ctx.RegionAttemptsLeft = Math.Clamp(config.RegionMaxAttempts, 0, 10);
            await HopRegionAsync(s, geo, want);
        }

        private static bool GroupMatches(GeoInfo g, string want) =>
            string.Equals(g.Group, want, StringComparison.OrdinalIgnoreCase) || (want == "Asia" && g.Group == "JP");

        // ---------------- サーバー地域の入り直し ----------------

        private async Task HopRegionAsync(RobloxSession s, GeoInfo? current, string want)
        {
            LaunchContext ctx = s.Ctx;
            string wantName = RegionName(want);

            if (ctx.RegionAttemptsLeft <= 0)
            {
                ctx.RegionDone = true;
                AddLog(TF($"希望の地域 ({wantName}) のサーバーが見つからなかったため、いまの接続のまま続けます。", $"No server in your preferred region ({wantName}) was found, so the current connection is kept."));
                return;
            }

            string place = s.PlaceId.Length > 0 ? s.PlaceId : ctx.PlaceId;
            if (place.Length == 0) { ctx.RegionDone = true; return; }

            ctx.RegionAttemptsLeft--;
            if (s.JobId.Length > 0) ctx.TriedJobs.Add(s.JobId);
            int left = ctx.RegionAttemptsLeft;
            AddLog(TF($"希望の地域 ({wantName}) と違うため、別のサーバーを探します (あと{left}回)。", $"Not in your preferred region ({wantName}), so looking for another server ({left} more tries)."));

            Interlocked.Increment(ref reconnectInFlight);
            try
            {
                GeoInfo? me = await GetMyGeoAsync();
                string? job = await PickServerForRegionAsync(place, want, me, ctx.TriedJobs);
                if (job == null)
                {
                    ctx.RegionAttemptsLeft = 0;
                    ctx.RegionDone = true;
                    AddLog(TF($"希望の地域 ({wantName}) のサーバーが見つからなかったため、いまの接続のまま続けます。", $"No server in your preferred region ({wantName}) was found, so the current connection is kept."));
                    return;
                }

                ctx.TriedJobs.Add(job);
                var next = new LaunchContext
                {
                    PlaceId = place,
                    UserPinned = false,
                    RegionAttemptsLeft = ctx.RegionAttemptsLeft,
                    TriedJobs = ctx.TriedJobs,
                    ReconnectTimes = ctx.ReconnectTimes,
                    LastReconnect = ctx.LastReconnect
                };
                await RestartClientAsync(s, BuildJoinUri(new RecentGame { PlaceId = place, JobId = job }), next);
            }
            catch (Exception ex)
            {
                ctx.RegionDone = true;
                AddLog(TF($"サーバーの入り直しに失敗しました: {ex.Message}", $"Could not re-join another server: {ex.Message}"));
            }
            finally
            {
                Interlocked.Decrement(ref reconnectInFlight);
            }
        }

        // 公開サーバー一覧から候補を選ぶ。一覧に地域は載っていないため、pingを手がかりにして、入った後に場所を確かめる。
        private async Task<string?> PickServerForRegionAsync(string placeId, string want, GeoInfo? me, HashSet<string> tried)
        {
            var list = new List<(string id, int ping)>();
            string? cursor = null;
            for (int page = 0; page < 2; page++)
            {
                string url = $"https://games.roblox.com/v1/games/{placeId}/servers/Public?sortOrder=Asc&excludeFullGames=true&limit=100";
                if (cursor != null) url += "&cursor=" + Uri.EscapeDataString(cursor);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using HttpResponseMessage resp = await Http.GetAsync(url, cts.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    int code = (int)resp.StatusCode;
                    AddLog(TF($"サーバー一覧を取得できませんでした (HTTP {code})。", $"Could not get the server list (HTTP {code})."));
                    break;
                }

                using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement el in data.EnumerateArray())
                    {
                        string id = "";
                        if (el.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String) id = idEl.GetString() ?? "";
                        if (id.Length == 0 || tried.Contains(id)) continue;

                        int ping = -1;
                        if (el.TryGetProperty("ping", out JsonElement pEl) && pEl.ValueKind == JsonValueKind.Number && pEl.TryGetInt32(out int pv)) ping = pv;
                        list.Add((id, ping));
                    }
                }

                cursor = null;
                if (root.TryGetProperty("nextPageCursor", out JsonElement nc) && nc.ValueKind == JsonValueKind.String) cursor = nc.GetString();
                if (string.IsNullOrEmpty(cursor)) break;
            }

            if (list.Count == 0) return null;

            // 自分のいる地域を希望しているならping順で近い方から、そうでなければ遠い方から探す
            bool near = me != null && GroupMatches(me, want);
            var ordered = near
                ? list.OrderBy(x => x.ping < 0 ? int.MaxValue : x.ping).ToList()
                : list.OrderByDescending(x => x.ping).ToList();
            var top = ordered.Take(8).ToList();
            return top[Random.Shared.Next(top.Count)].id;
        }

        // ---------------- 自動再接続 ----------------

        private void ScheduleReconnect(RobloxSession s, string kind)
        {
            if (s.ReconnectScheduledSerial == s.JoinSerial) return;
            s.ReconnectScheduledSerial = s.JoinSerial;
            s.Joined = false;
            _ = ReconnectAsync(s, kind);
        }

        private async Task ReconnectAsync(RobloxSession s, string kind)
        {
            int serial = s.JoinSerial;
            Interlocked.Increment(ref reconnectInFlight);
            try
            {
                AddLog(TF($"切断を検出しました ({kind})。{ReconnectDelaySec}秒後に再接続します...", $"Detected a disconnect ({kind}). Reconnecting in {ReconnectDelaySec} seconds..."));
                await Task.Delay(ReconnectDelaySec * 1000);

                if (s.HasExited) return;                 // 自分で閉じた
                if (s.JoinSerial != serial) return;      // 自分で入り直した

                LaunchContext ctx = s.Ctx;
                DateTime now = DateTime.Now;
                ctx.ReconnectTimes.RemoveAll(t => now - t > TimeSpan.FromMinutes(30));
                if (ctx.ReconnectTimes.Count >= MaxReconnectsPer30Min)
                {
                    AddLog(TF($"30分以内に{MaxReconnectsPer30Min}回再接続したため、自動再接続を止めました。", $"Auto-reconnect stopped: it already reconnected {MaxReconnectsPer30Min} times in 30 minutes."));
                    return;
                }
                if (ctx.NoRejoin)
                {
                    AddLog(T("このサーバー (予約サーバーなど) には自動では入り直せません。", "This server (e.g. a reserved server) cannot be re-joined automatically."));
                    return;
                }

                string place = s.PlaceId.Length > 0 ? s.PlaceId : ctx.PlaceId;
                if (place.Length == 0)
                {
                    AddLog(T("入っていたゲームが分からないため、再接続できません。", "Cannot reconnect: the game you were in is unknown."));
                    return;
                }

                // 直前の再接続から2分以内にまた切れたときは、同じサーバーをあきらめて別のサーバーに入る
                bool again = (now - ctx.LastReconnect) < TimeSpan.FromSeconds(120);
                var target = new RecentGame { PlaceId = place };
                if (ctx.LinkCode.Length > 0) target.LinkCode = ctx.LinkCode;
                else if (!again && s.JobId.Length > 0) target.JobId = s.JobId;

                ctx.ReconnectTimes.Add(now);
                var next = new LaunchContext
                {
                    PlaceId = place,
                    LinkCode = ctx.LinkCode,
                    UserPinned = ctx.UserPinned,
                    NoRejoin = ctx.NoRejoin,
                    ReconnectTimes = ctx.ReconnectTimes,
                    LastReconnect = now
                };
                AddLog(T("再接続します。", "Reconnecting."));
                await RestartClientAsync(s, BuildJoinUri(target), next);
            }
            catch (Exception ex)
            {
                AddLog(TF($"再接続に失敗しました: {ex.Message}", $"Reconnect failed: {ex.Message}"));
            }
            finally
            {
                Interlocked.Decrement(ref reconnectInFlight);
            }
        }

        // いまのクライアントだけを閉じて、新しいクライアントで入り直す (ほかのインスタンスには触れない)
        private async Task RestartClientAsync(RobloxSession s, string uri, LaunchContext next)
        {
            try
            {
                if (!s.HasExited)
                {
                    s.Proc.Kill(true);
                    await Task.Run(() => s.Proc.WaitForExit(5000));
                }
            }
            catch { }

            var sw = Stopwatch.StartNew();
            while (isLaunching && sw.Elapsed < TimeSpan.FromSeconds(60)) await Task.Delay(500);

            LaunchRobloxClient(uri, bypass: false, ctx: next);
        }

        // ---------------- サーバーの場所 (IPアドレスから) ----------------

        private async Task<GeoInfo?> GetMyGeoAsync()
        {
            if (myGeo != null) return myGeo;
            myGeo = await LookupGeoAsync(null);
            return myGeo;
        }

        private async Task<GeoInfo?> LookupGeoAsync(string? ip)
        {
            string key = ip ?? "";
            if (GeoCache.TryGetValue(key, out GeoInfo? cached)) return cached;

            GeoInfo? g = null;
            try { g = await FetchGeoIpwhoAsync(ip); } catch { }
            if (g == null)
            {
                try { g = await FetchGeoIpinfoAsync(ip); } catch { }
            }
            if (g != null) GeoCache[key] = g;
            return g;
        }

        private static string GroupOf(string countryCode, string continentCode)
        {
            if (countryCode == "JP") return "JP";
            switch (continentCode)
            {
                case "AS": return "Asia";
                case "EU": return "EU";
                case "NA": return "NA";
                case "SA": return "SA";
                case "OC": return "OC";
                default: return "Other";
            }
        }

        private static async Task<GeoInfo?> FetchGeoIpwhoAsync(string? ip)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using HttpResponseMessage resp = await Http.GetAsync("https://ipwho.is/" + (ip ?? ""), cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            JsonElement r = doc.RootElement;
            if (r.TryGetProperty("success", out JsonElement ok) && ok.ValueKind == JsonValueKind.False) return null;

            string cc = r.TryGetProperty("country_code", out JsonElement c) ? (c.GetString() ?? "") : "";
            string cont = r.TryGetProperty("continent_code", out JsonElement k) ? (k.GetString() ?? "") : "";
            string city = r.TryGetProperty("city", out JsonElement t) ? (t.GetString() ?? "") : "";
            if (cc.Length == 0) return null;
            return new GeoInfo { Ip = ip ?? "", CountryCode = cc, City = city, Group = GroupOf(cc, cont) };
        }

        // 予備: ipinfo.io は大陸を返さないため、主な国だけ自前の表で振り分ける
        private static readonly Dictionary<string, string> CountryGroups = BuildCountryGroups();

        private static Dictionary<string, string> BuildCountryGroups()
        {
            var d = new Dictionary<string, string>();
            void Add(string group, string codes) { foreach (string c in codes.Split(' ')) d[c] = group; }
            Add("JP", "JP");
            Add("Asia", "SG HK TW KR CN IN ID TH VN MY PH AE SA IL QA PK BD LK KZ MO KH");
            Add("NA", "US CA MX");
            Add("SA", "BR AR CL CO PE UY EC");
            Add("OC", "AU NZ");
            Add("EU", "GB IE FR DE NL BE LU CH AT IT ES PT SE NO DK FI PL CZ SK HU RO BG GR UA LT LV EE IS RS HR SI TR RU");
            return d;
        }

        private static async Task<GeoInfo?> FetchGeoIpinfoAsync(string? ip)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            string url = string.IsNullOrEmpty(ip) ? "https://ipinfo.io/json" : $"https://ipinfo.io/{ip}/json";
            using HttpResponseMessage resp = await Http.GetAsync(url, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            JsonElement r = doc.RootElement;
            string cc = r.TryGetProperty("country", out JsonElement c) ? (c.GetString() ?? "") : "";
            string city = r.TryGetProperty("city", out JsonElement t) ? (t.GetString() ?? "") : "";
            if (cc.Length == 0) return null;
            return new GeoInfo { Ip = ip ?? "", CountryCode = cc, City = city, Group = CountryGroups.TryGetValue(cc, out string? g) ? g : "Other" };
        }
    }
}
