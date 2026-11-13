using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AZQLAUNCHER
{
    public class LauncherConfig
    {
        public bool IsFirstLaunch { get; set; } = true;
        public string Language { get; set; } = "System";

        public bool UnlockFPS { get; set; } = false;
        public int TargetFps { get; set; } = 9999;
        public bool DisablePostFx { get; set; } = false;
        public bool LowRenderQuality { get; set; } = false;
        public string GraphicsApi { get; set; } = "Auto";
        public string CustomFlagsJson { get; set; } = "{\n  \"DFIntTaskSchedulerTargetFps\": 240\n}";

        public bool EnableMods { get; set; } = true;
        public string CursorMode { get; set; } = "System";
        public string CustomCursorPath { get; set; } = "";
        public string DeathSoundPath { get; set; } = "";
        public string DeathSoundDisplayName { get; set; } = "";
        public string SkyboxFolderPath { get; set; } = "";
        public string CustomFontPath { get; set; } = "";

        public bool BypassLauncher { get; set; } = false;
        public bool ProtectCursor { get; set; } = true;
        public bool AutoRegisterProtocol { get; set; } = true;
        public bool AutoClose { get; set; } = true;
        public bool CleanClientSettings { get; set; } = true;
    }

    public partial class MainWindow : Window
    {
        private readonly string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        private readonly string modsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mods");
        private readonly string assetsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
        private readonly string toolsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools");
        private LauncherConfig config = new LauncherConfig();
        private bool isLoaded = false;
        private bool isRunningAsProtocol = false;
        private TextBlock? protocolLaunchText;
        private Window? protocolWindow;
        private System.Windows.Forms.NotifyIcon? trayIcon;

        private static readonly HashSet<string> AllowedFastFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DFIntTaskSchedulerTargetFps",
            "FFlagDisablePostFx",
            "FFlagDebugGraphicsPreferD3D11",
            "FFlagDebugGraphicsPreferD3D11FL10",
            "FFlagDebugGraphicsPreferVulkan",
            "FFlagDebugGraphicsPreferOpenGL",
            "DFFlagDebugRenderForceTechnologyVoxel",
            "FFlagDebugForceFutureIsBrightPhase2",
            "FFlagDebugForceFutureIsBrightPhase3",
            "DFIntDebugFRMQualityLevelOverride",
            "FIntRenderShadowIntensity",
            "FFlagGlobalWindRendering",
            "DFIntTextureQualityOverride",
            "DFFlagTextureQualityOverrideEnabled",
            "FIntDebugForceMSAASamples",
            "FFlagDebugDisableTelemetryEphemeralCounter",
            "FFlagDebugDisableTelemetryEphemeralStat",
            "FFlagDebugDisableTelemetryEventIngest",
            "FFlagDebugDisableTelemetryPoint",
            "FFlagDebugDisableTelemetryV2Counter",
            "FFlagDebugDisableTelemetryV2Event",
            "FFlagDebugDisableTelemetryV2Stat"
        };

        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CopyIcon(IntPtr hIcon);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private const int IDC_ARROW = 32512;
        private const int IDC_IBEAM = 32513;

        public MainWindow()
        {
            InitializeComponent();
            Directory.CreateDirectory(modsDir);
            Directory.CreateDirectory(assetsDir);
            Directory.CreateDirectory(toolsDir);

            bool configExistedAtStart = File.Exists(configPath);
            LoadSettings();
            ApplyLanguage();
            isLoaded = true;
            InitTray();

            if (config.AutoRegisterProtocol)
            {
                RegisterProtocol(silent: true);
            }
            else
            {
                UpdateProtocolStatusText(false);
            }

            CheckArguments();

            if (!isRunningAsProtocol && config.IsFirstLaunch)
            {
                WelcomeOverlay.Visibility = Visibility.Visible;
                config.IsFirstLaunch = false;
                SaveSettings();
            }

            // 新しいフォルダでの初回起動時は、隣にある以前のバージョンを探して引き継ぎを提案する
            if (!isRunningAsProtocol && !configExistedAtStart)
            {
                Dispatcher.BeginInvoke(new Action(PromptImportOnFirstRun));
            }
        }

        private string GetEffectiveLanguage()
        {
            if (config.Language == "ja" || config.Language == "en") return config.Language;
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ja", StringComparison.OrdinalIgnoreCase) ? "ja" : "en";
        }

        private void ApplyLanguage()
        {
            bool isJa = GetEffectiveLanguage() == "ja";

            NavDashboard.Content = isJa ? "🎮 ダッシュボード" : "🎮 Dashboard";
            NavFastFlags.Content = "⚡ FastFlags";
            NavMods.Content = isJa ? "🎨 スカイボックス・Mods" : "🎨 Skybox & Mods";
            NavLogs.Content = isJa ? "📜 ログ" : "📜 Logs";
            NavSettings.Content = isJa ? "⚙️ 設定" : "⚙️ Settings";

            LblQuickLaunchTitle.Text = isJa ? "クイック起動" : "Quick Launch";
            LblDashSub.Text = isJa ? "設定を適用してRobloxを起動します" : "Applies your settings, then launches Roblox";
            LblDashClientTitle.Text = isJa ? "Roblox クライアント" : "Roblox Client";
            LblDashFlagsTitle.Text = "FastFlags";
            LblDashModsTitle.Text = "Mods";
            UpdateDashboard();
            BtnLaunchRoblox.Content = isJa ? "▶ Robloxを直接起動" : "▶ Launch Roblox";
            BtnLaunchVanilla.Content = isJa ? "⏩ ランチャーを通さないで起動" : "⏩ Launch Without Launcher";

            LblFastFlagsTitle.Text = "FastFlags";
            LblFastFlagsSub.Text = isJa ? "ホワイトリスト準拠の安全なクライアント設定" : "Allowlist-compliant client settings";
            BtnResetFastFlags.Content = isJa ? "リセット" : "Reset";
            BtnSaveFastFlags.Content = isJa ? "💾 設定を保存" : "💾 Save Settings";
            LblPerfCardTitle.Text = isJa ? "パフォーマンス・フレームレート" : "Performance & Frame Rate";
            ChkUnlockFPS.Content = isJa ? "FPS上限を解放する" : "Unlock FPS Limit";
            LblTargetFps.Text = isJa ? "目標FPS上限:" : "Target FPS Limit:";
            ChkDisablePostFx.Content = isJa ? "PostFX (ブルーム・被写界深度等の重いエフェクト) を無効化" : "Disable PostFX (Bloom, Depth of Field, etc.)";
            ChkLowRenderQuality.Content = isJa ? "レンダリング品質を軽量化 (FRM品質レベル1固定)" : "Low Render Quality (Force FRM Level 1)";
            LblGraphicsCardTitle.Text = isJa ? "グラフィックス・レンダリング" : "Graphics & Rendering";
            LblGraphicsApi.Text = isJa ? "描画エンジン (API):" : "Graphics API:";
            CmbApiAuto.Content = isJa ? "自動 (デフォルト)" : "Automatic (Default)";
            LblCustomFlagsTitle.Text = isJa ? "カスタム FastFlags (JSONエディタ)" : "Custom FastFlags (JSON Editor)";
            BtnFormatJson.Content = isJa ? "JSON整形" : "Format JSON";

            LblModsTitle.Text = isJa ? "カーソル・死亡音・スカイボックス・Mods" : "Cursor, Death Sound, Skybox & Mods";
            ChkEnableMods.Content = isJa ? "カスタムMods・外観変更をRoblox起動時に自動適用する" : "Automatically apply custom Mods & visuals on launch";

            LblCursorTitle.Text = isJa ? "🖱️ マウスカーソル" : "🖱️ Mouse Cursor";
            RbCursorSystem.Content = isJa ? "マウスカーソルそのまま (Windowsで設定している現在のカーソルを使用)" : "Keep System Cursor (Use Current Windows Cursor)";
            RbCursorDefault.Content = isJa ? "Roblox標準カーソル" : "Roblox Default Cursor";
            RbCursorClassic.Content = isJa ? "昔のマウスカーソル (2013年以前のクラシックカーソル)" : "Classic Mouse Cursor (Pre-2013 Legacy Cursor)";
            RbCursorCustom.Content = isJa ? "カスタム画像 (.png) を使用" : "Use Custom Image (.png)";
            BtnSelectCursor.Content = isJa ? "カーソル画像 (.png) を選択" : "Select Cursor (.png)";
            BtnClearCursor.Content = isJa ? "解除" : "Clear";

            LblSoundTitle.Text = isJa ? "🔊 死亡時の効果音 (Oof / 任意の音声自動変換)" : "🔊 Death Sound (Oof / Auto Audio Converter)";
            BtnClassicOof.Content = isJa ? "クラシック Oof 音を設定" : "Use Classic Oof";
            BtnSelectDeathSound.Content = isJa ? "音声ファイルを選択 (.mp3 / .wav / .ogg等)" : "Select Audio (.mp3 / .wav / .ogg...)";
            BtnClearSound.Content = isJa ? "解除" : "Clear";

            LblSkyTitle.Text = isJa ? "🌌 カスタム スカイボックス (bk, dn, ft, lf, rt, up)" : "🌌 Custom Skybox (bk, dn, ft, lf, rt, up)";
            BtnSelectSky.Content = isJa ? "スカイボックスフォルダを選択" : "Select Skybox Folder";
            BtnClearSky.Content = isJa ? "解除" : "Clear";

            LblFontTitle.Text = isJa ? "🔤 カスタム フォント (.ttf / .otf)" : "🔤 Custom Font (.ttf / .otf)";
            BtnSelectFont.Content = isJa ? "フォントファイルを選択" : "Select Font File";
            BtnClearFont.Content = isJa ? "解除" : "Clear";

            LblModsFolderTitle.Text = isJa ? "📁 汎用 Mods フォルダ" : "📁 General Mods Folder";
            BtnOpenMods.Content = isJa ? "Modsフォルダを開く" : "Open Mods Folder";

            LblLogsTitle.Text = isJa ? "実行ログ" : "Execution Logs";
            BtnClearLogs.Content = isJa ? "ログをクリア" : "Clear Logs";
            BtnOpenRobloxLogs.Content = isJa ? "Robloxログフォルダを開く" : "Open Roblox Logs Folder";

            LblSettingsTitle.Text = isJa ? "ランチャー設定" : "Launcher Settings";
            LblLanguage.Text = isJa ? "表示言語 (Language):" : "Display Language:";
            CmbLangSystem.Content = isJa ? "システム言語 (自動)" : "System Language (Auto)";
            ChkBypassLauncher.Content = isJa ? "ブラウザから起動時もランチャーを通さないで標準起動する" : "Bypass Launcher on Browser Launch";
            ChkProtectCursor.Content = isJa ? "カスタムカーソルの適用を維持する" : "Keep custom cursor applied";
            ChkAutoRegisterProtocol.Content = isJa ? "ランチャー起動時にブラウザ連携を自動登録する" : "Auto-register browser protocol on startup";
            ChkAutoClose.Content = isJa ? "Robloxの起動後は画面を隠してトレイに常駐し、Roblox終了時に自動で終了する" : "After Roblox starts, hide to the tray and exit when Roblox closes";
            ChkCleanClientSettings.Content = isJa ? "Roblox起動前に古いClientAppSettingsをクリーンアップする" : "Clean up old ClientAppSettings before launching Roblox";
            LblProtocolTitle.Text = isJa ? "ブラウザ連携 (URIプロトコル)" : "Browser Integration (URI Protocol)";
            BtnReRegisterProtocol.Content = isJa ? "ブラウザ連携を手動で再登録" : "Re-register Protocol Manually";
            LblMaintenanceTitle.Text = isJa ? "メンテナンス・情報" : "Maintenance & Info";
            BtnOpenRobloxFolder.Content = isJa ? "Robloxのクライアントフォルダを開く" : "Open Roblox Client Folder";
            BtnRepairRoblox.Content = isJa ? "ClientSettings・読み取り専用解除" : "Reset ClientSettings & Permissions";
            BtnShowIntro.Content = isJa ? "機能紹介ガイド" : "Feature Guide";
            BtnImportData.Content = isJa ? "以前のバージョンから設定を引き継ぐ" : "Import Data from Previous Version";

            LblWelcomeTitle.Text = isJa ? "AZQLAUNCHER へようこそ！" : "Welcome to AZQLAUNCHER!";
            LblWelcomeSub.Text = isJa ? "主な機能のご紹介" : "Overview of Main Features";
            LblFeat1Title.Text = isJa ? "⚡ FastFlags ＆ FPS上限解放" : "⚡ FastFlags & FPS Unlocker";
            LblFeat1Desc.Text = isJa ? "ホワイトリスト準拠の安全設計で、FPS上限設定や描画エンジンの切り替えが可能です。" : "Allowlist-compliant design for FPS limits and graphics API switching.";
            LblFeat2Title.Text = isJa ? "🎨 カーソル・死亡音・スカイボックス・フォントのカスタマイズ" : "🎨 Cursor, Death Sound, Skybox & Font Customization";
            LblFeat2Desc.Text = isJa ? "Windowsの現在のカーソル維持、クラシックカーソル、内蔵Oof音や自動OGG変換、スカイボックス・UIフォント変更に対応しています。" : "Supports Windows current cursor, classic cursor, built-in Oof or auto-converted audio, skyboxes, and UI fonts.";
            LblFeat3Title.Text = isJa ? "🌐 ブラウザ連携 ＆ 標準起動切替" : "🌐 Browser Integration & Vanilla Bypass";
            LblFeat3Desc.Text = isJa ? "Webブラウザからの直接起動と、ランチャーを通さない標準起動の切り替えに対応しています。" : "Supports direct browser launches and bypassing launcher modifications.";
            BtnStartLauncher.Content = isJa ? "はじめる" : "Get Started";

            LblLaunching.Text = isJa ? "Robloxを起動しています..." : "Launching Roblox...";

            CursorPathText.Text = string.IsNullOrEmpty(config.CustomCursorPath) ? (isJa ? "未設定" : "Not set") : Path.GetFileName(config.CustomCursorPath);
            SkyboxPathText.Text = string.IsNullOrEmpty(config.SkyboxFolderPath) ? (isJa ? "未設定" : "Not set") : Path.GetFileName(config.SkyboxFolderPath);
            FontPathText.Text = string.IsNullOrEmpty(config.CustomFontPath) ? (isJa ? "未設定" : "Not set") : Path.GetFileName(config.CustomFontPath);

            if (string.IsNullOrEmpty(config.DeathSoundPath))
            {
                DeathSoundPathText.Text = isJa ? "現在の設定: 未設定 (Roblox標準)" : "Current: Not set (Default)";
            }
            else
            {
                string disp = string.IsNullOrEmpty(config.DeathSoundDisplayName) ? Path.GetFileName(config.DeathSoundPath) : config.DeathSoundDisplayName;
                DeathSoundPathText.Text = isJa ? $"現在の設定: {disp}" : $"Current: {disp}";
            }
        }

        private void UpdateProtocolStatusText(bool registered, string? error = null)
        {
            bool isJa = GetEffectiveLanguage() == "ja";
            if (!string.IsNullOrEmpty(error))
            {
                ProtocolStatusText.Text = isJa ? $"状態: 登録エラー ({error})" : $"Status: Error ({error})";
                ProtocolStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 118, 117));
            }
            else if (registered)
            {
                ProtocolStatusText.Text = isJa ? "状態: 有効 (roblox-player / roblox 登録済み)" : "Status: Active (roblox-player / roblox registered)";
                ProtocolStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 184, 148));
            }
            else
            {
                ProtocolStatusText.Text = isJa ? "状態: 自動登録オフ" : "Status: Auto-register OFF";
                ProtocolStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(170, 170, 170));
            }
        }

        private void AddLog(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            Dispatcher.Invoke(() =>
            {
                TxtLogs.AppendText($"[{timestamp}] {message}\n");
                TxtLogs.ScrollToEnd();
            });
        }

        private void Nav_Click(object sender, RoutedEventArgs e)
        {
            var pages = new FrameworkElement[] { PageDashboard, PageFastFlags, PageMods, PageLogs, PageSettings };
            var navs = new Button[] { NavDashboard, NavFastFlags, NavMods, NavLogs, NavSettings };

            foreach (var page in pages) page.Visibility = Visibility.Collapsed;
            foreach (var nav in navs) nav.Tag = null;

            Button clicked = (Button)sender;
            clicked.Tag = "Active";

            FrameworkElement? target = null;
            if (clicked == NavDashboard) target = PageDashboard;
            else if (clicked == NavFastFlags) target = PageFastFlags;
            else if (clicked == NavMods) target = PageMods;
            else if (clicked == NavLogs) target = PageLogs;
            else if (clicked == NavSettings) target = PageSettings;

            if (target != null)
            {
                target.Visibility = Visibility.Visible;
                FadeIn(target);
            }
        }

        private static void FadeIn(FrameworkElement element)
        {
            var duration = TimeSpan.FromMilliseconds(190);
            var shift = new TranslateTransform(0, 10);
            element.RenderTransform = shift;
            element.BeginAnimation(UIElement.OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 1, duration));
            shift.BeginAnimation(TranslateTransform.YProperty,
                new System.Windows.Media.Animation.DoubleAnimation(10, 0, duration)
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                    }
                });
        }

        private int CountActiveFlags()
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(config.CustomFlagsJson))
            {
                try
                {
                    var custom = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(config.CustomFlagsJson);
                    if (custom != null)
                    {
                        foreach (string key in custom.Keys)
                        {
                            if (AllowedFastFlags.Contains(key)) keys.Add(key);
                        }
                    }
                }
                catch { }
            }
            if (config.UnlockFPS) keys.Add("DFIntTaskSchedulerTargetFps");
            if (config.DisablePostFx) keys.Add("FFlagDisablePostFx");
            if (config.LowRenderQuality) keys.Add("DFIntDebugFRMQualityLevelOverride");
            if (config.GraphicsApi == "D3D11" || config.GraphicsApi == "D3D10" || config.GraphicsApi == "Vulkan" || config.GraphicsApi == "OpenGL")
            {
                keys.Add("GraphicsApi:" + config.GraphicsApi);
            }
            return keys.Count;
        }

        private void UpdateDashboard()
        {
            bool isJa = GetEffectiveLanguage() == "ja";
            var white = System.Windows.Media.Brushes.White;
            var muted = new SolidColorBrush(System.Windows.Media.Color.FromRgb(142, 151, 181));

            string? exe = GetLatestRobloxExe();
            if (exe == null)
            {
                DashClientText.Text = isJa ? "未検出" : "Not found";
                DashClientText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 118, 117));
            }
            else
            {
                DashClientText.Text = Path.GetFileName(Path.GetDirectoryName(exe)!);
                DashClientText.Foreground = white;
            }

            int flags = CountActiveFlags();
            DashFlagsText.Text = flags > 0 ? (isJa ? $"{flags} 件を適用" : $"{flags} applied") : (isJa ? "なし" : "None");
            DashFlagsText.Foreground = flags > 0 ? white : muted;

            DashModsText.Text = config.EnableMods ? (isJa ? "有効" : "Enabled") : (isJa ? "無効" : "Disabled");
            DashModsText.Foreground = config.EnableMods
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 184, 148))
                : muted;
        }

        private void LoadSettings()
        {
            if (File.Exists(configPath))
            {
                try
                {
                    string json = File.ReadAllText(configPath);
                    var loaded = JsonSerializer.Deserialize<LauncherConfig>(json);
                    if (loaded != null) config = loaded;
                }
                catch (Exception ex)
                {
                    AddLog($"設定ファイル読み込みエラー: {ex.Message}");
                }
            }

            foreach (ComboBoxItem item in CmbLanguage.Items)
            {
                if (item.Tag?.ToString() == config.Language)
                {
                    CmbLanguage.SelectedItem = item;
                    break;
                }
            }
            if (CmbLanguage.SelectedItem == null) CmbLanguage.SelectedIndex = 0;

            ChkUnlockFPS.IsChecked = config.UnlockFPS;
            TxtTargetFps.Text = config.TargetFps.ToString();
            ChkDisablePostFx.IsChecked = config.DisablePostFx;
            ChkLowRenderQuality.IsChecked = config.LowRenderQuality;
            TxtCustomFlags.Text = config.CustomFlagsJson;

            foreach (ComboBoxItem item in CmbGraphicsApi.Items)
            {
                if (item.Tag?.ToString() == config.GraphicsApi)
                {
                    CmbGraphicsApi.SelectedItem = item;
                    break;
                }
            }
            if (CmbGraphicsApi.SelectedItem == null) CmbGraphicsApi.SelectedIndex = 0;

            ChkEnableMods.IsChecked = config.EnableMods;

            switch (config.CursorMode)
            {
                case "Default": RbCursorDefault.IsChecked = true; break;
                case "Classic": RbCursorClassic.IsChecked = true; break;
                case "Custom": RbCursorCustom.IsChecked = true; break;
                default: RbCursorSystem.IsChecked = true; break;
            }
            UpdateCustomCursorPanelState();

            ChkBypassLauncher.IsChecked = config.BypassLauncher;
            ChkProtectCursor.IsChecked = config.ProtectCursor;
            ChkAutoRegisterProtocol.IsChecked = config.AutoRegisterProtocol;
            ChkAutoClose.IsChecked = config.AutoClose;
            ChkCleanClientSettings.IsChecked = config.CleanClientSettings;
        }

        private void SaveSettings()
        {
            if (!isLoaded) return;

            if (CmbLanguage.SelectedItem is ComboBoxItem selectedLang)
            {
                config.Language = selectedLang.Tag?.ToString() ?? "System";
            }

            config.UnlockFPS = ChkUnlockFPS.IsChecked == true;
            if (int.TryParse(TxtTargetFps.Text, out int fps) && fps > 0)
            {
                config.TargetFps = fps;
            }
            config.DisablePostFx = ChkDisablePostFx.IsChecked == true;
            config.LowRenderQuality = ChkLowRenderQuality.IsChecked == true;
            config.CustomFlagsJson = TxtCustomFlags.Text;

            if (CmbGraphicsApi.SelectedItem is ComboBoxItem selectedApi)
            {
                config.GraphicsApi = selectedApi.Tag?.ToString() ?? "Auto";
            }

            config.EnableMods = ChkEnableMods.IsChecked == true;
            if (RbCursorDefault.IsChecked == true) config.CursorMode = "Default";
            else if (RbCursorClassic.IsChecked == true) config.CursorMode = "Classic";
            else if (RbCursorCustom.IsChecked == true) config.CursorMode = "Custom";
            else config.CursorMode = "System";

            config.BypassLauncher = ChkBypassLauncher.IsChecked == true;
            config.ProtectCursor = ChkProtectCursor.IsChecked == true;
            config.AutoRegisterProtocol = ChkAutoRegisterProtocol.IsChecked == true;
            config.AutoClose = ChkAutoClose.IsChecked == true;
            config.CleanClientSettings = ChkCleanClientSettings.IsChecked == true;

            try
            {
                string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(configPath, json);
            }
            catch { }

            UpdateDashboard();
        }

        private void Setting_Changed(object sender, RoutedEventArgs e) => SaveSettings();

        private void UpdateCustomCursorPanelState()
        {
            if (CustomCursorFilePanel == null) return;
            CustomCursorFilePanel.IsEnabled = RbCursorCustom.IsChecked == true;
            CustomCursorFilePanel.Opacity = RbCursorCustom.IsChecked == true ? 1.0 : 0.45;
        }

        private void CursorMode_Changed(object sender, RoutedEventArgs e)
        {
            UpdateCustomCursorPanelState();
            SaveSettings();
        }

        private void FastFlagUi_Changed(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            FastFlagsSaveStatusText.Text = GetEffectiveLanguage() == "ja" ? "未保存の変更があります" : "Unsaved changes";
            FastFlagsSaveStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(253, 203, 110));
        }

        private void FastFlagUi_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!isLoaded) return;
            FastFlagsSaveStatusText.Text = GetEffectiveLanguage() == "ja" ? "未保存の変更があります" : "Unsaved changes";
            FastFlagsSaveStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(253, 203, 110));
        }

        private void SaveFastFlags_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            string? robloxExe = GetLatestRobloxExe();
            bool isJa = GetEffectiveLanguage() == "ja";
            if (robloxExe != null)
            {
                try
                {
                    ApplyFastFlags(Path.GetDirectoryName(robloxExe)!);
                    FastFlagsSaveStatusText.Text = isJa ? "✓ 保存・反映しました" : "✓ Saved & Applied";
                    FastFlagsSaveStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 184, 148));
                    AddLog("FastFlags設定を保存しました。");
                }
                catch (Exception ex)
                {
                    FastFlagsSaveStatusText.Text = isJa ? "保存しましたが反映に失敗しました" : "Saved, but failed to apply";
                    FastFlagsSaveStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 118, 117));
                    AddLog($"FastFlags反映エラー: {ex.Message}");
                }
            }
            else
            {
                FastFlagsSaveStatusText.Text = isJa ? "保存しました (Roblox未検出のため起動時に反映)" : "Saved (Roblox not found; will apply on launch)";
                FastFlagsSaveStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(253, 203, 110));
                AddLog("FastFlags設定を保存しました (Robloxが見つからないため起動時に反映されます)。");
            }
        }

        private void ResetFastFlags_Click(object sender, RoutedEventArgs e)
        {
            ChkUnlockFPS.IsChecked = false;
            TxtTargetFps.Text = "9999";
            ChkDisablePostFx.IsChecked = false;
            ChkLowRenderQuality.IsChecked = false;
            CmbGraphicsApi.SelectedIndex = 0;
            TxtCustomFlags.Text = "{\n  \"DFIntTaskSchedulerTargetFps\": 240\n}";
            SaveSettings();
            FastFlagsSaveStatusText.Text = GetEffectiveLanguage() == "ja" ? "✓ 初期値にリセットしました" : "✓ Reset to defaults";
            FastFlagsSaveStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(116, 185, 255));
            AddLog("FastFlags設定をリセットしました。");
        }

        private void FormatJson_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(TxtCustomFlags.Text);
                if (parsed != null)
                {
                    TxtCustomFlags.Text = JsonSerializer.Serialize(parsed, new JsonSerializerOptions { WriteIndented = true });
                }
            }
            catch { }
        }

        private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!isLoaded) return;
            SaveSettings();
            ApplyLanguage();
            UpdateProtocolStatusText(config.AutoRegisterProtocol);
        }

        private void TxtCustomFlags_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!isLoaded) return;
            bool isJa = GetEffectiveLanguage() == "ja";
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(TxtCustomFlags.Text);
                if (parsed != null)
                {
                    int invalidCount = parsed.Keys.Count(k => !AllowedFastFlags.Contains(k));
                    if (invalidCount > 0)
                    {
                        JsonStatusText.Text = isJa
                            ? $"JSON正常 (注意: ホワイトリスト外の {invalidCount} 件は適用時に除外されます)"
                            : $"JSON Valid ({invalidCount} non-allowlisted keys will be ignored)";
                        JsonStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(253, 203, 110));
                    }
                    else
                    {
                        JsonStatusText.Text = isJa ? "JSONステータス: 正常 (すべてホワイトリスト内)" : "JSON Status: Valid (All allowlisted)";
                        JsonStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 184, 148));
                    }
                }
            }
            catch
            {
                JsonStatusText.Text = isJa ? "JSON構文エラー: 括弧やカンマを確認してください" : "JSON Syntax Error";
                JsonStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 118, 117));
            }
            FastFlagUi_Changed(sender, e);
        }

        private void RegisterProtocol_Click(object sender, RoutedEventArgs e) => RegisterProtocol(silent: false);

        private void RegisterProtocol(bool silent)
        {
            try
            {
                string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
                string[] schemes = { "roblox-player", "roblox" };

                foreach (string scheme in schemes)
                {
                    using (RegistryKey root = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{scheme}"))
                    {
                        root.SetValue("", $"URL:{scheme} Protocol");
                        root.SetValue("URL Protocol", "");
                    }
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{scheme}\shell\open\command"))
                    {
                        key.SetValue("", $"\"{exePath}\" \"%1\"");
                    }
                }

                UpdateProtocolStatusText(true);
                AddLog("ブラウザ連携プロトコル (roblox-player / roblox) を登録しました。");
            }
            catch (Exception ex)
            {
                UpdateProtocolStatusText(false, ex.Message);
                AddLog($"ブラウザ連携プロトコル登録エラー: {ex.Message}");
            }
        }

        private void CheckArguments()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && (args[1].StartsWith("roblox-player:") || args[1].StartsWith("roblox:")))
            {
                isRunningAsProtocol = true;
                AddLog("ブラウザからの起動リクエストを受信しました。");

                ShowInTaskbar = false;
                Loaded += (_, __) => Hide();
                protocolWindow = new Window
                {
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = System.Windows.Media.Brushes.Transparent,
                    Width = 300,
                    Height = 100,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Topmost = true
                };

                var border = new Border
                {
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(230, 18, 18, 22)),
                    CornerRadius = new CornerRadius(8),
                    BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 54)),
                    BorderThickness = new Thickness(1)
                };

                var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                protocolLaunchText = new TextBlock
                {
                    Text = GetEffectiveLanguage() == "ja" ? "Robloxを起動中..." : "Launching Roblox...",
                    Foreground = System.Windows.Media.Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = 16,
                    Margin = new Thickness(0, 0, 0, 10),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                sp.Children.Add(protocolLaunchText);
                sp.Children.Add(new ProgressBar
                {
                    IsIndeterminate = true,
                    Width = 150,
                    Height = 4,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 184, 148)),
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(58, 58, 72)),
                    BorderThickness = new Thickness(0)
                });

                border.Child = sp;
                protocolWindow.Content = border;
                protocolWindow.Show();

                LaunchRobloxClient(args[1], bypass: config.BypassLauncher);
            }
        }

        private string? GetLatestRobloxExe()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string versionsFolder = Path.Combine(localAppData, @"Roblox\Versions");

            if (!Directory.Exists(versionsFolder)) return null;

            return Directory.GetDirectories(versionsFolder)
                            .Select(dir => Path.Combine(dir, "RobloxPlayerBeta.exe"))
                            .Where(File.Exists)
                            .OrderByDescending(f => File.GetLastWriteTime(f))
                            .FirstOrDefault();
        }

        private void LaunchRoblox_Click(object sender, RoutedEventArgs e) => LaunchRobloxClient("", bypass: false);
        private void LaunchVanilla_Click(object sender, RoutedEventArgs e) => LaunchRobloxClient("", bypass: true);

        private async void LaunchRobloxClient(string launchArgs, bool bypass)
        {
            if (!isRunningAsProtocol) LaunchOverlay.Visibility = Visibility.Visible;

            string? robloxExe = GetLatestRobloxExe();
            if (robloxExe == null)
            {
                robloxExe = await InstallRobloxAsync();
                UpdateDashboard();
            }
            if (robloxExe == null)
            {
                AddLog("エラー: RobloxPlayerBeta.exe が見つかりませんでした。");
                MessageBox.Show("RobloxPlayerBeta.exe が見つかりません。Robloxがインストールされているか確認してください。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                if (!isRunningAsProtocol) LaunchOverlay.Visibility = Visibility.Collapsed;
                else Application.Current.Shutdown();
                return;
            }

            string targetVersionDir = Path.GetDirectoryName(robloxExe)!;
            AddLog($"対象クライアント検出: {targetVersionDir}");
            HashSet<string> knownVersionDirs = GetAllRobloxVersionDirs();

            try
            {
                foreach (var oldProc in Process.GetProcessesByName("RobloxPlayerBeta"))
                {
                    try
                    {
                        if (oldProc.MainWindowHandle == IntPtr.Zero)
                        {
                            oldProc.Kill();
                        }
                    }
                    catch { }
                }

                if (bypass)
                {
                    AddLog("「ランチャーを通さないで起動」モードで標準クライアントを起動します。");
                    string jsonPath = Path.Combine(targetVersionDir, "ClientSettings", "ClientAppSettings.json");
                    if (File.Exists(jsonPath))
                    {
                        try { File.Delete(jsonPath); } catch { }
                    }
                }
                else
                {
                    ApplyFastFlags(targetVersionDir);

                    if (config.EnableMods)
                    {
                        await ApplyAllModsAsync(targetVersionDir);
                    }
                    else
                    {
                        AddLog("Mods適用はオフに設定されています。");
                    }
                }

                AddLog("Robloxクライアントを起動しています...");

                Process? proc = null;
                string quotedArgs = string.IsNullOrEmpty(launchArgs) ? "" : "\"" + launchArgs.Replace("\"", "") + "\"";
                string launcherExe = Path.Combine(targetVersionDir, "RobloxPlayerLauncher.exe");

                if (!string.IsNullOrEmpty(launchArgs))
                {
                    if (File.Exists(launcherExe))
                    {
                        ProcessStartInfo psi = new ProcessStartInfo
                        {
                            FileName = launcherExe,
                            Arguments = quotedArgs,
                            WorkingDirectory = targetVersionDir,
                            UseShellExecute = true
                        };
                        proc = Process.Start(psi);
                    }
                    else
                    {
                        ProcessStartInfo psi = new ProcessStartInfo
                        {
                            FileName = robloxExe,
                            Arguments = quotedArgs,
                            WorkingDirectory = targetVersionDir,
                            UseShellExecute = true
                        };
                        proc = Process.Start(psi);
                    }
                }
                else
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = robloxExe,
                        Arguments = "--app",
                        WorkingDirectory = targetVersionDir,
                        UseShellExecute = true
                    };
                    proc = Process.Start(psi);
                }

                // Roblox本体のアップデート中は「アップデート中」と表示し、実際に起動するまで待つ
                var waitResult = await WaitForRobloxStartAsync(targetVersionDir, knownVersionDirs, bypass);
                targetVersionDir = waitResult.versionDir;
                UpdateDashboard();
                if (waitResult.beta != null) proc = waitResult.beta;
                SetLaunchStatus(GetEffectiveLanguage() == "ja" ? "Robloxを起動しています..." : "Launching Roblox...");

                Task protectionTask = Task.CompletedTask;
                if (!bypass && config.EnableMods && config.ProtectCursor && config.CursorMode != "Default")
                {
                    AddLog("カーソル上書き保護の監視タスクを開始しました。");
                    protectionTask = EnforceCursorProtectionAsync(targetVersionDir, proc);
                }

                bool robloxStarted = waitResult.beta != null;
                if (!robloxStarted)
                {
                    // 起動できなかった(キャンセル等)
                    if (!isRunningAsProtocol) LaunchOverlay.Visibility = Visibility.Collapsed;
                    else Application.Current.Shutdown();
                }
                else if (config.AutoClose || isRunningAsProtocol)
                {
                    // Bloxstrapと同じ: 起動を確認したらすぐ画面を隠し、トレイに常駐してRobloxを見張る。
                    // Robloxが終了したらランチャーも終了する。
                    CloseProtocolWindow();
                    LaunchOverlay.Visibility = Visibility.Collapsed;
                    Hide();
                    SetTrayStatus(true);
                    AddLog("Robloxの起動を確認しました。トレイに常駐して監視します。");

                    // カーソル保護の確認はバックグラウンドで続ける
                    try { await protectionTask; } catch { }
                    await WatchRobloxAsync();
                    Application.Current.Shutdown();
                }
                else
                {
                    CloseProtocolWindow();
                    await Task.Delay(2000);
                    LaunchOverlay.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                AddLog($"起動エラー: {ex.Message}");
                if (!isRunningAsProtocol) LaunchOverlay.Visibility = Visibility.Collapsed;
                else Application.Current.Shutdown();
            }
        }

        // Robloxが未インストールの場合、公式インストーラーを取得して実行し、インストール完了まで待つ。
        private async Task<string?> InstallRobloxAsync()
        {
            bool isJa = GetEffectiveLanguage() == "ja";

            if (!isRunningAsProtocol)
            {
                var answer = MessageBox.Show(
                    isJa ? "Robloxが見つかりません。公式インストーラーをダウンロードして、自動でインストールしますか?"
                         : "Roblox was not found. Download the official installer and install it now?",
                    "AZQLAUNCHER", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return null;
            }

            AddLog("Robloxが見つからないため、公式インストーラーを取得します。");
            SetLaunchStatus(isJa ? "Robloxのインストーラーをダウンロード中..." : "Downloading the Roblox installer...");

            string installerPath = Path.Combine(Path.GetTempPath(), "RobloxPlayerInstaller.exe");
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
                {
                    byte[] data = await client.GetByteArrayAsync("https://setup.rbxcdn.com/RobloxPlayerInstaller.exe");
                    await File.WriteAllBytesAsync(installerPath, data);
                }

                AddLog("Robloxのインストーラーを実行しました。完了するまで待機します...");
                SetLaunchStatus(isJa ? "Robloxをインストール中..." : "Installing Roblox...");

                Process? installer = Process.Start(new ProcessStartInfo
                {
                    FileName = installerPath,
                    UseShellExecute = true
                });

                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromMinutes(10))
                {
                    string? exe = GetLatestRobloxExe();
                    if (exe != null)
                    {
                        // ファイルの書き込みが終わるのを少し待つ
                        await Task.Delay(1500);
                        AddLog("Robloxのインストールが完了しました。");
                        return exe;
                    }

                    bool installerAlive = installer != null && !installer.HasExited;
                    if (!installerAlive && !IsRobloxUpdaterRunning() && sw.Elapsed > TimeSpan.FromSeconds(30))
                    {
                        AddLog("Robloxのインストールを確認できませんでした (キャンセルされた可能性があります)。");
                        return null;
                    }

                    await Task.Delay(500);
                }

                AddLog("Robloxのインストール待機がタイムアウトしました。");
                return null;
            }
            catch (Exception ex)
            {
                AddLog($"Robloxの自動インストールに失敗しました: {ex.Message}");
                return null;
            }
        }

        private void CloseProtocolWindow()
        {
            try { protocolWindow?.Close(); } catch { }
            protocolWindow = null;
        }

        // RobloxPlayerBeta が1つも動いていない状態が数秒続くまで待つ (再参加などで一瞬途切れても終了しない)
        private async Task WatchRobloxAsync()
        {
            int idleSeconds = 0;
            while (idleSeconds < 5)
            {
                if (FindRunningRobloxBeta() == null) idleSeconds++;
                else idleSeconds = 0;
                await Task.Delay(1000);
            }
            AddLog("Robloxが終了したため、ランチャーも終了します。");
        }

        private void InitTray()
        {
            bool isJa = GetEffectiveLanguage() == "ja";
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add(isJa ? "ランチャーを開く" : "Open Launcher", null, (_, __) => Dispatcher.Invoke(ShowFromTray));
            menu.Items.Add(isJa ? "Robloxを起動" : "Launch Roblox", null, (_, __) => Dispatcher.Invoke(() => LaunchRobloxClient("", bypass: false)));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(isJa ? "終了" : "Exit", null, (_, __) => Dispatcher.Invoke(() => Application.Current.Shutdown()));

            trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "AZQLAUNCHER",
                ContextMenuStrip = menu,
                Visible = true
            };
            trayIcon.DoubleClick += (_, __) => Dispatcher.Invoke(ShowFromTray);

            // 終了時にアイコンが残らないようにする
            Application.Current.Exit += (_, __) =>
            {
                if (trayIcon != null)
                {
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                    trayIcon = null;
                }
            };
        }

        private void SetTrayStatus(bool robloxRunning)
        {
            if (trayIcon == null) return;
            bool isJa = GetEffectiveLanguage() == "ja";
            trayIcon.Text = robloxRunning
                ? (isJa ? "AZQLAUNCHER - Roblox実行中" : "AZQLAUNCHER - Roblox running")
                : "AZQLAUNCHER";
        }

        private void ShowFromTray()
        {
            ShowInTaskbar = true;
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        // exeに埋め込んだアプリアイコンを使う。取得できなければ描画で代用する。
        private System.Drawing.Icon LoadAppIcon()
        {
            try
            {
                string? exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                    if (icon != null) return icon;
                }
            }
            catch { }
            return CreateTrayIcon();
        }

        private System.Drawing.Icon CreateTrayIcon()
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(System.Drawing.Color.Transparent);
                g.TranslateTransform(16, 16);
                g.RotateTransform(14);
                var rect = new RectangleF(-12, -12, 24, 24);
                using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(rect, System.Drawing.Color.FromArgb(61, 107, 255), System.Drawing.Color.FromArgb(139, 92, 255), 45f))
                {
                    g.FillRectangle(brush, rect);
                }
                using (var hole = new SolidBrush(System.Drawing.Color.FromArgb(12, 14, 22)))
                {
                    g.FillRectangle(hole, -4f, -4f, 8f, 8f);
                }
            }
            IntPtr hIcon = bmp.GetHicon();
            try
            {
                using var tmp = System.Drawing.Icon.FromHandle(hIcon);
                return (System.Drawing.Icon)tmp.Clone();
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }

        private static readonly string[] RobloxUpdaterProcessNames = { "RobloxPlayerLauncher", "RobloxPlayerInstaller" };

        private HashSet<string> GetAllRobloxVersionDirs()
        {
            string versionsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Roblox\Versions");
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (Directory.Exists(versionsFolder))
                {
                    foreach (string d in Directory.GetDirectories(versionsFolder)) result.Add(d);
                }
            }
            catch { }
            return result;
        }

        private static Process? FindRunningRobloxBeta()
        {
            foreach (var p in Process.GetProcessesByName("RobloxPlayerBeta"))
            {
                try { if (!p.HasExited) return p; } catch { }
            }
            return null;
        }

        private static bool IsRobloxUpdaterRunning()
        {
            foreach (string name in RobloxUpdaterProcessNames)
            {
                if (Process.GetProcessesByName(name).Length > 0) return true;
            }
            return false;
        }

        private void SetLaunchStatus(string text)
        {
            LblLaunching.Text = text;
            if (protocolLaunchText != null) protocolLaunchText.Text = text;
        }

        // Robloxが実際に起動するまで待つ。更新で新しいバージョンフォルダが作られた場合は、そちらにFastFlags/Modsを適用する。
        private async Task<(Process? beta, string versionDir)> WaitForRobloxStartAsync(string initialVersionDir, HashSet<string> knownDirs, bool bypass)
        {
            bool isJa = GetEffectiveLanguage() == "ja";
            string currentDir = initialVersionDir;
            bool showedUpdating = false;
            bool appliedToNewVersion = false;
            var sw = Stopwatch.StartNew();

            while (sw.Elapsed < TimeSpan.FromMinutes(10))
            {
                var beta = FindRunningRobloxBeta();

                // 更新で作られた新しいバージョンフォルダ (ダウンロード中はまだ exe が無い)
                bool newDirExists = GetAllRobloxVersionDirs().Any(d => !knownDirs.Contains(d));
                string? latestExe = GetLatestRobloxExe();
                string? latestDir = latestExe != null ? Path.GetDirectoryName(latestExe) : null;

                if (latestDir != null && !knownDirs.Contains(latestDir) && !appliedToNewVersion)
                {
                    appliedToNewVersion = true;
                    currentDir = latestDir;
                    AddLog($"Robloxの更新を検出しました。新しいバージョンに設定を適用します: {Path.GetFileName(latestDir)}");
                    if (!bypass)
                    {
                        try
                        {
                            ApplyFastFlags(currentDir);
                            if (config.EnableMods) await ApplyAllModsAsync(currentDir);
                        }
                        catch (Exception ex)
                        {
                            AddLog($"更新後の設定適用エラー: {ex.Message}");
                        }
                    }
                }

                if (beta != null)
                {
                    if (showedUpdating) AddLog("Robloxの更新が完了し、起動しました。");
                    return (beta, currentDir);
                }

                bool updaterRunning = IsRobloxUpdaterRunning();
                bool looksLikeUpdate = newDirExists || (updaterRunning && sw.Elapsed > TimeSpan.FromSeconds(4));
                if (looksLikeUpdate && !showedUpdating)
                {
                    showedUpdating = true;
                    AddLog("Robloxのアップデート中です。完了するまで待機します...");
                }
                if (showedUpdating)
                {
                    SetLaunchStatus(isJa ? "Robloxをアップデート中..." : "Updating Roblox...");
                }

                // 何も動いておらず更新も無いまま猶予を過ぎたら終了 (キャンセルされた場合など)
                if (!updaterRunning && !newDirExists && sw.Elapsed > TimeSpan.FromSeconds(20))
                {
                    AddLog("Robloxの起動を確認できませんでした (キャンセルされた可能性があります)。");
                    return (null, currentDir);
                }

                await Task.Delay(500);
            }

            AddLog("Robloxの起動待機がタイムアウトしました。");
            return (null, currentDir);
        }

        private async Task EnforceCursorProtectionAsync(string versionDir, Process? robloxProcess)
        {
            int[] checkDelaysMs = { 2000, 4500, 7000 };
            foreach (int delay in checkDelaysMs)
            {
                await Task.Delay(delay);
                if (robloxProcess != null && robloxProcess.HasExited) break;
                try { await ApplyCursorModAsync(versionDir, silent: true); } catch { }
            }
            AddLog("カーソル上書き保護チェックが完了しました。");
        }

        private async Task ApplyCursorModAsync(string versionDir, bool silent = false)
        {
            if (config.CursorMode == "Default") return;

            string[] cursorFolders = {
                Path.Combine(versionDir, @"content\textures\Cursors\KeyboardMouse"),
                Path.Combine(versionDir, @"content\textures\Cursors")
            };

            string[] targetCursorFiles = {
                "ArrowCursor.png",
                "ArrowFarCursor.png",
                "IBeamCursor.png"
            };

            if (config.CursorMode == "System")
            {
                string sysArrowPng = Path.Combine(assetsDir, "system_arrow.png");
                string sysIbeamPng = Path.Combine(assetsDir, "system_ibeam.png");
                ExportCurrentWindowsCursorToPng("Arrow", IDC_ARROW, sysArrowPng);
                ExportCurrentWindowsCursorToPng("IBeam", IDC_IBEAM, sysIbeamPng);

                foreach (string folder in cursorFolders)
                {
                    if (!Directory.Exists(folder)) continue;
                    CopyFileSafe(sysArrowPng, Path.Combine(folder, "ArrowCursor.png"));
                    CopyFileSafe(sysArrowPng, Path.Combine(folder, "ArrowFarCursor.png"));
                    if (File.Exists(sysIbeamPng))
                    {
                        CopyFileSafe(sysIbeamPng, Path.Combine(folder, "IBeamCursor.png"));
                    }
                }
                if (!silent) AddLog("Windowsで現在設定中のカーソルを適用しました。");
                return;
            }

            if (config.CursorMode == "Classic")
            {
                string classicArrow = Path.Combine(assetsDir, "classic_arrow.png");
                string classicFar = Path.Combine(assetsDir, "classic_arrow_far.png");

                if (File.Exists(classicArrow))
                {
                    foreach (string folder in cursorFolders)
                    {
                        if (!Directory.Exists(folder)) continue;
                        CopyFileSafe(classicArrow, Path.Combine(folder, "ArrowCursor.png"));
                        CopyFileSafe(File.Exists(classicFar) ? classicFar : classicArrow, Path.Combine(folder, "ArrowFarCursor.png"));
                    }
                    if (!silent) AddLog("内蔵のクラシックカーソルを適用しました。");
                }
                else
                {
                    try
                    {
                        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                        byte[] d1 = await client.GetByteArrayAsync("https://raw.githubusercontent.com/bloxstraplabs/bloxstrap/main/Bloxstrap/Resources/Mods/Cursor/From2013/ArrowCursor.png");
                        await File.WriteAllBytesAsync(classicArrow, d1);
                        byte[] d2 = await client.GetByteArrayAsync("https://raw.githubusercontent.com/bloxstraplabs/bloxstrap/main/Bloxstrap/Resources/Mods/Cursor/From2013/ArrowFarCursor.png");
                        await File.WriteAllBytesAsync(classicFar, d2);

                        foreach (string folder in cursorFolders)
                        {
                            if (!Directory.Exists(folder)) continue;
                            CopyFileSafe(classicArrow, Path.Combine(folder, "ArrowCursor.png"));
                            CopyFileSafe(classicFar, Path.Combine(folder, "ArrowFarCursor.png"));
                        }
                        if (!silent) AddLog("昔のクラシックカーソルを適用しました。");
                    }
                    catch (Exception ex)
                    {
                        if (!silent) AddLog($"クラシックカーソル取得エラー: {ex.Message}");
                    }
                }
                return;
            }

            if (config.CursorMode == "Custom")
            {
                if (string.IsNullOrEmpty(config.CustomCursorPath) || !File.Exists(config.CustomCursorPath)) return;

                foreach (string folder in cursorFolders)
                {
                    if (!Directory.Exists(folder)) continue;
                    foreach (string fileName in targetCursorFiles)
                    {
                        CopyFileSafe(config.CustomCursorPath, Path.Combine(folder, fileName));
                    }
                }
                if (!silent) AddLog($"カスタムカーソルを適用しました (保護: {(config.ProtectCursor ? "ON" : "OFF")})");
            }
        }

        private void ExportCurrentWindowsCursorToPng(string regValueName, int fallbackCursorId, string outputPngPath)
        {
            try
            {
                string? customSysCursorFile = null;
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors"))
                {
                    if (key != null)
                    {
                        string? rawPath = key.GetValue(regValueName) as string;
                        if (!string.IsNullOrWhiteSpace(rawPath))
                        {
                            string expanded = Environment.ExpandEnvironmentVariables(rawPath);
                            if (File.Exists(expanded))
                            {
                                customSysCursorFile = expanded;
                            }
                        }
                    }
                }

                IntPtr hCursor = LoadCursor(IntPtr.Zero, (IntPtr)fallbackCursorId);
                if (hCursor != IntPtr.Zero)
                {
                    IntPtr hCopy = CopyIcon(hCursor);
                    if (hCopy != IntPtr.Zero)
                    {
                        try
                        {
                            using var icon = System.Drawing.Icon.FromHandle(hCopy);
                            using var bmp = icon.ToBitmap();
                            bmp.Save(outputPngPath, ImageFormat.Png);
                            return;
                        }
                        finally
                        {
                            DestroyIcon(hCopy);
                        }
                    }
                }

                if (!string.IsNullOrEmpty(customSysCursorFile))
                {
                    using var icon = new System.Drawing.Icon(customSysCursorFile);
                    using var bmp = icon.ToBitmap();
                    bmp.Save(outputPngPath, ImageFormat.Png);
                }
            }
            catch { }
        }

        private void CopyFileSafe(string srcPath, string destPath)
        {
            if (!File.Exists(srcPath)) return;
            try
            {
                if (File.Exists(destPath))
                {
                    File.SetAttributes(destPath, FileAttributes.Normal);
                }
                File.Copy(srcPath, destPath, true);
            }
            catch { }
        }

        private void ApplyFastFlags(string versionDir)
        {
            string settingsDir = Path.Combine(versionDir, "ClientSettings");
            Directory.CreateDirectory(settingsDir);
            string jsonPath = Path.Combine(settingsDir, "ClientAppSettings.json");

            if (config.CleanClientSettings && File.Exists(jsonPath))
            {
                try { File.Delete(jsonPath); } catch { }
                AddLog("既存の ClientAppSettings.json をクリーンアップしました。");
            }

            var finalFlags = new Dictionary<string, object>();

            if (!string.IsNullOrWhiteSpace(config.CustomFlagsJson))
            {
                try
                {
                    var customDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(config.CustomFlagsJson);
                    if (customDict != null)
                    {
                        foreach (var kvp in customDict)
                        {
                            if (AllowedFastFlags.Contains(kvp.Key))
                            {
                                finalFlags[kvp.Key] = kvp.Value;
                            }
                            else
                            {
                                AddLog($"[FastFlags] ホワイトリスト外のキーを除外: {kvp.Key}");
                            }
                        }
                    }
                }
                catch { }
            }

            if (config.UnlockFPS)
            {
                finalFlags["DFIntTaskSchedulerTargetFps"] = config.TargetFps > 0 ? config.TargetFps : 9999;
            }
            if (config.DisablePostFx)
            {
                finalFlags["FFlagDisablePostFx"] = true;
            }
            if (config.LowRenderQuality)
            {
                finalFlags["DFIntDebugFRMQualityLevelOverride"] = 1;
            }

            switch (config.GraphicsApi)
            {
                case "D3D11":
                    finalFlags["FFlagDebugGraphicsPreferD3D11"] = true;
                    break;
                case "D3D10":
                    finalFlags["FFlagDebugGraphicsPreferD3D11FL10"] = true;
                    break;
                case "Vulkan":
                    finalFlags["FFlagDebugGraphicsPreferVulkan"] = true;
                    break;
                case "OpenGL":
                    finalFlags["FFlagDebugGraphicsPreferOpenGL"] = true;
                    break;
            }

            if (finalFlags.Count > 0)
            {
                string outputJson = JsonSerializer.Serialize(finalFlags, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(jsonPath, outputJson);
                AddLog($"FastFlags を {finalFlags.Count} 件適用しました。");
            }
        }

        private async Task ApplyAllModsAsync(string versionDir)
        {
            if (Directory.Exists(modsDir))
            {
                CopyDirectory(modsDir, versionDir);
                AddLog("汎用 Mods フォルダのファイルをコピーしました。");
            }

            await ApplyCursorModAsync(versionDir);

            if (!string.IsNullOrEmpty(config.DeathSoundPath) && File.Exists(config.DeathSoundPath))
            {
                string targetSoundsDir = Path.Combine(versionDir, @"content\sounds");
                Directory.CreateDirectory(targetSoundsDir);
                string destOuch = Path.Combine(targetSoundsDir, "ouch.ogg");
                try
                {
                    if (File.Exists(destOuch)) File.SetAttributes(destOuch, FileAttributes.Normal);
                    File.Copy(config.DeathSoundPath, destOuch, true);
                    AddLog($"死亡音を適用しました: {config.DeathSoundDisplayName}");
                }
                catch (Exception ex)
                {
                    AddLog($"死亡音適用エラー: {ex.Message}");
                }
            }

            if (!string.IsNullOrEmpty(config.SkyboxFolderPath) && Directory.Exists(config.SkyboxFolderPath))
            {
                string targetSkyDir = Path.Combine(versionDir, @"PlatformContent\pc\textures\sky");
                Directory.CreateDirectory(targetSkyDir);

                string[] skyFaces = { "bk", "dn", "ft", "lf", "rt", "up" };
                int appliedCount = 0;
                foreach (string file in Directory.GetFiles(config.SkyboxFolderPath))
                {
                    string nameWithoutExt = Path.GetFileNameWithoutExtension(file).ToLower();
                    foreach (string face in skyFaces)
                    {
                        if (nameWithoutExt.EndsWith(face))
                        {
                            try
                            {
                                File.Copy(file, Path.Combine(targetSkyDir, $"sky512_{face}.tex"), true);
                                File.Copy(file, Path.Combine(targetSkyDir, $"sky512_{face}{Path.GetExtension(file)}"), true);
                                appliedCount++;
                            }
                            catch (Exception ex)
                            {
                                AddLog($"スカイボックス適用エラー ({face}): {ex.Message}");
                            }
                            break;
                        }
                    }
                }
                AddLog($"カスタムスカイボックスを適用しました ({appliedCount} 面)。");
            }

            if (!string.IsNullOrEmpty(config.CustomFontPath) && File.Exists(config.CustomFontPath))
            {
                string targetFontsDir = Path.Combine(versionDir, @"content\fonts");
                if (Directory.Exists(targetFontsDir))
                {
                    string[] targetFonts = {
                        "SourceSansPro-Regular.ttf",
                        "SourceSansPro-Bold.ttf",
                        "SourceSansPro-Light.ttf",
                        "SourceSansPro-Semibold.ttf",
                        "GothamSSm-Medium.otf",
                        "GothamSSm-Bold.otf",
                        "GothamSSm-Book.otf",
                        "BuilderSans-Regular.otf",
                        "BuilderSans-Medium.otf",
                        "BuilderSans-Bold.otf"
                    };

                    foreach (string fontName in targetFonts)
                    {
                        string destFont = Path.Combine(targetFontsDir, fontName);
                        if (File.Exists(destFont))
                        {
                            try
                            {
                                File.SetAttributes(destFont, FileAttributes.Normal);
                                File.Copy(config.CustomFontPath, destFont, true);
                            }
                            catch (Exception ex)
                            {
                                AddLog($"フォント適用エラー ({fontName}): {ex.Message}");
                            }
                        }
                    }
                    AddLog($"カスタムフォントを適用しました: {Path.GetFileName(config.CustomFontPath)}");
                }
            }
        }

        private void CopyDirectory(string sourceDir, string destinationDir)
        {
            foreach (var dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(destinationDir, Path.GetRelativePath(sourceDir, dirPath)));
            }
            foreach (var newPath in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string destFile = Path.Combine(destinationDir, Path.GetRelativePath(sourceDir, newPath));
                try
                {
                    if (File.Exists(destFile))
                    {
                        File.SetAttributes(destFile, FileAttributes.Normal);
                    }
                    File.Copy(newPath, destFile, true);
                    if (config.ProtectCursor && destFile.Contains("Cursors", StringComparison.OrdinalIgnoreCase))
                    {
                        File.SetAttributes(destFile, FileAttributes.ReadOnly);
                    }
                }
                catch (Exception ex)
                {
                    AddLog($"Modコピー失敗: {Path.GetFileName(newPath)} ({ex.Message})");
                }
            }
        }

        private void SelectCursor_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "カスタムカーソル画像 (.png) を選択",
                Filter = "PNG画像 (*.png)|*.png|すべてのファイル (*.*)|*.*"
            };
            if (dialog.ShowDialog() == true)
            {
                config.CustomCursorPath = dialog.FileName;
                CursorPathText.Text = Path.GetFileName(dialog.FileName);
                AddLog($"カスタムカーソルを選択: {Path.GetFileName(dialog.FileName)}");
                SaveSettings();
            }
        }

        private void ClearCursor_Click(object sender, RoutedEventArgs e)
        {
            config.CustomCursorPath = "";
            CursorPathText.Text = GetEffectiveLanguage() == "ja" ? "未設定" : "Not set";
            AddLog("カスタムカーソル設定を解除しました。");
            SaveSettings();
        }

        private void SetClassicOof_Click(object sender, RoutedEventArgs e)
        {
            string localOofPath = Path.Combine(assetsDir, "classic_ouch.ogg");

            if (File.Exists(localOofPath))
            {
                config.DeathSoundPath = localOofPath;
                config.DeathSoundDisplayName = "クラシック Oof 音 (内蔵アセット)";
                ApplyLanguage();
                AddLog("内蔵のクラシック Oof 音を設定しました（オフライン動作対応）。");
                SaveSettings();
            }
            else
            {
                DeathSoundPathText.Text = "エラー: Assets/classic_ouch.ogg が見つかりません。任意の音声ファイルを選択してください。";
                AddLog("エラー: Assets/classic_ouch.ogg が見つかりませんでした。");
            }
        }

        private async void SelectDeathSound_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "死亡時の効果音を選択 (.ogg 以外は自動変換されます)",
                Filter = "音声ファイル (*.ogg;*.mp3;*.wav;*.m4a;*.aac;*.flac;*.wma)|*.ogg;*.mp3;*.wav;*.m4a;*.aac;*.flac;*.wma|すべてのファイル (*.*)|*.*"
            };

            if (dialog.ShowDialog() != true) return;

            string selectedFile = dialog.FileName;
            string ext = Path.GetExtension(selectedFile).ToLower();

            if (ext == ".ogg")
            {
                config.DeathSoundPath = selectedFile;
                config.DeathSoundDisplayName = Path.GetFileName(selectedFile);
                ApplyLanguage();
                AddLog($"死亡音を設定: {config.DeathSoundDisplayName}");
                SaveSettings();
                return;
            }

            BtnSelectDeathSound.IsEnabled = false;
            try
            {
                string? ffmpegExe = await EnsureFfmpegAsync();
                if (ffmpegExe == null)
                {
                    DeathSoundPathText.Text = "変換エラー: 音声変換ツール(FFmpeg)の取得に失敗しました。.ogg ファイルを選択してください。";
                    AddLog("FFmpegの取得に失敗したため変換を中止しました。");
                    return;
                }

                DeathSoundPathText.Text = $"「{Path.GetFileName(selectedFile)}」を Roblox用 .ogg へ変換中...";
                AddLog($"音声変換を開始: {Path.GetFileName(selectedFile)} -> custom_ouch.ogg");
                string convertedOggPath = Path.Combine(assetsDir, "custom_ouch.ogg");
                try { if (File.Exists(convertedOggPath)) File.Delete(convertedOggPath); } catch { }

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = ffmpegExe,
                    Arguments = $"-y -i \"{selectedFile}\" -c:a libvorbis -q:a 4 \"{convertedOggPath}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };

                int exitCode = -1;
                using (Process? proc = Process.Start(psi))
                {
                    if (proc != null)
                    {
                        await proc.WaitForExitAsync();
                        exitCode = proc.ExitCode;
                    }
                }

                if (exitCode == 0 && File.Exists(convertedOggPath) && new FileInfo(convertedOggPath).Length > 0)
                {
                    config.DeathSoundPath = convertedOggPath;
                    config.DeathSoundDisplayName = $"{Path.GetFileName(selectedFile)} (自動OGG変換済み)";
                    ApplyLanguage();
                    AddLog($"音声変換が完了しました: {config.DeathSoundDisplayName}");
                    SaveSettings();
                }
                else
                {
                    DeathSoundPathText.Text = "変換エラー: 音声ファイルの変換に失敗しました。別のファイルをお試しください。";
                    AddLog("音声変換エラー: 出力ファイルが生成されませんでした。");
                }
            }
            catch (Exception ex)
            {
                DeathSoundPathText.Text = $"変換エラー: {ex.Message}";
                AddLog($"音声変換例外: {ex.Message}");
            }
            finally
            {
                BtnSelectDeathSound.IsEnabled = true;
            }
        }

        private async Task<string?> EnsureFfmpegAsync()
        {
            string ffmpegPath = Path.Combine(toolsDir, "ffmpeg.exe");
            if (File.Exists(ffmpegPath)) return ffmpegPath;

            DeathSoundPathText.Text = "初回セットアップ: 音声自動変換エンジン (FFmpeg) をダウンロード中...";
            AddLog("初回セットアップ: FFmpeg をダウンロードしています...");

            try
            {
                string zipPath = Path.Combine(toolsDir, "ffmpeg.zip");
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromMinutes(3);

                byte[] zipData = await client.GetByteArrayAsync("https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip");
                await File.WriteAllBytesAsync(zipPath, zipData);

                using (ZipArchive archive = ZipFile.OpenRead(zipPath))
                {
                    var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
                    if (entry != null)
                    {
                        entry.ExtractToFile(ffmpegPath, true);
                    }
                }

                if (File.Exists(zipPath)) File.Delete(zipPath);
                AddLog("FFmpeg のセットアップが完了しました。");
                return File.Exists(ffmpegPath) ? ffmpegPath : null;
            }
            catch (Exception ex)
            {
                AddLog($"FFmpeg ダウンロードエラー: {ex.Message}");
                try { string z = Path.Combine(toolsDir, "ffmpeg.zip"); if (File.Exists(z)) File.Delete(z); } catch { }
                return null;
            }
        }

        private void ClearDeathSound_Click(object sender, RoutedEventArgs e)
        {
            config.DeathSoundPath = "";
            config.DeathSoundDisplayName = "";
            ApplyLanguage();
            AddLog("死亡音設定を解除しました。");
            SaveSettings();
        }

        private void SelectSkybox_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "スカイボックス画像 (bk, dn, ft, lf, rt, up) が入ったフォルダを選択"
            };
            if (dialog.ShowDialog() == true)
            {
                config.SkyboxFolderPath = dialog.FolderName;
                SkyboxPathText.Text = Path.GetFileName(dialog.FolderName);
                AddLog($"スカイボックスフォルダを選択: {dialog.FolderName}");
                SaveSettings();
            }
        }

        private void ClearSkybox_Click(object sender, RoutedEventArgs e)
        {
            config.SkyboxFolderPath = "";
            SkyboxPathText.Text = GetEffectiveLanguage() == "ja" ? "未設定" : "Not set";
            AddLog("スカイボックス設定を解除しました。");
            SaveSettings();
        }

        private void SelectFont_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "カスタムフォントを選択",
                Filter = "フォントファイル (*.ttf;*.otf)|*.ttf;*.otf|すべてのファイル (*.*)|*.*"
            };
            if (dialog.ShowDialog() == true)
            {
                config.CustomFontPath = dialog.FileName;
                FontPathText.Text = Path.GetFileName(dialog.FileName);
                AddLog($"カスタムフォントを選択: {Path.GetFileName(dialog.FileName)}");
                SaveSettings();
            }
        }

        private void ClearFont_Click(object sender, RoutedEventArgs e)
        {
            config.CustomFontPath = "";
            FontPathText.Text = GetEffectiveLanguage() == "ja" ? "未設定" : "Not set";
            AddLog("カスタムフォント設定を解除しました。");
            SaveSettings();
        }

        private void ClearLogs_Click(object sender, RoutedEventArgs e) => TxtLogs.Clear();

        private void OpenRobloxLogsFolder_Click(object sender, RoutedEventArgs e)
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string robloxLogsDir = Path.Combine(localAppData, @"Roblox\logs");
            if (Directory.Exists(robloxLogsDir))
            {
                Process.Start("explorer.exe", robloxLogsDir);
            }
            else
            {
                AddLog("Robloxのログフォルダが見つかりませんでした。");
            }
        }

        private void OpenModsFolder_Click(object sender, RoutedEventArgs e) => Process.Start("explorer.exe", modsDir);

        private void OpenRobloxFolder_Click(object sender, RoutedEventArgs e)
        {
            string? exe = GetLatestRobloxExe();
            if (exe != null)
            {
                Process.Start("explorer.exe", Path.GetDirectoryName(exe)!);
            }
            else
            {
                AddLog("Robloxのインストールフォルダが見つかりませんでした。");
            }
        }

        private void ClearReadOnlyAttributes(string dir)
        {
            foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var attrs = File.GetAttributes(file);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                    }
                }
                catch { }
            }
        }

        private void RepairRoblox_Click(object sender, RoutedEventArgs e)
        {
            string? exe = GetLatestRobloxExe();
            if (exe == null)
            {
                AddLog("Robloxのインストールフォルダが見つかりませんでした。");
                return;
            }

            string versionDir = Path.GetDirectoryName(exe)!;
            try
            {
                ClearReadOnlyAttributes(versionDir);
                string settingsDir = Path.Combine(versionDir, "ClientSettings");
                if (Directory.Exists(settingsDir))
                {
                    Directory.Delete(settingsDir, true);
                }
                AddLog("ClientSettings 削除と読み取り専用属性の解除を完了しました。");
                MessageBox.Show("ClientSettings の削除とファイル属性の初期化が完了しました。", "完了", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AddLog($"削除エラー: {ex.Message}");
            }
        }

        // ===== 以前のバージョンからのデータ引っ越し =====

        private static string NormalizeDir(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

        private string? FindPreviousInstall()
        {
            try
            {
                string baseDir = NormalizeDir(AppDomain.CurrentDomain.BaseDirectory);
                string? parent = Path.GetDirectoryName(baseDir);
                if (parent == null) return null;

                return Directory.GetDirectories(parent)
                    .Where(d => !string.Equals(NormalizeDir(d), baseDir, StringComparison.OrdinalIgnoreCase)
                                && File.Exists(Path.Combine(d, "config.json")))
                    .OrderByDescending(d => File.GetLastWriteTime(Path.Combine(d, "config.json")))
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        private static string? ResolvePreviousDir(string dir)
        {
            if (File.Exists(Path.Combine(dir, "config.json"))) return dir;
            try
            {
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    if (File.Exists(Path.Combine(sub, "config.json"))) return sub;
                }
            }
            catch { }
            return null;
        }

        // 以前のフォルダ内を指していたパスは、新しいフォルダ内の同じ場所に付け替える
        private static string RemapPath(string path, string oldDir, string newDir)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string prefix = oldDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(newDir, path.Substring(prefix.Length));
            }
            return path;
        }

        // 既にあるファイルは上書きしない (同梱アセットを壊さないため)
        private int CopyMissingFiles(string srcDir, string destDir)
        {
            if (!Directory.Exists(srcDir)) return 0;
            int copied = 0;
            foreach (string file in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                string dest = Path.Combine(destDir, Path.GetRelativePath(srcDir, file));
                if (File.Exists(dest)) continue;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(file, dest);
                    copied++;
                }
                catch (Exception ex)
                {
                    AddLog($"引き継ぎコピー失敗: {Path.GetFileName(file)} ({ex.Message})");
                }
            }
            return copied;
        }

        private void PromptImportOnFirstRun()
        {
            string? found = FindPreviousInstall();
            if (found == null) return;

            bool isJa = GetEffectiveLanguage() == "ja";
            var answer = MessageBox.Show(
                isJa ? $"以前のバージョンが見つかりました。\n{found}\n\n設定とデータを引き継ぎますか?"
                     : $"A previous version was found.\n{found}\n\nImport its settings and data?",
                "AZQLAUNCHER", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes) ImportFromFolder(found);
        }

        private void ImportData_Click(object sender, RoutedEventArgs e)
        {
            bool isJa = GetEffectiveLanguage() == "ja";
            var dialog = new OpenFolderDialog
            {
                Title = isJa ? "以前のバージョンのフォルダ (config.json があるフォルダ) を選択"
                             : "Select the previous version's folder (the one with config.json)"
            };
            string? guess = FindPreviousInstall();
            if (guess != null) dialog.InitialDirectory = guess;
            if (dialog.ShowDialog() == true) ImportFromFolder(dialog.FolderName);
        }

        private void ImportFromFolder(string selected)
        {
            bool isJa = GetEffectiveLanguage() == "ja";
            string baseDir = NormalizeDir(AppDomain.CurrentDomain.BaseDirectory);

            string? srcDir = ResolvePreviousDir(selected);
            if (srcDir == null)
            {
                MessageBox.Show(
                    isJa ? "選んだフォルダに config.json が見つかりません。以前のバージョンのフォルダを選んでください。"
                         : "config.json was not found in that folder. Please select the previous version's folder.",
                    "AZQLAUNCHER", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.Equals(NormalizeDir(srcDir), baseDir, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    isJa ? "現在のランチャーと同じフォルダです。以前のバージョンのフォルダを選んでください。"
                         : "That is the current launcher's own folder. Please select the previous version's folder.",
                    "AZQLAUNCHER", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                string json = File.ReadAllText(Path.Combine(srcDir, "config.json"));
                var loaded = JsonSerializer.Deserialize<LauncherConfig>(json);
                if (loaded == null) throw new InvalidDataException("config.json is empty.");

                int copied = 0;
                copied += CopyMissingFiles(Path.Combine(srcDir, "Mods"), modsDir);
                copied += CopyMissingFiles(Path.Combine(srcDir, "Assets"), assetsDir);
                copied += CopyMissingFiles(Path.Combine(srcDir, "Tools"), toolsDir);

                loaded.DeathSoundPath = RemapPath(loaded.DeathSoundPath, srcDir, baseDir);
                loaded.CustomCursorPath = RemapPath(loaded.CustomCursorPath, srcDir, baseDir);
                loaded.SkyboxFolderPath = RemapPath(loaded.SkyboxFolderPath, srcDir, baseDir);
                loaded.CustomFontPath = RemapPath(loaded.CustomFontPath, srcDir, baseDir);
                loaded.IsFirstLaunch = false;

                if (File.Exists(configPath)) File.Copy(configPath, configPath + ".bak", true);
                File.WriteAllText(configPath, JsonSerializer.Serialize(loaded, new JsonSerializerOptions { WriteIndented = true }));

                // 読み込み中に画面の変更イベントで設定が上書きされないよう、一時的に保存を止める
                isLoaded = false;
                LoadSettings();
                ApplyLanguage();
                isLoaded = true;

                // ブラウザ連携の登録先を、新しい実行ファイルに付け替える
                if (config.AutoRegisterProtocol) RegisterProtocol(silent: true);
                else UpdateProtocolStatusText(false);
                UpdateDashboard();

                AddLog($"以前のバージョンから設定を引き継ぎました: {srcDir} (ファイル {copied} 件をコピー)");
                MessageBox.Show(
                    isJa ? $"設定とデータを引き継ぎました。\nコピーしたファイル: {copied} 件"
                         : $"Settings and data were imported.\nFiles copied: {copied}",
                    "AZQLAUNCHER", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                isLoaded = true;
                AddLog($"引き継ぎエラー: {ex.Message}");
                MessageBox.Show(
                    isJa ? $"引き継ぎに失敗しました。\n{ex.Message}" : $"Import failed.\n{ex.Message}",
                    "AZQLAUNCHER", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowIntro_Click(object sender, RoutedEventArgs e) => WelcomeOverlay.Visibility = Visibility.Visible;
        private void CloseWelcome_Click(object sender, RoutedEventArgs e) => WelcomeOverlay.Visibility = Visibility.Collapsed;

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
            for (DependencyObject? d = e.OriginalSource as DependencyObject; d != null; d = (d is Visual || d is System.Windows.Media.Media3D.Visual3D) ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            {
                if (d is System.Windows.Controls.Primitives.TextBoxBase || d is System.Windows.Controls.Primitives.ScrollBar || d is System.Windows.Controls.Primitives.ButtonBase || d is ComboBoxItem || d is System.Windows.Controls.Primitives.Popup) return;
            }
            try { this.DragMove(); } catch (InvalidOperationException) { }
        }
        private void Minimize_Click(object sender, RoutedEventArgs e) => Hide(); // トレイに格納
        private void Close_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
    }
}