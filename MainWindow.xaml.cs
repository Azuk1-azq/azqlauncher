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
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AZQLAUNCHER
{
    public class RecentGame
    {
        public string PlaceId { get; set; } = "";
        public string Name { get; set; } = "";
        public string LinkCode { get; set; } = "";
        public string JobId { get; set; } = "";
    }

    // FastFlag管理画面の1行分
    public class FlagRow
    {
        public string Name { get; set; } = "";
        public string Raw { get; set; } = "true";   // JSONとしての値 (true / 240 / "text")
        public bool Enabled { get; set; } = true;
    }

    public class LauncherConfig
    {
        public bool IsFirstLaunch { get; set; } = true;
        public string Language { get; set; } = "System";

        public bool UnlockFPS { get; set; } = false;
        public int TargetFps { get; set; } = 9999;
        public bool DisablePostFx { get; set; } = false;
        public bool LowRenderQuality { get; set; } = false;
        public string GraphicsApi { get; set; } = "Auto";

        // 大人数サーバー向けの軽量化 (Robloxの許可リストにあるフラグだけを使う)
        public bool LowTextureQuality { get; set; } = false;
        public bool DisableMsaa { get; set; } = false;
        public bool ReduceGrass { get; set; } = false;
        public bool AggressiveLod { get; set; } = false;
        public bool PauseVoxelizer { get; set; } = false;
        public bool HighPriority { get; set; } = false;

        // フラグ管理画面でオフにしたフラグ (名前 → JSONの値)。適用はされないが値は保持する
        public Dictionary<string, string> DisabledFlags { get; set; } = new Dictionary<string, string>();
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

        public string Theme { get; set; } = "System";   // System / Dark / Light
        public bool MinimizeToTray { get; set; } = false;
        public double WindowWidth { get; set; } = 0;
        public double WindowHeight { get; set; } = 0;

        public List<RecentGame> RecentGames { get; set; } = new List<RecentGame>();
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

        private readonly string logsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
        private readonly string backupsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups");
        private readonly object logLock = new object();
        private readonly Dictionary<string, HashSet<string>> createdFileCache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private bool isLaunching = false;
        private CancellationTokenSource? launchCts;
        private bool shownTrayHint = false;

        private static readonly HttpClient Http = CreateHttpClient();
        private static readonly JsonSerializerOptions JsonReadOpts = new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        private static readonly JsonSerializerOptions JsonWriteOpts = new JsonSerializerOptions { WriteIndented = true };
        private static readonly Regex GamePathRegex = new Regex(@"/games/(\d+)", RegexOptions.IgnoreCase);

        private System.Windows.Threading.DispatcherTimer? statusTimer;
        private System.Windows.Threading.DispatcherTimer? autoSaveTimer;

        // テーマ配色 (キーは App.xaml のリソース名。"Color" で終わるものは Color、それ以外は SolidColorBrush として差し替える)
        private static readonly Dictionary<string, string> DarkTheme = new Dictionary<string, string>
        {
            ["WinBg1Color"] = "#FF121626", ["WinBg2Color"] = "#FF0C0E16", ["LogoHoleBrush"] = "#FF0C0E16",
            ["ContentBgBrush"] = "#CC101320", ["SurfaceBrush"] = "#FF171B2A", ["Surface2Brush"] = "#FF1F2437",
            ["SurfaceRaisedBrush"] = "#FF222842", ["InputBrush"] = "#FF0E1019", ["LineBrush"] = "#FF262C42",
            ["LineStrongBrush"] = "#FF2E3552", ["TextBrush"] = "#FFFFFFFF", ["TextSoftBrush"] = "#FFB4BBD6",
            ["TextBodyBrush"] = "#FFD5DAEE", ["TextMutedBrush"] = "#FF8E97B5", ["AccentTextBrush"] = "#FF8FA6FF",
            ["OkBrush"] = "#FF00B894", ["OkBrightBrush"] = "#FF00E676", ["InfoBrush"] = "#FF74B9FF",
            ["PurpleBrush"] = "#FFA29BFE", ["ErrBrush"] = "#FFFF7675", ["WarnBrush"] = "#FFFDCB6E",
            ["OverlayBrush"] = "#E60A0C13", ["HoverOverlayBrush"] = "#FFFFFFFF", ["HoverBorderBrush"] = "#FF48527A",
            ["MenuHoverBrush"] = "#FF1A1F33", ["MenuActiveBrush"] = "#FF222A4A", ["ItemHoverBrush"] = "#FF232944",
            ["ItemSelectedBrush"] = "#FF2A3260", ["TrackBgBrush"] = "#FF2A3046", ["TrackBorderBrush"] = "#FF38405E",
            ["ThumbOffBrush"] = "#FFB4BBD6", ["RadioRingBrush"] = "#FF4A5478", ["ScrollThumbBrush"] = "#FF353D5C",
            ["ScrollThumbHoverBrush"] = "#FF5B6694", ["DangerBgBrush"] = "#FF3A1F2B", ["DangerBorderBrush"] = "#FF7A2E45",
            ["DangerFgBrush"] = "#FFFF9DB0"
        };

        private static readonly Dictionary<string, string> LightTheme = new Dictionary<string, string>
        {
            ["WinBg1Color"] = "#FFF5F7FC", ["WinBg2Color"] = "#FFE6EAF6", ["LogoHoleBrush"] = "#FFE6EAF6",
            ["ContentBgBrush"] = "#CCFFFFFF", ["SurfaceBrush"] = "#FFFFFFFF", ["Surface2Brush"] = "#FFF0F3FB",
            ["SurfaceRaisedBrush"] = "#FFE7EBF7", ["InputBrush"] = "#FFF6F8FD", ["LineBrush"] = "#FFDCE1F0",
            ["LineStrongBrush"] = "#FFC5CCE2", ["TextBrush"] = "#FF182033", ["TextSoftBrush"] = "#FF4B5575",
            ["TextBodyBrush"] = "#FF343C58", ["TextMutedBrush"] = "#FF69728F", ["AccentTextBrush"] = "#FF3A55D8",
            ["OkBrush"] = "#FF00896F", ["OkBrightBrush"] = "#FF0A8F4E", ["InfoBrush"] = "#FF2A6FC4",
            ["PurpleBrush"] = "#FF5B4CD6", ["ErrBrush"] = "#FFCF2F30", ["WarnBrush"] = "#FFA66A00",
            ["OverlayBrush"] = "#E6EEF1FA", ["HoverOverlayBrush"] = "#FF000000", ["HoverBorderBrush"] = "#FFA7B1D3",
            ["MenuHoverBrush"] = "#FFE8ECF8", ["MenuActiveBrush"] = "#FFDCE3FB", ["ItemHoverBrush"] = "#FFEBEFFA",
            ["ItemSelectedBrush"] = "#FFDCE3FB", ["TrackBgBrush"] = "#FFCDD4E8", ["TrackBorderBrush"] = "#FFB5BDD6",
            ["ThumbOffBrush"] = "#FFFFFFFF", ["RadioRingBrush"] = "#FF98A2C4", ["ScrollThumbBrush"] = "#FFC0C8E0",
            ["ScrollThumbHoverBrush"] = "#FF9AA5CB", ["DangerBgBrush"] = "#FFFDECEF", ["DangerBorderBrush"] = "#FFE6A1B1",
            ["DangerFgBrush"] = "#FFB3203F"
        };

        private static HttpClient CreateHttpClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("AZQLAUNCHER/0.0.5");
            return c;
        }

        private static readonly HashSet<string> AllowedFastFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DFIntTaskSchedulerTargetFps",
            "DFIntCSGLevelOfDetailSwitchingDistance",
            "DFIntCSGLevelOfDetailSwitchingDistanceL12",
            "DFIntCSGLevelOfDetailSwitchingDistanceL23",
            "DFIntCSGLevelOfDetailSwitchingDistanceL34",
            "FFlagHandleAltEnterFullscreenManually",
            "DFFlagDisableDPIScale",
            "FFlagDebugSkyGray",
            "DFFlagDebugPauseVoxelizer",
            "FIntFRMMaxGrassDistance",
            "FIntFRMMinGrassDistance",
            "FIntGrassMovementReducedMotionFactor",
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

        // Roblox公式の許可リスト (2025-09-29 公開)。これ以外のフラグは現在のRobloxでは無視される。
        private static readonly HashSet<string> OfficialAllowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DFIntCSGLevelOfDetailSwitchingDistance", "DFIntCSGLevelOfDetailSwitchingDistanceL12",
            "DFIntCSGLevelOfDetailSwitchingDistanceL23", "DFIntCSGLevelOfDetailSwitchingDistanceL34",
            "FFlagHandleAltEnterFullscreenManually", "DFFlagTextureQualityOverrideEnabled", "DFIntTextureQualityOverride",
            "FIntDebugForceMSAASamples", "DFFlagDisableDPIScale", "FFlagDebugGraphicsPreferD3D11", "FFlagDebugSkyGray",
            "DFFlagDebugPauseVoxelizer", "DFIntDebugFRMQualityLevelOverride", "FIntFRMMaxGrassDistance",
            "FIntFRMMinGrassDistance", "FFlagDebugGraphicsPreferVulkan", "FFlagDebugGraphicsPreferOpenGL",
            "FIntGrassMovementReducedMotionFactor"
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
            Directory.CreateDirectory(logsDir);
            Directory.CreateDirectory(backupsDir);
            CleanupOldLogs();
            AddLog("--- AZQLAUNCHER beta0.0.5 を起動しました ---");

            LoadSettings();
            RestoreWindowSize();
            ApplyTheme();
            ApplyLanguage();
            isLoaded = true;
            InitTray();
            InitBackgroundServices();

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
            BtnLaunchVanilla.Content = isJa ? "⏩ 標準起動 (Modsなし)" : "⏩ Vanilla Launch";
            BtnLaunchRoblox.ToolTip = isJa ? "FastFlags と Mods を適用して Roblox を起動します (Ctrl+Enter)" : "Launch Roblox with your FastFlags and Mods applied (Ctrl+Enter)";
            BtnLaunchVanilla.ToolTip = isJa ? "FastFlags と Mods を適用せず (元に戻して) 標準の Roblox を起動します" : "Launch stock Roblox: FastFlags and Mods are removed first";
            BtnSidebarLaunch.Content = isJa ? "▶ Robloxを起動" : "▶ Launch Roblox";
            NavDashboard.ToolTip = "Ctrl+1";
            NavFastFlags.ToolTip = "Ctrl+2";
            NavMods.ToolTip = "Ctrl+3";
            NavLogs.ToolTip = "Ctrl+4";
            NavSettings.ToolTip = "Ctrl+5";

            LblFastFlagsTitle.Text = "FastFlags";
            LblFastFlagsSub.Text = isJa ? "ホワイトリスト準拠の安全なクライアント設定" : "Allowlist-compliant client settings";
            BtnResetFastFlags.Content = isJa ? "リセット" : "Reset";
            LblPerfCardTitle.Text = isJa ? "パフォーマンス・フレームレート" : "Performance & Frame Rate";
            ChkUnlockFPS.Content = isJa ? "FPS上限を解放する" : "Unlock FPS Limit";
            LblTargetFps.Text = isJa ? "目標FPS上限:" : "Target FPS Limit:";
            ChkDisablePostFx.Content = isJa ? "PostFX (ブルーム・被写界深度等の重いエフェクト) を無効化" : "Disable PostFX (Bloom, Depth of Field, etc.)";
            ChkLowRenderQuality.Content = isJa ? "レンダリング品質を軽量化 (FRM品質レベル1固定)" : "Low Render Quality (Force FRM Level 1)";
            LblLargeCardTitle.Text = isJa ? "大人数サーバー向けの軽量化" : "Large-Server Performance";
            LblLargeNote.Text = isJa ? "人が多いサーバーで重いのは主に描画と物理演算です。ここはRoblox公式が許可しているフラグだけを使います。効果はPCやゲームによって変わります。" : "Crowded servers are mostly bound by rendering. Only flags on Roblox's official allowlist are used here; results vary by PC and game.";
            BtnLargePreset.Content = isJa ? "⚡ 大人数向けプリセットを適用" : "⚡ Apply large-server preset";
            ChkLowTexture.Content = isJa ? "テクスチャ品質を最低にする" : "Lowest texture quality";
            ChkDisableMsaa.Content = isJa ? "アンチエイリアス (MSAA) を切る" : "Disable MSAA";
            ChkReduceGrass.Content = isJa ? "草の描画をなくす" : "Remove grass rendering";
            ChkAggressiveLod.Content = isJa ? "遠くのモデルを早めに簡略化する (LOD距離を短縮)" : "Simplify distant models sooner (shorter LOD distance)";
            ChkPauseVoxelizer.Content = isJa ? "ライティングの更新を停止 (見た目が崩れることがあります)" : "Pause voxel lighting updates (can look off)";
            ChkHighPriority.Content = isJa ? "RobloxのCPU優先度を「高」にする" : "Set Roblox CPU priority to High";
            LblFlagNote.Text = isJa ? "※ FPS上限の解放とPostFX無効化は、Roblox公式の許可リストにないため、現在のRobloxでは効かない場合があります。" : "Note: FPS unlock and PostFX are not on Roblox's official allowlist and may be ignored by the current client.";
            LblGraphicsCardTitle.Text = isJa ? "グラフィックス・レンダリング" : "Graphics & Rendering";
            LblGraphicsApi.Text = isJa ? "描画エンジン (API):" : "Graphics API:";
            CmbApiAuto.Content = isJa ? "自動 (デフォルト)" : "Automatic (Default)";
            LblCustomFlagsTitle.Text = isJa ? "フラグ管理" : "Flag Manager";
            LblFlagMgrHint.Text = isJa ? "スイッチで有効/無効を切り替え、値はそのまま書き換えられます。変更は自動保存されます。上のスイッチ類の設定が優先されます。" : "Toggle flags on/off and edit values inline. Changes are saved automatically. The switches above take priority.";
            LblAddFlagTitle.Text = isJa ? "フラグを追加 (候補から選ぶか、名前を直接入力)" : "Add a flag (pick a suggestion or type a name)";
            BtnAddFlag.Content = isJa ? "追加" : "Add";
            TxtFlagSearch.ToolTip = isJa ? "名前で絞り込み" : "Filter by name";
            TxtAddFlagValue.ToolTip = isJa ? "値 (true / false / 数値)" : "Value (true / false / number)";
            TxtAddFlagName.ToolTip = isJa ? "フラグ名 (例: FIntDebugForceMSAASamples)" : "Flag name";
            LblJsonEditorTitle.Text = isJa ? "JSONエディタ (上のリストと同期します)" : "JSON editor (synced with the list above)";
            UpdateJsonToggleText();
            RenderFlagRows();
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
            BtnCopyLogs.Content = isJa ? "コピー" : "Copy";
            BtnClearLogs.Content = isJa ? "クリア" : "Clear";
            BtnOpenRobloxLogs.Content = isJa ? "📂 Robloxログ" : "📂 Roblox Logs";
            BtnOpenLauncherLogs.Content = isJa ? "📂 ランチャーログ" : "📂 Launcher Logs";

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
            BtnRestoreMods.Content = isJa ? "Modsを元に戻す" : "Restore Original Files";

            LblAppearanceTitle.Text = isJa ? "表示" : "Appearance";
            LblTheme.Text = isJa ? "テーマ:" : "Theme:";
            CmbThemeSystem.Content = isJa ? "システム設定に合わせる (推奨)" : "Follow system (recommended)";
            CmbThemeDark.Content = isJa ? "ダーク" : "Dark";
            CmbThemeLight.Content = isJa ? "ライト" : "Light";
            LblLaunchSectionTitle.Text = isJa ? "起動の動作" : "Launch Behavior";
            ChkMinimizeToTray.Content = isJa ? "最小化したときトレイに格納する" : "Hide to tray when minimized";
            DescAutoClose.Text = isJa ? "起動後はウィンドウを隠してトレイに常駐し、Roblox の終了と同時にランチャーも終了します。" : "Hides to the tray after launch and exits together with Roblox.";
            DescMinimizeToTray.Text = isJa ? "オフ: 最小化するとタスクバーに残ります。オン: トレイに格納します。" : "Off: stays on the taskbar. On: hides to the tray.";
            DescBypassLauncher.Text = isJa ? "ブラウザ (roblox.com) から参加するときも FastFlags / Mods を適用しません。" : "Skips FastFlags and Mods when joining from the browser.";
            DescCleanClientSettings.Text = isJa ? "起動前に ClientAppSettings.json を作り直し、削除した設定が残らないようにします (推奨)。" : "Rebuilds ClientAppSettings.json before launch so removed flags don't linger (recommended).";
            DescProtectCursor.Text = isJa ? "Roblox が起動直後にカーソルを上書きしても、数秒間再適用して維持します。" : "Re-applies your cursor for a few seconds in case Roblox overwrites it at startup.";
            DescAutoRegisterProtocol.Text = isJa ? "ブラウザの「Robloxを開く」をこのランチャーで受け取れるようにします。" : "Lets this launcher handle \"Open Roblox\" links from your browser.";
            LblModsNote.Text = isJa ? "ここでの変更は次回の Roblox 起動時に反映されます (起動中の Roblox には反映されません)。" : "Changes take effect the next time Roblox is launched (not while it is running).";
            UpdateRunningStatus();
            BtnExportSettings.Content = isJa ? "設定をエクスポート" : "Export Settings";
            BtnImportSettings.Content = isJa ? "設定をインポート" : "Import Settings";

            LblQuickJoinTitle.Text = isJa ? "クイック参加" : "Quick Join";
            LblQuickJoinHint.Text = isJa
                ? "ゲームのURL・プレイスID・プライベートサーバーのリンクを貼り付けて Enter"
                : "Paste a game URL, place ID or private server link, then press Enter";
            BtnQuickJoin.Content = isJa ? "参加" : "Join";
            LblRecentTitle.Text = isJa ? "最近のゲーム (右クリックで履歴から削除)" : "Recent games (right-click to remove)";
            BtnCancelLaunch.Content = isJa ? "キャンセル" : "Cancel";
            BtnFpsUnlimited.Content = isJa ? "無制限" : "Max";

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
            RebuildRecentGames();

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
                SetFg(ProtocolStatusText, "ErrBrush");
            }
            else if (registered)
            {
                ProtocolStatusText.Text = isJa ? "状態: 有効 (roblox-player / roblox 登録済み)" : "Status: Active (roblox-player / roblox registered)";
                SetFg(ProtocolStatusText, "OkBrush");
            }
            else
            {
                ProtocolStatusText.Text = isJa ? "状態: 自動登録オフ" : "Status: Auto-register OFF";
                SetFg(ProtocolStatusText, "TextMutedBrush");
            }
        }

        private void AddLog(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";

            // ランチャーは Roblox 終了と同時に閉じるため、画面のログは消える。ファイルにも残しておく。
            try
            {
                lock (logLock)
                {
                    string file = Path.Combine(logsDir, $"launcher-{DateTime.Now:yyyyMMdd}.log");
                    File.AppendAllText(file, line + Environment.NewLine);
                }
            }
            catch { }

            try
            {
                void Append()
                {
                    TxtLogs.AppendText(line + "\n");
                    if (TxtLogs.Text.Length > 200000)
                    {
                        TxtLogs.Text = TxtLogs.Text.Substring(TxtLogs.Text.Length - 100000);
                    }
                    TxtLogs.ScrollToEnd();
                }

                if (Dispatcher.CheckAccess()) Append();
                else Dispatcher.Invoke(Append);
            }
            catch { }
        }

        private void CleanupOldLogs()
        {
            try
            {
                var old = new DirectoryInfo(logsDir)
                    .GetFiles("launcher-*.log")
                    .OrderByDescending(f => f.Name)
                    .Skip(14);
                foreach (var f in old)
                {
                    try { f.Delete(); } catch { }
                }
            }
            catch { }
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
                    var custom = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(config.CustomFlagsJson, JsonReadOpts);
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
            if (config.LowTextureQuality) keys.Add("DFIntTextureQualityOverride");
            if (config.DisableMsaa) keys.Add("FIntDebugForceMSAASamples");
            if (config.ReduceGrass) keys.Add("FIntFRMMaxGrassDistance");
            if (config.AggressiveLod) keys.Add("DFIntCSGLevelOfDetailSwitchingDistance");
            if (config.PauseVoxelizer) keys.Add("DFFlagDebugPauseVoxelizer");
            if (config.GraphicsApi == "D3D11" || config.GraphicsApi == "D3D10" || config.GraphicsApi == "Vulkan" || config.GraphicsApi == "OpenGL")
            {
                keys.Add("GraphicsApi:" + config.GraphicsApi);
            }
            return keys.Count;
        }

        private void UpdateDashboard()
        {
            bool isJa = GetEffectiveLanguage() == "ja";

            string? exe = GetLatestRobloxExe();
            if (exe == null)
            {
                DashClientText.Text = isJa ? "未検出" : "Not found";
                SetFg(DashClientText, "ErrBrush");
            }
            else
            {
                DashClientText.Text = Path.GetFileName(Path.GetDirectoryName(exe)!);
                SetFg(DashClientText, "TextBrush");
            }

            int flags = CountActiveFlags();
            DashFlagsText.Text = flags > 0 ? (isJa ? $"{flags} 件を適用" : $"{flags} applied") : (isJa ? "なし" : "None");
            SetFg(DashFlagsText, flags > 0 ? "TextBrush" : "TextMutedBrush");

            DashModsText.Text = config.EnableMods ? (isJa ? "有効" : "Enabled") : (isJa ? "無効" : "Disabled");
            SetFg(DashModsText, config.EnableMods ? "OkBrush" : "TextMutedBrush");
        }

        // テーマ変更に追従するよう、色は固定値ではなくリソース参照で設定する
        private static void SetFg(TextBlock tb, string key) => tb.SetResourceReference(TextBlock.ForegroundProperty, key);

        // ---------------- テーマ ----------------

        private static bool SystemUsesLightTheme()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("AppsUseLightTheme") is int v) return v != 0;
            }
            catch { }
            return false;
        }

        private bool IsLightTheme()
        {
            if (config.Theme == "Light") return true;
            if (config.Theme == "Dark") return false;
            return SystemUsesLightTheme();
        }

        private void ApplyTheme()
        {
            var palette = IsLightTheme() ? LightTheme : DarkTheme;
            var res = Application.Current.Resources;
            foreach (var kv in palette)
            {
                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(kv.Value);
                if (kv.Key.EndsWith("Color", StringComparison.Ordinal)) res[kv.Key] = color;
                else res[kv.Key] = new SolidColorBrush(color);
            }
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (config.Theme != "System") return;
            if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.VisualStyle) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ApplyTheme();
                UpdateDashboard();
            }));
        }

        private void CmbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!isLoaded) return;
            SaveSettings();
            ApplyTheme();
            UpdateDashboard();
        }

        private void LoadSettings()
        {
            if (File.Exists(configPath))
            {
                try
                {
                    string json = File.ReadAllText(configPath);
                    var loaded = JsonSerializer.Deserialize<LauncherConfig>(json, JsonReadOpts);
                    if (loaded != null) config = loaded;
                }
                catch (Exception ex)
                {
                    AddLog($"設定ファイル読み込みエラー: {ex.Message} (破損したファイルは config.json.bak に退避しました)");
                    try { File.Copy(configPath, configPath + ".bak", true); } catch { }
                }
            }
            config.RecentGames ??= new List<RecentGame>();

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
            ChkLowTexture.IsChecked = config.LowTextureQuality;
            ChkDisableMsaa.IsChecked = config.DisableMsaa;
            ChkReduceGrass.IsChecked = config.ReduceGrass;
            ChkAggressiveLod.IsChecked = config.AggressiveLod;
            ChkPauseVoxelizer.IsChecked = config.PauseVoxelizer;
            ChkHighPriority.IsChecked = config.HighPriority;
            config.DisabledFlags ??= new Dictionary<string, string>();
            InitFlagManager();
            RebuildFlagRows();

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

            foreach (ComboBoxItem item in CmbTheme.Items)
            {
                if (item.Tag?.ToString() == config.Theme)
                {
                    CmbTheme.SelectedItem = item;
                    break;
                }
            }
            if (CmbTheme.SelectedItem == null) CmbTheme.SelectedIndex = 0;
            ChkMinimizeToTray.IsChecked = config.MinimizeToTray;

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
            if (int.TryParse(TxtTargetFps.Text.Trim(), out int fps) && fps > 0)
            {
                config.TargetFps = Math.Min(fps, 9999);
            }
            config.DisablePostFx = ChkDisablePostFx.IsChecked == true;
            config.LowRenderQuality = ChkLowRenderQuality.IsChecked == true;
            config.CustomFlagsJson = TxtCustomFlags.Text;
            config.LowTextureQuality = ChkLowTexture.IsChecked == true;
            config.DisableMsaa = ChkDisableMsaa.IsChecked == true;
            config.ReduceGrass = ChkReduceGrass.IsChecked == true;
            config.AggressiveLod = ChkAggressiveLod.IsChecked == true;
            config.PauseVoxelizer = ChkPauseVoxelizer.IsChecked == true;
            config.HighPriority = ChkHighPriority.IsChecked == true;

            if (CmbGraphicsApi.SelectedItem is ComboBoxItem selectedApi)
            {
                config.GraphicsApi = selectedApi.Tag?.ToString() ?? "Auto";
            }

            config.EnableMods = ChkEnableMods.IsChecked == true;
            if (RbCursorDefault.IsChecked == true) config.CursorMode = "Default";
            else if (RbCursorClassic.IsChecked == true) config.CursorMode = "Classic";
            else if (RbCursorCustom.IsChecked == true) config.CursorMode = "Custom";
            else config.CursorMode = "System";

            if (CmbTheme.SelectedItem is ComboBoxItem selectedTheme)
            {
                config.Theme = selectedTheme.Tag?.ToString() ?? "System";
            }
            config.MinimizeToTray = ChkMinimizeToTray.IsChecked == true;

            config.BypassLauncher = ChkBypassLauncher.IsChecked == true;
            config.ProtectCursor = ChkProtectCursor.IsChecked == true;
            config.AutoRegisterProtocol = ChkAutoRegisterProtocol.IsChecked == true;
            config.AutoClose = ChkAutoClose.IsChecked == true;
            config.CleanClientSettings = ChkCleanClientSettings.IsChecked == true;

            try
            {
                WriteConfigFile();
            }
            catch (Exception ex)
            {
                AddLog($"設定の保存に失敗しました: {ex.Message}");
            }

            UpdateDashboard();
        }

        // 書き込み途中で落ちても config.json が壊れないよう、一時ファイル経由で置き換える
        private void WriteConfigFile()
        {
            string json = JsonSerializer.Serialize(config, JsonWriteOpts);
            string tmp = configPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, configPath, true);
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
            MarkFastFlagsDirty();
        }

        private void FastFlagUi_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!isLoaded) return;
            MarkFastFlagsDirty();
        }

        // 変更から少し待って自動保存する (「保存」を押し忘れても失われない)
        private void MarkFastFlagsDirty()
        {
            FastFlagsSaveStatusText.Text = GetEffectiveLanguage() == "ja" ? "変更あり… 自動で保存します" : "Changed… auto-saving";
            SetFg(FastFlagsSaveStatusText, "WarnBrush");
            if (autoSaveTimer != null)
            {
                autoSaveTimer.Stop();
                autoSaveTimer.Start();
            }
        }

        private void AutoSaveTick()
        {
            autoSaveTimer?.Stop();
            if (!isLoaded) return;
            SaveSettings();
            FastFlagsSaveStatusText.Text = GetEffectiveLanguage() == "ja" ? "✓ 自動保存しました (次回の起動時に反映)" : "✓ Auto-saved (applied on next launch)";
            SetFg(FastFlagsSaveStatusText, "OkBrush");
        }

        private void SaveFastFlags_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            string? robloxExe = GetLatestRobloxExe();
            bool isJa = GetEffectiveLanguage() == "ja";
            if (isLaunching || FindRunningRobloxBeta() != null)
            {
                FastFlagsSaveStatusText.Text = isJa ? "✓ 保存しました (Robloxの次回起動で反映)" : "✓ Saved (applies on the next Roblox launch)";
                SetFg(FastFlagsSaveStatusText, "OkBrush");
                return;
            }
            if (robloxExe != null)
            {
                try
                {
                    ApplyFastFlags(Path.GetDirectoryName(robloxExe)!);
                    FastFlagsSaveStatusText.Text = isJa ? "✓ 保存・反映しました" : "✓ Saved & Applied";
                    SetFg(FastFlagsSaveStatusText, "OkBrush");
                    AddLog("FastFlags設定を保存しました。");
                }
                catch (Exception ex)
                {
                    FastFlagsSaveStatusText.Text = isJa ? "保存しましたが反映に失敗しました" : "Saved, but failed to apply";
                    SetFg(FastFlagsSaveStatusText, "ErrBrush");
                    AddLog($"FastFlags反映エラー: {ex.Message}");
                }
            }
            else
            {
                FastFlagsSaveStatusText.Text = isJa ? "保存しました (Roblox未検出のため起動時に反映)" : "Saved (Roblox not found; will apply on launch)";
                SetFg(FastFlagsSaveStatusText, "WarnBrush");
                AddLog("FastFlags設定を保存しました (Robloxが見つからないため起動時に反映されます)。");
            }
        }

        // 大人数サーバー向けの軽量化プリセット (見た目が大きく崩れない項目だけをまとめてオン)
        private void LargeServerPreset_Click(object sender, RoutedEventArgs e)
        {
            ChkLowRenderQuality.IsChecked = true;
            ChkLowTexture.IsChecked = true;
            ChkDisableMsaa.IsChecked = true;
            ChkReduceGrass.IsChecked = true;
            ChkAggressiveLod.IsChecked = true;
            ChkHighPriority.IsChecked = true;
            SaveSettings();
            bool isJa = GetEffectiveLanguage() == "ja";
            FastFlagsSaveStatusText.Text = isJa ? "✓ 大人数向けプリセットを適用しました (次回の起動から有効)" : "✓ Large-server preset applied (takes effect on next launch)";
            SetFg(FastFlagsSaveStatusText, "OkBrush");
            AddLog("大人数サーバー向けの軽量化プリセットを適用しました。");
        }

        // Roblox本体のCPU優先度を上げる (OS標準の機能。失敗しても起動には影響しない)
        private void ApplyRobloxPriority(Process? p)
        {
            if (!config.HighPriority || p == null) return;
            try
            {
                p.PriorityClass = ProcessPriorityClass.High;
                AddLog("RobloxのCPU優先度を「高」にしました。");
            }
            catch (Exception ex)
            {
                AddLog($"CPU優先度の変更に失敗しました: {ex.Message}");
            }
        }

        // ============================================================
        //  FastFlag 管理画面 (リスト表示・有効/無効・値の編集・追加・削除)
        // ============================================================

        private readonly List<FlagRow> flagRows = new List<FlagRow>();
        private bool syncingFlags = false;
        private bool flagCatalogReady = false;

        // 追加時の候補。Roblox公式の許可リストにあるものだけを載せる (最後の1件だけ非公式)
        private static readonly (string name, string def, string ja, string en)[] FlagCatalog =
        {
            ("DFIntDebugFRMQualityLevelOverride", "1", "描画品質レベルの固定 (1が最軽量)", "Force render quality level (1 = lightest)"),
            ("DFFlagTextureQualityOverrideEnabled", "true", "テクスチャ品質の上書きを有効にする (下の品質値とセット)", "Enable texture quality override (pair with the level)"),
            ("DFIntTextureQualityOverride", "0", "テクスチャ品質 (0が最低)", "Texture quality (0 = lowest)"),
            ("FIntDebugForceMSAASamples", "1", "MSAAサンプル数 (1でほぼオフ)", "MSAA samples (1 = effectively off)"),
            ("FIntFRMMinGrassDistance", "0", "草を描く最短距離 (0で描かない)", "Min grass distance (0 = none)"),
            ("FIntFRMMaxGrassDistance", "0", "草を描く最長距離 (0で描かない)", "Max grass distance (0 = none)"),
            ("FIntGrassMovementReducedMotionFactor", "5", "草の揺れを抑える係数", "Grass sway reduction factor"),
            ("DFIntCSGLevelOfDetailSwitchingDistance", "100", "メッシュのLOD切替距離 (小さいほど早く簡略化)", "Mesh LOD switch distance (smaller = simplify sooner)"),
            ("DFIntCSGLevelOfDetailSwitchingDistanceL12", "200", "LOD 1→2の切替距離", "LOD 1-to-2 switch distance"),
            ("DFIntCSGLevelOfDetailSwitchingDistanceL23", "300", "LOD 2→3の切替距離", "LOD 2-to-3 switch distance"),
            ("DFIntCSGLevelOfDetailSwitchingDistanceL34", "400", "LOD 3→4の切替距離", "LOD 3-to-4 switch distance"),
            ("DFFlagDebugPauseVoxelizer", "true", "ライティングの更新を止める (見た目が崩れることがあります)", "Pause voxel lighting updates (can look off)"),
            ("DFFlagDisableDPIScale", "true", "Windowsの拡大表示(DPI)による描画拡大を無効化", "Disable DPI scaling of the render"),
            ("FFlagDebugSkyGray", "true", "空を灰色にする", "Gray sky"),
            ("FFlagHandleAltEnterFullscreenManually", "false", "Alt+Enterの全画面切替の挙動を変える", "Change how Alt+Enter fullscreen is handled"),
            ("FFlagDebugGraphicsPreferD3D11", "true", "Direct3D 11を優先", "Prefer Direct3D 11"),
            ("FFlagDebugGraphicsPreferVulkan", "true", "Vulkanを優先", "Prefer Vulkan"),
            ("FFlagDebugGraphicsPreferOpenGL", "true", "OpenGLを優先", "Prefer OpenGL"),
            ("DFIntTaskSchedulerTargetFps", "240", "FPS上限 (※公式の許可リスト外。現在のRobloxでは効かない場合があります)", "FPS cap (not on the official allowlist; may be ignored)")
        };

        private void InitFlagManager()
        {
            if (flagCatalogReady) return;
            flagCatalogReady = true;
            CmbAddFlag.Items.Clear();
            foreach (var f in FlagCatalog)
            {
                CmbAddFlag.Items.Add(new ComboBoxItem { Content = f.name, Tag = f.name });
            }
        }

        private static string ParseRaw(string text)
        {
            string t = (text ?? "").Trim();
            if (t.Equals("true", StringComparison.OrdinalIgnoreCase)) return "true";
            if (t.Equals("false", StringComparison.OrdinalIgnoreCase)) return "false";
            if (Regex.IsMatch(t, @"^-?\d+(\.\d+)?$")) return t;
            return JsonSerializer.Serialize(t);
        }

        private static string DisplayRaw(string raw)
        {
            if (raw.StartsWith("\""))
            {
                try { return JsonSerializer.Deserialize<string>(raw) ?? raw; } catch { }
            }
            return raw;
        }

        // JSONエディタの内容からリストを作り直す (構文エラー中は何もしない)
        private void RebuildFlagRows()
        {
            if (FlagListPanel == null) return;
            Dictionary<string, JsonElement>? parsed;
            try
            {
                parsed = string.IsNullOrWhiteSpace(TxtCustomFlags.Text)
                    ? new Dictionary<string, JsonElement>()
                    : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(TxtCustomFlags.Text, JsonReadOpts);
            }
            catch { return; }
            if (parsed == null) return;

            flagRows.Clear();
            foreach (var kv in parsed)
            {
                flagRows.Add(new FlagRow { Name = kv.Key, Raw = kv.Value.GetRawText(), Enabled = true });
            }
            config.DisabledFlags ??= new Dictionary<string, string>();
            foreach (var kv in config.DisabledFlags)
            {
                if (!flagRows.Any(r => r.Name == kv.Key))
                    flagRows.Add(new FlagRow { Name = kv.Key, Raw = kv.Value, Enabled = false });
            }
            config.DisabledFlags = flagRows.Where(r => !r.Enabled).ToDictionary(r => r.Name, r => r.Raw);
            RenderFlagRows();
        }

        // リストの内容をJSONエディタ (= 保存される設定) に書き戻す
        private void SyncJsonFromRows()
        {
            var enabled = flagRows.Where(r => r.Enabled).ToList();
            var sb = new StringBuilder("{");
            for (int i = 0; i < enabled.Count; i++)
            {
                sb.Append("\n  ").Append(JsonSerializer.Serialize(enabled[i].Name)).Append(": ").Append(enabled[i].Raw);
                if (i < enabled.Count - 1) sb.Append(',');
            }
            sb.Append(enabled.Count > 0 ? "\n}" : "}");

            config.DisabledFlags = flagRows.Where(r => !r.Enabled).ToDictionary(r => r.Name, r => r.Raw);
            syncingFlags = true;
            try { TxtCustomFlags.Text = sb.ToString(); }
            finally { syncingFlags = false; }
            if (isLoaded) MarkFastFlagsDirty();
        }

        private void RenderFlagRows()
        {
            if (FlagListPanel == null) return;
            bool isJa = GetEffectiveLanguage() == "ja";
            FlagListPanel.Children.Clear();
            string filter = (TxtFlagSearch?.Text ?? "").Trim();
            int shown = 0;

            foreach (var row in flagRows)
            {
                if (filter.Length > 0 && row.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                shown++;
                var r = row;
                bool official = OfficialAllowlist.Contains(r.Name);

                var grid = new Grid { Margin = new Thickness(0, 0, 0, 8), Opacity = r.Enabled ? 1.0 : 0.5 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var toggle = new CheckBox
                {
                    IsChecked = r.Enabled,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0),
                    ToolTip = isJa ? "オン/オフ (オフにすると値を残したまま適用しません)" : "On/off (off keeps the value but does not apply it)"
                };
                toggle.Checked += (_, __) => { r.Enabled = true; SyncJsonFromRows(); RenderFlagRows(); };
                toggle.Unchecked += (_, __) => { r.Enabled = false; SyncJsonFromRows(); RenderFlagRows(); };
                Grid.SetColumn(toggle, 0);
                grid.Children.Add(toggle);

                var namePanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
                var nameText = new TextBlock
                {
                    Text = r.Name,
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize = 12.5,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = r.Name
                };
                nameText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                namePanel.Children.Add(nameText);

                string? desc = FlagCatalog.Where(f => f.name == r.Name).Select(f => isJa ? f.ja : f.en).FirstOrDefault();
                var sub = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap };
                if (!official)
                {
                    sub.Text = isJa ? "⚠ 公式の許可リスト外 (現在のRobloxでは無視される可能性があります)" : "⚠ Not on the official allowlist (may be ignored)";
                    sub.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
                }
                else
                {
                    sub.Text = desc ?? (isJa ? "✓ 公式の許可リストにあります" : "✓ On the official allowlist");
                    sub.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
                }
                namePanel.Children.Add(sub);
                Grid.SetColumn(namePanel, 1);
                grid.Children.Add(namePanel);

                FrameworkElement editor;
                if (r.Raw == "true" || r.Raw == "false")
                {
                    var cb = new CheckBox { IsChecked = r.Raw == "true", VerticalAlignment = VerticalAlignment.Center, Content = r.Raw == "true" ? (isJa ? "オン" : "On") : (isJa ? "オフ" : "Off"), MinWidth = 70 };
                    cb.Checked += (_, __) => { r.Raw = "true"; SyncJsonFromRows(); RenderFlagRows(); };
                    cb.Unchecked += (_, __) => { r.Raw = "false"; SyncJsonFromRows(); RenderFlagRows(); };
                    editor = cb;
                }
                else
                {
                    var tb = new TextBox { Text = DisplayRaw(r.Raw), Width = 110, Height = 28, Padding = new Thickness(6, 3, 6, 3), VerticalAlignment = VerticalAlignment.Center };
                    tb.TextChanged += (_, __) => { r.Raw = ParseRaw(tb.Text); SyncJsonFromRows(); };
                    editor = tb;
                }
                editor.Margin = new Thickness(0, 0, 8, 0);
                Grid.SetColumn(editor, 2);
                grid.Children.Add(editor);

                var del = new Button
                {
                    Content = "✕",
                    Style = (Style)FindResource("SecondaryButton"),
                    Width = 30,
                    Height = 28,
                    Padding = new Thickness(0),
                    ToolTip = isJa ? "このフラグを削除" : "Remove this flag"
                };
                del.Click += (_, __) => { flagRows.Remove(r); SyncJsonFromRows(); RenderFlagRows(); };
                Grid.SetColumn(del, 3);
                grid.Children.Add(del);

                FlagListPanel.Children.Add(grid);
            }

            FlagEmptyText.Text = flagRows.Count == 0
                ? (isJa ? "まだフラグがありません。下から追加できます。" : "No flags yet. Add one below.")
                : (shown == 0 ? (isJa ? "一致するフラグがありません。" : "No matching flags.") : "");
        }

        private void TxtFlagSearch_TextChanged(object sender, TextChangedEventArgs e) => RenderFlagRows();

        private void CmbAddFlag_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbAddFlag.SelectedItem is not ComboBoxItem item || item.Tag is not string name) return;
            var f = FlagCatalog.First(x => x.name == name);
            bool isJa = GetEffectiveLanguage() == "ja";
            TxtAddFlagName.Text = f.name;
            TxtAddFlagValue.Text = f.def;
            LblAddFlagDesc.Text = isJa ? f.ja : f.en;
        }

        private void AddFlag_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                BtnAddFlag_Click(sender, e);
                e.Handled = true;
            }
        }

        private void BtnAddFlag_Click(object sender, RoutedEventArgs e)
        {
            bool isJa = GetEffectiveLanguage() == "ja";
            string name = TxtAddFlagName.Text.Trim();
            if (name.Length == 0 || Regex.IsMatch(name, @"[\s""{}:,]"))
            {
                LblAddFlagDesc.Text = isJa ? "フラグ名を入力してください (空白や記号は使えません)。" : "Enter a valid flag name (no spaces or symbols).";
                SetFg(LblAddFlagDesc, "ErrBrush");
                return;
            }
            string raw = ParseRaw(TxtAddFlagValue.Text.Length == 0 ? "true" : TxtAddFlagValue.Text);

            var existing = flagRows.FirstOrDefault(r => r.Name == name);
            if (existing != null) { existing.Raw = raw; existing.Enabled = true; }
            else flagRows.Add(new FlagRow { Name = name, Raw = raw, Enabled = true });

            SyncJsonFromRows();
            RenderFlagRows();
            TxtAddFlagName.Text = "";
            TxtAddFlagValue.Text = "";
            CmbAddFlag.SelectedIndex = -1;
            LblAddFlagDesc.Text = OfficialAllowlist.Contains(name)
                ? (isJa ? $"✓ {name} を追加しました" : $"✓ Added {name}")
                : (isJa ? $"追加しました。ただし {name} は公式の許可リスト外なので、現在のRobloxでは無視される可能性があります。" : $"Added, but {name} is not on the official allowlist and may be ignored.");
            SetFg(LblAddFlagDesc, OfficialAllowlist.Contains(name) ? "OkBrush" : "WarnBrush");
        }

        private void ToggleJson_Click(object sender, RoutedEventArgs e)
        {
            JsonEditorPanel.Visibility = JsonEditorPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            UpdateJsonToggleText();
        }

        private void UpdateJsonToggleText()
        {
            bool isJa = GetEffectiveLanguage() == "ja";
            bool open = JsonEditorPanel.Visibility == Visibility.Visible;
            BtnToggleJson.Content = (isJa ? "{ }  JSONを直接編集 " : "{ }  Edit JSON directly ") + (open ? "▲" : "▼");
        }

        private void ResetFastFlags_Click(object sender, RoutedEventArgs e)
        {
            ChkUnlockFPS.IsChecked = false;
            TxtTargetFps.Text = "9999";
            ChkDisablePostFx.IsChecked = false;
            ChkLowRenderQuality.IsChecked = false;
            ChkLowTexture.IsChecked = false;
            ChkDisableMsaa.IsChecked = false;
            ChkReduceGrass.IsChecked = false;
            ChkAggressiveLod.IsChecked = false;
            ChkPauseVoxelizer.IsChecked = false;
            ChkHighPriority.IsChecked = false;
            config.DisabledFlags = new Dictionary<string, string>();
            CmbGraphicsApi.SelectedIndex = 0;
            TxtCustomFlags.Text = "{\n  \"DFIntTaskSchedulerTargetFps\": 240\n}";
            SaveSettings();
            FastFlagsSaveStatusText.Text = GetEffectiveLanguage() == "ja" ? "✓ 初期値にリセットしました" : "✓ Reset to defaults";
            SetFg(FastFlagsSaveStatusText, "InfoBrush");
            AddLog("FastFlags設定をリセットしました。");
        }

        private void FormatJson_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(TxtCustomFlags.Text, JsonReadOpts);
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
                var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(TxtCustomFlags.Text, JsonReadOpts);
                if (parsed != null)
                {
                    int invalidCount = parsed.Keys.Count(k => !AllowedFastFlags.Contains(k));
                    if (invalidCount > 0)
                    {
                        JsonStatusText.Text = isJa
                            ? $"JSON正常 (注意: ホワイトリスト外の {invalidCount} 件は適用時に除外されます)"
                            : $"JSON Valid ({invalidCount} non-allowlisted keys will be ignored)";
                        SetFg(JsonStatusText, "WarnBrush");
                    }
                    else
                    {
                        JsonStatusText.Text = isJa ? "JSONステータス: 正常 (すべてホワイトリスト内)" : "JSON Status: Valid (All allowlisted)";
                        SetFg(JsonStatusText, "OkBrush");
                    }
                }
            }
            catch
            {
                JsonStatusText.Text = isJa ? "JSON構文エラー: 括弧やカンマを確認してください" : "JSON Syntax Error";
                SetFg(JsonStatusText, "ErrBrush");
            }
            if (!syncingFlags) RebuildFlagRows();
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

        private void LaunchRoblox_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings(); // 「保存」を押し忘れた FastFlags の変更も起動時に反映する
            LaunchRobloxClient("", bypass: false);
        }

        private void LaunchVanilla_Click(object sender, RoutedEventArgs e) => LaunchRobloxClient("", bypass: true);

        private void CancelLaunch_Click(object sender, RoutedEventArgs e)
        {
            try { launchCts?.Cancel(); } catch { }
        }

        // 二重起動を防ぎ、キャンセルできるようにするためのラッパー
        private async void LaunchRobloxClient(string launchArgs, bool bypass)
        {
            if (isLaunching)
            {
                AddLog("すでに起動処理中のため、新しい起動要求は無視しました。");
                return;
            }

            isLaunching = true;
            launchCts = new CancellationTokenSource();
            try
            {
                await LaunchRobloxClientCore(launchArgs, bypass, launchCts.Token);
            }
            catch (Exception ex)
            {
                AddLog($"起動エラー: {ex.Message}");
                if (!isRunningAsProtocol) LaunchOverlay.Visibility = Visibility.Collapsed;
            }
            finally
            {
                isLaunching = false;
                try { launchCts?.Dispose(); } catch { }
                launchCts = null;
            }
        }

        private async Task LaunchRobloxClientCore(string launchArgs, bool bypass, CancellationToken ct)
        {
            if (!isRunningAsProtocol) LaunchOverlay.Visibility = Visibility.Visible;

            string? robloxExe = GetLatestRobloxExe();
            if (robloxExe == null)
            {
                robloxExe = await InstallRobloxAsync(ct);
                UpdateDashboard();
            }
            if (robloxExe == null && ct.IsCancellationRequested)
            {
                if (!isRunningAsProtocol) LaunchOverlay.Visibility = Visibility.Collapsed;
                else Application.Current.Shutdown();
                return;
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
                    RestoreMods(targetVersionDir, silent: false);
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
                        RestoreMods(targetVersionDir, silent: true);
                    }
                }

                AddLog("Robloxクライアントを起動しています...");

                Process? proc = null;
                string quotedArgs = string.IsNullOrEmpty(launchArgs) ? "" : "\"" + launchArgs.Replace("\"", "") + "\"";
                string launcherExe = Path.Combine(targetVersionDir, "RobloxPlayerLauncher.exe");
                if (!File.Exists(launcherExe))
                {
                    // 環境によっては Versions フォルダ直下にある
                    string? versionsRoot = Path.GetDirectoryName(targetVersionDir);
                    string alt = versionsRoot == null ? "" : Path.Combine(versionsRoot, "RobloxPlayerLauncher.exe");
                    if (alt.Length > 0 && File.Exists(alt)) launcherExe = alt;
                }

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
                var waitResult = await WaitForRobloxStartAsync(targetVersionDir, knownVersionDirs, bypass, ct);
                targetVersionDir = waitResult.versionDir;
                UpdateDashboard();
                if (waitResult.beta != null) proc = waitResult.beta;
                if (!bypass) ApplyRobloxPriority(waitResult.beta);
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
        private async Task<string?> InstallRobloxAsync(CancellationToken ct)
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
                    if (ct.IsCancellationRequested)
                    {
                        AddLog("Robloxのインストール待機をキャンセルしました。");
                        return null;
                    }

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
        private async Task<(Process? beta, string versionDir)> WaitForRobloxStartAsync(string initialVersionDir, HashSet<string> knownDirs, bool bypass, CancellationToken ct)
        {
            bool isJa = GetEffectiveLanguage() == "ja";
            string currentDir = initialVersionDir;
            bool showedUpdating = false;
            bool appliedToNewVersion = false;
            var sw = Stopwatch.StartNew();

            while (sw.Elapsed < TimeSpan.FromMinutes(10))
            {
                if (ct.IsCancellationRequested)
                {
                    AddLog("Robloxの起動待機をキャンセルしました。");
                    return (null, currentDir);
                }

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
                    CopyModFile(versionDir, sysArrowPng, Path.Combine(folder, "ArrowCursor.png"));
                    CopyModFile(versionDir, sysArrowPng, Path.Combine(folder, "ArrowFarCursor.png"));
                    if (File.Exists(sysIbeamPng))
                    {
                        CopyModFile(versionDir, sysIbeamPng, Path.Combine(folder, "IBeamCursor.png"));
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
                        CopyModFile(versionDir, classicArrow, Path.Combine(folder, "ArrowCursor.png"));
                        CopyModFile(versionDir, File.Exists(classicFar) ? classicFar : classicArrow, Path.Combine(folder, "ArrowFarCursor.png"));
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
                            CopyModFile(versionDir, classicArrow, Path.Combine(folder, "ArrowCursor.png"));
                            CopyModFile(versionDir, classicFar, Path.Combine(folder, "ArrowFarCursor.png"));
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
                        CopyModFile(versionDir, config.CustomCursorPath, Path.Combine(folder, fileName));
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

        private void CopyModFile(string versionDir, string srcPath, string destPath)
        {
            if (!File.Exists(srcPath)) return;
            try
            {
                BackupBeforeWrite(versionDir, destPath);
                if (File.Exists(destPath))
                {
                    File.SetAttributes(destPath, FileAttributes.Normal);
                }
                File.Copy(srcPath, destPath, true);
            }
            catch { }
        }

        // ---- Mods の原本バックアップ ----
        // 上書きする前に元のファイルを Backups\<バージョン>\files に退避し、新規作成するファイルは created.txt に記録する。
        // これにより「解除」「Mods無効」「標準起動」で確実に元の状態へ戻せる。
        private string GetBackupRoot(string versionDir)
        {
            string name = Path.GetFileName(versionDir.TrimEnd('\\', '/'));
            return Path.Combine(backupsDir, name);
        }

        private HashSet<string> GetCreatedSet(string root)
        {
            if (createdFileCache.TryGetValue(root, out var set)) return set;
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string list = Path.Combine(root, "created.txt");
            try
            {
                if (File.Exists(list))
                {
                    foreach (string line in File.ReadAllLines(list))
                    {
                        if (!string.IsNullOrWhiteSpace(line)) set.Add(line.Trim());
                    }
                }
            }
            catch { }
            createdFileCache[root] = set;
            return set;
        }

        private void BackupBeforeWrite(string versionDir, string destPath)
        {
            try
            {
                string rel = Path.GetRelativePath(versionDir, destPath);
                if (rel.StartsWith("..") || Path.IsPathRooted(rel)) return;

                string root = GetBackupRoot(versionDir);
                string backupFile = Path.Combine(root, "files", rel);
                var created = GetCreatedSet(root);

                if (File.Exists(destPath))
                {
                    if (File.Exists(backupFile) || created.Contains(rel)) return; // 原本は退避済み / 自分で作ったファイル
                    Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
                    File.Copy(destPath, backupFile, false);
                }
                else if (created.Add(rel))
                {
                    Directory.CreateDirectory(root);
                    File.AppendAllText(Path.Combine(root, "created.txt"), rel + Environment.NewLine);
                }
            }
            catch { }
        }

        // 適用済みの Mods を取り消し、元のファイルに戻す。戻したファイル数を返す。
        private int RestoreMods(string versionDir, bool silent)
        {
            int restored = 0;
            int failed = 0;
            string root = GetBackupRoot(versionDir);
            if (!Directory.Exists(root)) return 0;

            try
            {
                string filesDir = Path.Combine(root, "files");
                if (Directory.Exists(filesDir))
                {
                    foreach (string backup in Directory.GetFiles(filesDir, "*", SearchOption.AllDirectories))
                    {
                        string rel = Path.GetRelativePath(filesDir, backup);
                        string dest = Path.Combine(versionDir, rel);
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                            if (File.Exists(dest)) File.SetAttributes(dest, FileAttributes.Normal);
                            File.Copy(backup, dest, true);
                            File.Delete(backup);
                            restored++;
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            AddLog($"元ファイルの復元に失敗: {rel} ({ex.Message})");
                        }
                    }
                }

                foreach (string rel in GetCreatedSet(root).ToList())
                {
                    string dest = Path.Combine(versionDir, rel);
                    try
                    {
                        if (File.Exists(dest))
                        {
                            File.SetAttributes(dest, FileAttributes.Normal);
                            File.Delete(dest);
                            restored++;
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        AddLog($"追加ファイルの削除に失敗: {rel} ({ex.Message})");
                    }
                }

                if (failed == 0)
                {
                    createdFileCache.Remove(root);
                    try { Directory.Delete(root, true); } catch { }
                }
            }
            catch (Exception ex)
            {
                AddLog($"Modsの復元中にエラー: {ex.Message}");
            }

            if (!silent || restored > 0)
            {
                AddLog($"Modsを元に戻しました ({restored} ファイル{(failed > 0 ? $"、失敗 {failed}" : "")})。");
            }
            return restored;
        }

        // Roblox側に存在しなくなったバージョンのバックアップを掃除する
        private void CleanupStaleBackups()
        {
            try
            {
                var alive = new HashSet<string>(
                    GetAllRobloxVersionDirs().Select(d => Path.GetFileName(d.TrimEnd('\\', '/'))),
                    StringComparer.OrdinalIgnoreCase);
                foreach (string dir in Directory.GetDirectories(backupsDir))
                {
                    if (!alive.Contains(Path.GetFileName(dir)))
                    {
                        createdFileCache.Remove(dir);
                        try { Directory.Delete(dir, true); } catch { }
                    }
                }
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
                    var customDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(config.CustomFlagsJson, JsonReadOpts);
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

            if (config.LowTextureQuality)
            {
                finalFlags["DFFlagTextureQualityOverrideEnabled"] = true;
                finalFlags["DFIntTextureQualityOverride"] = 0;
            }
            if (config.DisableMsaa) finalFlags["FIntDebugForceMSAASamples"] = 1;
            if (config.ReduceGrass)
            {
                finalFlags["FIntFRMMinGrassDistance"] = 0;
                finalFlags["FIntFRMMaxGrassDistance"] = 0;
            }
            if (config.AggressiveLod)
            {
                finalFlags["DFIntCSGLevelOfDetailSwitchingDistance"] = 100;
                finalFlags["DFIntCSGLevelOfDetailSwitchingDistanceL12"] = 200;
                finalFlags["DFIntCSGLevelOfDetailSwitchingDistanceL23"] = 300;
                finalFlags["DFIntCSGLevelOfDetailSwitchingDistanceL34"] = 400;
            }
            if (config.PauseVoxelizer) finalFlags["DFFlagDebugPauseVoxelizer"] = true;

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
                foreach (string k in finalFlags.Keys.Where(k => !OfficialAllowlist.Contains(k)))
                    AddLog($"[FastFlags] 注意: {k} はRoblox公式の許可リストにないため、現在のRobloxでは無視される可能性があります。");
                AddLog($"FastFlags を {finalFlags.Count} 件適用しました。");
            }
        }

        private async Task ApplyAllModsAsync(string versionDir)
        {
            // 前回の適用内容をいったん元に戻してから適用し直す (「解除」した項目が残らないように)
            RestoreMods(versionDir, silent: true);
            CleanupStaleBackups();

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
                    BackupBeforeWrite(versionDir, destOuch);
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
                                string destTex = Path.Combine(targetSkyDir, $"sky512_{face}.tex");
                                string destImg = Path.Combine(targetSkyDir, $"sky512_{face}{Path.GetExtension(file)}");
                                BackupBeforeWrite(versionDir, destTex);
                                BackupBeforeWrite(versionDir, destImg);
                                File.Copy(file, destTex, true);
                                File.Copy(file, destImg, true);
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
                                BackupBeforeWrite(versionDir, destFont);
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
                    BackupBeforeWrite(destinationDir, destFile);
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

        // ============================================================
        //  使いやすさ: サイドバーの状態表示 / ショートカット / ウィンドウサイズの記憶
        // ============================================================

        private void InitBackgroundServices()
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

            autoSaveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            autoSaveTimer.Tick += (_, __) => AutoSaveTick();

            statusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            statusTimer.Tick += (_, __) => UpdateRunningStatus();
            statusTimer.Start();
            UpdateRunningStatus();

            Application.Current.Exit += (_, __) =>
            {
                try { SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged; } catch { }
                try { WriteConfigFile(); } catch { }
            };
        }

        private void UpdateRunningStatus()
        {
            if (!IsLoaded && !isLoaded) return;
            bool isJa = GetEffectiveLanguage() == "ja";
            bool running = false;
            try { running = FindRunningRobloxBeta() != null; } catch { }

            string dotKey;
            if (isLaunching)
            {
                SidebarStatusText.Text = isJa ? "起動処理中..." : "Launching...";
                dotKey = "WarnBrush";
            }
            else if (running)
            {
                SidebarStatusText.Text = isJa ? "Roblox 実行中" : "Roblox is running";
                dotKey = "OkBrush";
            }
            else
            {
                SidebarStatusText.Text = isJa ? "Roblox 停止中" : "Roblox is not running";
                dotKey = "TextMutedBrush";
            }
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dotKey);

            BtnSidebarLaunch.IsEnabled = !isLaunching;
            BtnLaunchRoblox.IsEnabled = !isLaunching;
            BtnLaunchVanilla.IsEnabled = !isLaunching;
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (WelcomeOverlay.Visibility == Visibility.Visible)
                {
                    WelcomeOverlay.Visibility = Visibility.Collapsed;
                    e.Handled = true;
                }
                else if (LaunchOverlay.Visibility == Visibility.Visible)
                {
                    CancelLaunch_Click(sender, e);
                    e.Handled = true;
                }
                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;
            if (WelcomeOverlay.Visibility == Visibility.Visible || LaunchOverlay.Visibility == Visibility.Visible) return;

            Button? nav = null;
            switch (e.Key)
            {
                case Key.D1: nav = NavDashboard; break;
                case Key.D2: nav = NavFastFlags; break;
                case Key.D3: nav = NavMods; break;
                case Key.D4: nav = NavLogs; break;
                case Key.D5: nav = NavSettings; break;
            }

            if (nav != null)
            {
                Nav_Click(nav, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.S && PageFastFlags.Visibility == Visibility.Visible)
            {
                SaveFastFlags_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                LaunchRoblox_Click(sender, e);
                e.Handled = true;
            }
        }

        private void RestoreWindowSize()
        {
            if (config.WindowWidth >= MinWidth && config.WindowHeight >= MinHeight)
            {
                Width = Math.Min(config.WindowWidth, SystemParameters.WorkArea.Width);
                Height = Math.Min(config.WindowHeight, SystemParameters.WorkArea.Height);
            }
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!isLoaded || WindowState != WindowState.Normal) return;
            config.WindowWidth = ActualWidth;
            config.WindowHeight = ActualHeight;
        }

        // ============================================================
        //  追加機能: ログ / FPSプリセット / Mods復元 / 設定の入出力 / クイック参加
        // ============================================================

        private void CopyLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(TxtLogs.Text)) return;
                System.Windows.Clipboard.SetText(TxtLogs.Text);
                AddLog("ログをクリップボードにコピーしました。");
            }
            catch (Exception ex)
            {
                AddLog($"クリップボードへのコピーに失敗: {ex.Message}");
            }
        }

        private void OpenLauncherLogsFolder_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(logsDir);
            Process.Start("explorer.exe", logsDir);
        }

        private void FpsPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string value)
            {
                TxtTargetFps.Text = value;
                ChkUnlockFPS.IsChecked = true;
            }
        }

        private void TxtTargetFps_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!isLoaded) return;
            if (!int.TryParse(TxtTargetFps.Text.Trim(), out int fps) || fps < 1)
            {
                fps = config.TargetFps > 0 ? config.TargetFps : 9999;
            }
            TxtTargetFps.Text = Math.Min(fps, 9999).ToString();
        }

        private void RestoreMods_Click(object sender, RoutedEventArgs e)
        {
            bool isJa = GetEffectiveLanguage() == "ja";
            string? exe = GetLatestRobloxExe();
            if (exe == null)
            {
                AddLog("Robloxのインストールフォルダが見つかりませんでした。");
                return;
            }

            if (FindRunningRobloxBeta() != null)
            {
                MessageBox.Show(
                    isJa ? "Robloxが起動中です。Robloxを終了してからもう一度お試しください。" : "Roblox is running. Close it and try again.",
                    "AZQLAUNCHER", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int n = RestoreMods(Path.GetDirectoryName(exe)!, silent: false);
            MessageBox.Show(
                n > 0
                    ? (isJa ? $"{n} 個のファイルを元に戻しました。" : $"Restored {n} file(s).")
                    : (isJa ? "元に戻すファイルはありませんでした。" : "Nothing to restore."),
                "AZQLAUNCHER", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ExportSettings_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            var dialog = new SaveFileDialog
            {
                Title = "設定をエクスポート",
                FileName = $"azqlauncher-settings-{DateTime.Now:yyyyMMdd}.json",
                Filter = "JSON (*.json)|*.json"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                File.Copy(configPath, dialog.FileName, true);
                AddLog($"設定をエクスポートしました: {dialog.FileName}");
            }
            catch (Exception ex)
            {
                AddLog($"設定のエクスポートに失敗: {ex.Message}");
            }
        }

        private void ImportSettings_Click(object sender, RoutedEventArgs e)
        {
            bool isJa = GetEffectiveLanguage() == "ja";
            var dialog = new OpenFileDialog
            {
                Title = "設定をインポート",
                Filter = "JSON (*.json)|*.json|すべてのファイル (*.*)|*.*"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                var loaded = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(dialog.FileName), JsonReadOpts);
                if (loaded == null) throw new InvalidDataException("設定を読み取れませんでした。");
                loaded.IsFirstLaunch = false;
                loaded.RecentGames ??= new List<RecentGame>();

                isLoaded = false;
                try
                {
                    config = loaded;
                    WriteConfigFile();
                    LoadSettings();
                }
                finally
                {
                    isLoaded = true;
                }

                ApplyLanguage();
                UpdateProtocolStatusText(config.AutoRegisterProtocol);
                UpdateDashboard();
                AddLog($"設定をインポートしました: {Path.GetFileName(dialog.FileName)}");
                MessageBox.Show(isJa ? "設定をインポートしました。" : "Settings imported.", "AZQLAUNCHER", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AddLog($"設定のインポートに失敗: {ex.Message}");
                MessageBox.Show((isJa ? "設定のインポートに失敗しました: " : "Failed to import settings: ") + ex.Message,
                    "AZQLAUNCHER", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ---------------- クイック参加 ----------------

        private static string? GetQueryValue(string query, params string[] names)
        {
            foreach (string part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string key = Uri.UnescapeDataString(part.Substring(0, eq));
                foreach (string n in names)
                {
                    if (key.Equals(n, StringComparison.OrdinalIgnoreCase))
                    {
                        return Uri.UnescapeDataString(part.Substring(eq + 1));
                    }
                }
            }
            return null;
        }

        private bool TryParseJoinInput(string input, out RecentGame? game, out string error)
        {
            bool isJa = GetEffectiveLanguage() == "ja";
            game = null;
            error = "";

            string text = input.Trim();
            int hash = text.IndexOf('#');
            if (hash >= 0) text = text.Substring(0, hash);

            if (text.Length == 0)
            {
                error = isJa ? "ゲームのURLかプレイスIDを入力してください。" : "Enter a game URL or place ID.";
                return false;
            }

            string placeId = "", linkCode = "", jobId = "";

            if (Regex.IsMatch(text, @"^\d{1,20}$"))
            {
                placeId = text;
            }
            else
            {
                int q = text.IndexOf('?');
                string path = q >= 0 ? text.Substring(0, q) : text;
                string query = q >= 0 ? text.Substring(q + 1) : "";

                var m = GamePathRegex.Match(path);
                placeId = m.Success ? m.Groups[1].Value : (GetQueryValue(query, "placeId") ?? "");
                linkCode = GetQueryValue(query, "privateServerLinkCode", "linkCode") ?? "";
                jobId = GetQueryValue(query, "gameInstanceId", "jobId") ?? "";

                if (placeId.Length == 0 && GetQueryValue(query, "code") != null)
                {
                    error = isJa
                        ? "共有リンク (share?code=...) には未対応です。ゲームページのURL (…/games/数字/…) を使ってください。"
                        : "Share links (share?code=...) are not supported. Use the game page URL (…/games/<id>/…).";
                    return false;
                }
            }

            if (!Regex.IsMatch(placeId, @"^\d{1,20}$"))
            {
                error = isJa ? "プレイスIDを読み取れませんでした。ゲームページのURLかプレイスID(数字)を貼り付けてください。" : "Couldn't find a place ID. Paste the game page URL or the numeric place ID.";
                return false;
            }
            if (linkCode.Length > 0 && !Regex.IsMatch(linkCode, @"^[A-Za-z0-9_\-]{1,128}$"))
            {
                error = isJa ? "プライベートサーバーのリンクコードが不正です。" : "Invalid private server link code.";
                return false;
            }
            if (jobId.Length > 0 && !Regex.IsMatch(jobId, @"^[0-9A-Fa-f\-]{8,64}$"))
            {
                error = isJa ? "サーバーID (gameInstanceId) が不正です。" : "Invalid server ID (gameInstanceId).";
                return false;
            }

            game = new RecentGame { PlaceId = placeId, LinkCode = linkCode, JobId = jobId };
            return true;
        }

        private static string BuildJoinUri(RecentGame g)
        {
            string uri = $"roblox://experiences/start?placeId={g.PlaceId}";
            if (!string.IsNullOrEmpty(g.LinkCode)) uri += $"&linkCode={g.LinkCode}";
            if (!string.IsNullOrEmpty(g.JobId)) uri += $"&gameInstanceId={g.JobId}";
            return uri;
        }

        private static bool SameGame(RecentGame a, RecentGame b) =>
            a.PlaceId == b.PlaceId && a.LinkCode == b.LinkCode && a.JobId == b.JobId;

        private void TxtQuickJoin_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                QuickJoin_Click(sender, e);
            }
        }

        private void QuickJoin_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParseJoinInput(TxtQuickJoin.Text, out RecentGame? game, out string error) || game == null)
            {
                QuickJoinStatusText.Text = error;
                return;
            }

            QuickJoinStatusText.Text = "";
            TxtQuickJoin.Clear();
            StartQuickJoin(game);
        }

        private void StartQuickJoin(RecentGame game)
        {
            if (isLaunching)
            {
                QuickJoinStatusText.Text = GetEffectiveLanguage() == "ja" ? "起動処理中です。しばらくお待ちください。" : "A launch is already in progress.";
                return;
            }
            QuickJoinStatusText.Text = "";

            // 履歴の先頭へ (同じゲームは1件にまとめ、取得済みの名前は引き継ぐ)
            var previous = config.RecentGames.FirstOrDefault(x => SameGame(x, game));
            if (previous != null && string.IsNullOrEmpty(game.Name)) game.Name = previous.Name;
            if (string.IsNullOrEmpty(game.Name))
            {
                game.Name = config.RecentGames.FirstOrDefault(x => x.PlaceId == game.PlaceId && !string.IsNullOrEmpty(x.Name))?.Name ?? "";
            }
            config.RecentGames.RemoveAll(x => SameGame(x, game));
            config.RecentGames.Insert(0, game);
            if (config.RecentGames.Count > 8) config.RecentGames.RemoveRange(8, config.RecentGames.Count - 8);
            SaveSettings();
            RebuildRecentGames();

            AddLog($"クイック参加: PlaceId {game.PlaceId}{(string.IsNullOrEmpty(game.LinkCode) ? "" : " (プライベートサーバー)")}{(string.IsNullOrEmpty(game.JobId) ? "" : " (サーバー指定)")}");
            LaunchRobloxClient(BuildJoinUri(game), bypass: false);

            if (string.IsNullOrEmpty(game.Name)) _ = ResolvePlaceNameAsync(game.PlaceId);
        }

        private void RecentGame_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is RecentGame g) StartQuickJoin(g);
        }

        private void RemoveRecent_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag is RecentGame g)
            {
                config.RecentGames.Remove(g);
                SaveSettings();
                RebuildRecentGames();
            }
        }

        private void RebuildRecentGames()
        {
            bool isJa = GetEffectiveLanguage() == "ja";
            RecentGamesPanel.Children.Clear();
            LblRecentTitle.Visibility = config.RecentGames.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (RecentGame g in config.RecentGames)
            {
                string label = string.IsNullOrWhiteSpace(g.Name) ? $"Place {g.PlaceId}" : g.Name;
                if (label.Length > 24) label = label.Substring(0, 23) + "…";
                if (!string.IsNullOrEmpty(g.LinkCode)) label = "🔒 " + label;

                var btn = new Button
                {
                    Content = label,
                    Tag = g,
                    Style = (Style)FindResource("SecondaryButton"),
                    Padding = new Thickness(12, 6, 12, 6),
                    Margin = new Thickness(0, 0, 8, 8),
                    FontSize = 12,
                    ToolTip = $"PlaceId: {g.PlaceId}"
                };
                btn.Click += RecentGame_Click;

                var menu = new ContextMenu();
                var remove = new MenuItem { Header = isJa ? "履歴から削除" : "Remove from history", Tag = g };
                remove.Click += RemoveRecent_Click;
                menu.Items.Add(remove);
                btn.ContextMenu = menu;

                RecentGamesPanel.Children.Add(btn);
            }
        }

        // ゲーム名を公開APIから取得する (失敗してもプレイスIDの表示のまま)
        private async Task ResolvePlaceNameAsync(string placeId)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

                string universeJson = await Http.GetStringAsync($"https://apis.roblox.com/universes/v1/places/{placeId}/universe", cts.Token);
                using var universeDoc = JsonDocument.Parse(universeJson);
                if (!universeDoc.RootElement.TryGetProperty("universeId", out JsonElement idEl) || idEl.ValueKind != JsonValueKind.Number) return;

                string gamesJson = await Http.GetStringAsync($"https://games.roblox.com/v1/games?universeIds={idEl.GetRawText()}", cts.Token);
                using var gamesDoc = JsonDocument.Parse(gamesJson);
                JsonElement data = gamesDoc.RootElement.GetProperty("data");
                if (data.GetArrayLength() == 0) return;

                string? name = data[0].GetProperty("name").GetString();
                if (string.IsNullOrWhiteSpace(name)) return;

                foreach (RecentGame g in config.RecentGames.Where(x => x.PlaceId == placeId)) g.Name = name;
                SaveSettings();
                RebuildRecentGames();
            }
            catch
            {
                // 名前が取れなくても動作に影響しない
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
        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            if (!config.MinimizeToTray)
            {
                WindowState = WindowState.Minimized; // 普通にタスクバーへ最小化
                return;
            }

            Hide(); // トレイに格納
            if (!shownTrayHint && trayIcon != null)
            {
                shownTrayHint = true;
                try
                {
                    trayIcon.ShowBalloonTip(2000, "AZQLAUNCHER",
                        GetEffectiveLanguage() == "ja" ? "トレイに格納しました。アイコンをダブルクリックで再表示します。" : "Minimized to tray. Double-click the icon to show it again.",
                        System.Windows.Forms.ToolTipIcon.Info);
                }
                catch { }
            }
        }
        private void Close_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
    }
}