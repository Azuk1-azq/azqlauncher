# AZQLAUNCHER

Roblox を自分の環境に合わせて起動する、Windows 向けのランチャーです。  
A Windows launcher that applies your FastFlags and mods before starting Roblox.

**Version:** beta 0.0.3 · **Platform:** Windows 10/11 · **Runtime:** .NET 8 (WPF)

> AZQLAUNCHER は個人が開発した非公式ツールで、Roblox Corporation とは関係ありません。Roblox の利用規約を守ってお使いください。  
> AZQLAUNCHER is an unofficial, independently made tool and is not affiliated with Roblox Corporation.

---

## 日本語

### 主な機能

- **FastFlags / FPS 上限解放**: 目標 FPS、PostFX の無効化、レンダリング品質の軽量化、描画 API(D3D11 / D3D10 / Vulkan / OpenGL)の切り替え。
- **許可リスト方式**: JSON エディタに書いたキーのうち、内蔵の許可リストにあるものだけを適用します。それ以外は起動時に除外され、ログに記録されます。
- **Mods**: マウスカーソル(Windows の現在のカーソル / Roblox 標準 / 2013 年以前のクラシック / 任意の PNG)、死亡時の効果音(クラシック Oof または任意の音声ファイル)、スカイボックス(bk, dn, ft, lf, rt, up)、UI フォント(.ttf / .otf)。
- **カーソル保護**: 起動後に Roblox がカーソルを上書きしても、再適用して維持します。
- **ブラウザ連携**: `roblox-player` と `roblox` の URI を登録し、Web サイトの「プレイ」ボタンからランチャー経由で起動できます。
- **標準起動**: 「ランチャーを通さないで起動」で設定ファイルを削除し、標準のクライアントを起動します。ブラウザ起動時の既定にもできます。
- **更新への追従**: Roblox の更新で新しいバージョンフォルダが作られたら、そちらへ FastFlags と Mods を再適用します。
- **日本語 / 英語**: システム言語に合わせて自動切り替え。設定で固定もできます。
- **実行ログ**: アプリ内で起動時の動作を確認できます。

### ダウンロード

[azqlauncher.zip (beta0.0.3)](https://github.com/Azuk1-azq/azqlauncher/releases/download/beta0.0.3/azqlauncher.zip)

zip を展開し、`AZQLAUNCHER.exe` を起動します。

### 必要なもの

- Windows 10 / 11
- Roblox(あらかじめインストールしておく)
- ビルドする場合は [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### ソースからビルドする場合

```bash
cd AZQLAUNCHER_beta0.0.3_Complete
dotnet build -c Release
```

出力フォルダの `AZQLAUNCHER.exe` を起動します。配布用にまとめる場合:

```bash
dotnet publish -c Release -r win-x64 --self-contained false
```

初回起動時に紹介ガイドが表示されます。「はじめる」を押したあと、ダッシュボードの **Robloxを直接起動** で起動できます。

### フォルダ構成

実行ファイルと同じ場所に、次のファイルとフォルダが作られます。

| 名前 | 内容 |
| --- | --- |
| `config.json` | ランチャーの設定 |
| `Mods/` | 汎用 Mods。起動時に Roblox のバージョンフォルダへそのままコピーされます |
| `Assets/` | 内蔵のクラシックカーソル、Oof 音など |
| `Tools/` | 補助ツール用 |

ソースの構成:

```
AZQLAUNCHER_beta0.0.3_Complete/
├── AZQLAUNCHER.csproj
├── App.xaml / App.xaml.cs
├── MainWindow.xaml / MainWindow.xaml.cs
└── Assets/
    ├── classic_arrow.png
    ├── classic_arrow_far.png
    └── classic_ouch.ogg
```

### FastFlags の許可リスト

次のキーのみ適用されます。

```
DFIntTaskSchedulerTargetFps        FFlagDisablePostFx
FFlagDebugGraphicsPreferD3D11      FFlagDebugGraphicsPreferD3D11FL10
FFlagDebugGraphicsPreferVulkan     FFlagDebugGraphicsPreferOpenGL
DFFlagDebugRenderForceTechnologyVoxel
FFlagDebugForceFutureIsBrightPhase2 / Phase3
DFIntDebugFRMQualityLevelOverride  FIntRenderShadowIntensity
FFlagGlobalWindRendering           DFIntTextureQualityOverride
DFFlagTextureQualityOverrideEnabled FIntDebugForceMSAASamples
FFlagDebugDisableTelemetry* (Ephemeral / Event / Point / V2 系)
```

### 設定項目

| 設定 | 既定値 |
| --- | --- |
| ブラウザから起動時もランチャーを通さない | オフ |
| カスタムカーソルの適用を維持する | オン |
| 起動時にブラウザ連携を自動登録する | オン |
| Roblox の起動後にランチャーを自動で閉じる | オン |
| 起動前に古い ClientAppSettings をクリーンアップする | オン |

### 注意

- ブラウザ連携は `HKCU\Software\Classes` に登録します。管理者権限は不要です。
- 「ClientSettings・読み取り専用解除」ボタンで、設定ファイルの権限をリセットできます。
- 現在はベータ版です。不具合は実行ログと一緒に報告してください。

---

## English

### Features

- **FastFlags and FPS unlock**: target FPS, PostFX off, lighter render quality, and a graphics API switch (D3D11, D3D10, Vulkan, OpenGL).
- **Allowlist only**: keys written in the JSON editor are applied only if they are on the built-in allowlist. Anything else is dropped at launch and logged.
- **Mods**: mouse cursor (current Windows cursor, Roblox default, pre-2013 classic, or your own PNG), death sound (classic Oof or any audio file), skybox (bk, dn, ft, lf, rt, up) and UI font (.ttf / .otf).
- **Cursor protection**: re-applies your cursor if Roblox overwrites it after launch.
- **Browser integration**: registers the `roblox-player` and `roblox` URI schemes so the Play button on the website launches through AZQLAUNCHER.
- **Vanilla launch**: "Launch Without Launcher" deletes the settings file and starts the stock client. It can also be the default for browser launches.
- **Update-aware**: when a Roblox update creates a new version folder, flags and mods are re-applied there.
- **Japanese and English**: follows the system language, or pin one in Settings.
- **Execution log**: see what happened on each launch inside the app.

### Download

[azqlauncher.zip (beta0.0.3)](https://github.com/Azuk1-azq/azqlauncher/releases/download/beta0.0.3/azqlauncher.zip)

Unzip it and run `AZQLAUNCHER.exe`.

### Requirements

- Windows 10 or 11
- Roblox, installed beforehand
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build

### Build from source

```bash
cd AZQLAUNCHER_beta0.0.3_Complete
dotnet build -c Release
```

Run `AZQLAUNCHER.exe` from the output folder. For a distributable folder:

```bash
dotnet publish -c Release -r win-x64 --self-contained false
```

A welcome guide appears on first launch. Press **Get Started**, then use **Launch Roblox** on the dashboard.

### Files created next to the executable

| Name | Purpose |
| --- | --- |
| `config.json` | Launcher settings |
| `Mods/` | General mods, copied as-is into the Roblox version folder on launch |
| `Assets/` | Bundled classic cursors, Oof sound, etc. |
| `Tools/` | Reserved for helper tools |

### Notes

- Browser integration writes to `HKCU\Software\Classes` and needs no admin rights.
- **Reset ClientSettings & Permissions** clears read-only attributes on the settings file.
- This is a beta. Please include the execution log when reporting a bug.

---

© AZQLAUNCHER contributors. Add a LICENSE file before publishing the repository.
