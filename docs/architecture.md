# 内部構成

[README](../README.md) · [開発環境とビルド方法](development.md)

## プロジェクト構成

`ContinuousPlayer.slnx`の主なプロジェクトと関連ディレクトリは次のとおりです。

| パス | 役割 |
|---|---|
| [`ContinuousPlayer/`](../ContinuousPlayer/) | WinUI 3アプリ。画面、入力、再生制御、Windowsとの連携 |
| [`src/ContinuousPlayer.Core/`](../src/ContinuousPlayer.Core/) | WinUIに依存しない共通処理。列挙・整列、再生状態、DLNA、HTTPストリーム、保存 |
| [`src/ContinuousPlayer.Audio.Windows/`](../src/ContinuousPlayer.Audio.Windows/) | Windows固有の音声処理。Media Foundationデコード、WASAPI排他出力、機器列挙 |
| [`docs/`](./) | 仕様、設計、開発環境とビルド方法 |

アプリはCoreとAudio.Windowsを参照し、Audio.WindowsもCoreを参照します。Coreのターゲットは`net10.0`、アプリとAudio.Windowsは`net10.0-windows10.0.26100.0`です。

主な依存ライブラリはWindows App SDK 2.5.1、CommunityToolkit.Mvvm 8.4.2、NAudio.Wasapi 2.4.0です。アプリはx64 / ARM64の単一プロジェクトMSIX構成で、開発時はWinApp CLIで登録・起動します。

## UIとWindows連携

`App.xaml.cs`が起動引数を読み取り、`MainWindow`を作成します。`MainWindow`はタイトルバー、全画面切り替え、DPIに応じたサイズ調整を担当し、`MainPage`を表示します。初期の再生領域は1024×576（16:9）で、タイトルバーと枠の分を別に確保します。タイトルバーは高さ32、追加ボタンは32×28、アイコンは16×16を基準に表示倍率へ追従します。

`MainPage.xaml`にホーム・ブラウザー・再生画面を配置し、`MainPageViewModel`に一覧行と表示用の状態を保持します。画面と再生の連携は、`MainPage`のpartial classを役割別に分けています。

| ファイル | 主な担当 |
|---|---|
| `MainPage.xaml.cs` | 初期化、画面イベント、共通のキー処理 |
| `MainPage.Browser.cs` / `MainPage.Connection.cs` | 保存済み一覧、ローカル・DLNA閲覧、手入力接続・再接続 |
| `MainPage.Player.cs` / `MainPage.Session.cs` | キュー、エンジン選択、再生・再試行、情報画面、一時中断からの復元 |
| `MainPage.Audio.cs` / `AudioSettingsDialog.xaml.cs` | 音声出力設定と保存、再生中の設定変更 |
| `MainPage.Artwork.cs` / `Playback/AlbumArtworkReader.cs` | 埋め込み・フォルダー・DLNAのジャケット取得と表示 |
| `MainPage.Input.cs` | タッチ、ポインター、操作部のフォーカス |
| `MainPage.Chrome.cs` / `MainPage.Cursor.cs` / `CursorSurface.cs` | 操作部の表示とカーソル制御 |
| `Interop/` | Windowsメディアキー、入力監視、最小化・ロック・スリープ・音声機器切断の監視 |
| `Styles/` | 配色・アイコン資源 |

Android版のSAFはWindowsのフォルダーピッカーとドライブ一覧へ、メディアセッションはWindowsのSystem Media Transport Controlsへ置き換えています。

## 再生エンジンと状態管理

共通の[`IPlaybackEngine`](../src/ContinuousPlayer.Core/IPlaybackEngine.cs)で、再生準備・再生・一時停止・停止・シーク、位置・形式・出力情報、開始・終了・失敗などのイベントを扱います。1つのエンジンインスタンスが1つのメディアを所有します。

| 実装 | 対象・処理 |
|---|---|
| `Playback/MediaPlayerPlaybackEngine.cs` | 標準再生。Windows `MediaPlayer`を`MediaPlayerElement`に接続し、映像・音声を再生 |
| `WasapiPlaybackEngine.cs` | 排他音声再生。Media FoundationでPCMへデコードし、WASAPIで指定機器へ出力 |

ファイル選択から再生までの流れは次のとおりです。

1. ブラウザーに表示された同じフォルダー直下のメディアから`PlaybackQueue`を作り、選択位置を設定します。
2. DLNAの場合は同じファイルIDを再取得し、現在の配信URLに更新します。キューの順序と件数は保持します。
3. 音声設定とメディアの内容からエンジンを選びます。排他モードでは対象音声を調査し、映像を含むファイルやUNCなどは標準再生へ渡します。
4. `PlaybackEngineSession`が旧エンジンの処理を取り消し、解放完了を待って新エンジンを準備します。準備完了後、再生状態に従って再生・一時停止を適用します。

`PlaybackSessionState`は再生意図、準備・終了・失敗の段階、一時停止と割り込み理由を管理します。一覧・情報画面を開くと一時的な復元情報を保持し、その間に割り込みが起きた場合は自動再開しません。世代と現在のエンジンを検査し、取消後や曲切り替え後の遅延通知を適用しないようにします。

`PlaybackRecovery`は実行中セッション内の確認済み位置を保持し、再試行時に復元します。`PlaybackProgress`は位置の停滞を監視します。ホーム復帰・終了時はエンジンと画面消灯抑止を解放します。

### 排他音声の処理

`PcmDecoder`がMedia Foundationを使い、受信・デコードしたデータのPCM形式を取得します。`DecodedPcmQueue`を介して、専用ワーカーがデコードとWASAPI出力を処理します。`ExclusiveEndpoint`が出力機器を初期化・解放し、`ExclusiveAudioPolicy`が出力形式の候補を判断します。

各曲のPCMレートとチャンネル数を保持し、対応機器では精度を保った整数ビット幅の拡張を行います。サーバー変換された音源も、元ファイルのタグではなく受信データのPCM形式を基準にします。

HTTP音声にはCoreの`HttpAudioStream`とWindows側の`HttpComStream`を使います。HTTP Rangeと全体サイズを取得できる配信先を対象に、ストリームごとに1 MiB単位・最大8 MiBのキャッシュを使用します。通信断は自然終了と区別し、再試行時にURLを取り直します。

## ファイル列挙とDLNA

`LocalFolders`がローカル・USBのフォルダー直下を列挙し、`MediaCatalog`が対応拡張子の判定、AppleDouble除外、OP / EDと季節フォルダーの整列を行います。`BrowserSelection`と`MediaDisplayNames`は一覧の選択復元と表示名を扱い、原名とIDを保持します。

`DlnaClient`がSSDP探索、デバイス記述の取得、ContentDirectoryの閲覧・再接続を担当します。`DlnaAddress`が手入力アドレスを正規化し、`DlnaProtocol`がSOAP / DIDL-Liteを処理します。Browseは200件単位で取得し、次ページはフィルター前の`NumberReturned`を基準にします。XMLは4 MiBまでとし、DTDと外部エンティティを禁止します。

ブラウザーの取得処理はキャンセルと要求世代で管理します。取得中も前の一覧・選択を保持し、取消後の応答で画面を戻さないようにします。DLNAの配信URLは保存せず、再生開始・次送り・再試行ごとに再取得します。

## 保存と診断ログ

保存先は`ApplicationData.Current.LocalFolder`です。

| ファイル | 内容 |
|---|---|
| `library.json` | `LibraryStore`が管理する登録フォルダー、最後の登録先・ファイルID・再生元階層。DLNAはサーバーID・フォルダーID・デバイス記述URLを保存 |
| `audio-settings.json` | `AudioOutputSettingsStore`が管理する音声モードと出力機器。フォルダー登録とは独立した形式 |
| `last-error.log` | 未処理例外・起動例外の記録 |

設定の書き込みは各Storeで直列化し、一時ファイルから置き換えます。再生位置・プレイリストのプロセス終了後の復元は行わず、一時的な位置復元は実行中セッション内で扱います。

`--audio-log`指定時は`Playback/AudioPlaybackLog.cs`がJSONL形式で再生セッションと排他音声の診断情報を指定先に記録します。
