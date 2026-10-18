# ContinuousPlayer for Windows

Android版の仕様・Kotlinコード・デザイン資産をもとにした、WinUI 3製の動画・音声連続再生アプリです。濃紺・シアン・紫のホーム画面、ファイルブラウザー、一時停止ツールバーをAndroid版に合わせています。

## 機能

- PC・USBのフォルダーを登録し、独自ブラウザーからファイルを選択
- 同一LAN上のNAS / DLNAサーバーをSSDPで探索し、公開フォルダーを閲覧・登録
- 選択したファイルから同じフォルダー直下を連続再生（末尾で終了、リピートなし）
- `作品 OP → ED → OP2 → ED2` の整列、季節フォルダーの年・冬春夏秋順
- タッチの左右・中央タップ、横スワイプ、キーボード、Windowsメディアキー
- 再生元フォルダーへの復帰、現在のファイルへのフォーカス復元
- フォルダー登録と最後の選択位置の保存、オフラインでの登録解除
- 再生エラーの表示・再試行、メディア情報、全画面再生

列挙する形式: `.mp4 .m4v .mp3 .flac .m4a .aac .wav .ogg .opus`。`._`で始まるAppleDoubleファイルは除外します。デコードできる形式はWindowsのコーデック構成にも依存します。

## ビルド・起動

Windows 10 1809以降 / Windows 11、.NET 10以上のSDK、WinApp CLI 0.7.1以上、開発者モードが必要です。現在の開発環境では.NET SDK 11 RCを利用し、アプリのターゲットは.NET 10です。通常のJITビルドにC++ツールチェーンは不要です。

```powershell
winget install --id Microsoft.WinAppCli --exact
dotnet build .\ContinuousPlayer.slnx -p:Platform=x64
winapp run .\ContinuousPlayer --detach
```

Visual Studioでは `ContinuousPlayer.slnx` を開き、x64またはARM64を選んでください。起動時に自動でフォルダーピッカーは開きません。「OP / EDを選ぶ」→「フォルダーを追加」から始めます。登録後はファイルを選択すると再生します。登録解除は保存済み一覧で選択後にDelete、右クリック、長押し、または登録解除ボタンを使います。実ファイルは削除しません。

## 操作

| キー / タッチ | 動作 |
|---|---|
| Space / Enter、中央タップ | 再生 / 一時停止 |
| ← / → | 前 / 次のファイル |
| ↑ / ↓ | 10秒巻き戻し / 早送り |
| 左 / 右1/3のタップ | 10秒巻き戻し / 早送り |
| 右 / 左スワイプ | 前 / 次のファイル |
| Esc / O | 再生停止後、再生元フォルダーを表示 |
| H | ホームへ戻る |
| I | 再生情報 |
| F11 | 全画面の切り替え |
| Tab / Shift+Tab | ボタン間の移動 |
| Enter（一時停止ツールバー内） | フォーカス中の操作 |
| F5（ブラウザー内） | 再読み込み |
| Backspace / Alt+←（ブラウザー内） | 親へ移動 |
| Esc（ブラウザー内） | キャンセル（取得中も可能） |

再生画面では全画面に切り替わり、一時停止すると上部ツールバーが表示されます。ウィンドウが非アクティブになると一時停止します。Windowsの音量キーはOSに任せます。

動画再生中は、マウス操作が3秒間ないとカーソルを非表示にします。マウスを動かすかクリックすると再表示されます。一時停止中、情報表示中、音声のみの再生、ホーム・フォルダー画面では非表示にしません。

## 検証

```powershell
dotnet run --project .\tests\ContinuousPlayer.Core.Tests
.\scripts\New-TestMedia.ps1  # UI検証用の無音WAVを生成
.\scripts\Test-UI.ps1       # ローカル画面を自動操作（テスト用登録は終了時に解除）
# カーソル検証: 60秒以上の動画1本のみを置いた専用フォルダーと、起動中アプリのPIDを指定
.\scripts\Test-Cursor.ps1 -AppPid 1234 -VideoPath C:\TestVideo\sample.mp4
```

共通ロジックのテストは追加テストフレームワークなしで実行できます。整列・列挙・キュー境界・永続化・XML・ローカルHTTPサーバーによるSOAP/ページ分割を検証します。NAS実機での探索・再生と、物理タッチパネルでの確認は別途必要です。

## Windows版の範囲

再生にはWinUIの`MediaPlayerElement`とWindows `MediaPlayer`を使用します。AndroidのSAFはWindowsのフォルダーピッカーへ置き換えています。USBはOSにマウントされたドライブが対象です。AndroidのUSB Preferred Mixer Attributesに相当するビットパーフェクト設定、WASAPI排他出力、Android TV固有APIは実装していません。音声はWindows標準ミキサー経由です。再生位置・プレイリストのプロセス終了後の復元、バックグラウンド再生サービスも対象外です。

DLNAは公開されたContentDirectoryを読み取ります。NASのログイン、SMB、DTCP-IP、キャストには対応しません。保存にはサーバーIDとフォルダーIDを使い、再接続時にURLを取得し直します。

詳細は[Windows版仕様書](docs/spec.md)を参照してください。再生APIは[MicrosoftのMediaPlayer資料](https://learn.microsoft.com/en-us/windows/apps/develop/media-playback/play-audio-and-video-with-mediaplayer)に基づいています。
