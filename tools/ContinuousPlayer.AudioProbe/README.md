# AudioProbe（Hi-Res 第1・3・4・5段階）

Windows用コンソール。第1段階の独立した検証と、第3段階の本番エンジン検証を含む。NAudio.Wasapi / NAudio.Core **2.4.0**と、WindowsにインストールされたMedia Foundationコーデックを使用する。アプリの設定・既定出力・Windowsの共有モード形式は変更しない。

## 実行

リポジトリのルートからPowerShellで実行する。

```powershell
$project = '.\tools\ContinuousPlayer.AudioProbe'
dotnet restore $project --locked-mode
dotnet build $project --no-restore
dotnet run --project $project --no-build -- list --output .tools/audio-stage1/devices.json
```

`Devices`の`Id`をコピーして選択する。名前の部分一致や既定機器への自動フォールバックは行わない。以下の`$deviceId`には**実際のID**を入れる。

```powershell
$deviceId = '{0.0.0.00000000}.{実際のエンドポイントID}'

# 6レート × モノ/ステレオ × 7形式。各試行で排他初期化→解放する。
dotnet run --project $project --no-build -- matrix --device $deviceId --output .tools/audio-stage1/matrix.json

# 無音の排他ストリームを開始する。本体のレート表示を目視する。
# 排他利用中は同じ機器を他のアプリが利用できない場合がある。
dotnet run --project $project --no-build -- switch --device $deviceId --bits 32 --seconds 10 --output .tools/audio-stage1/switch-32.json
dotnet run --project $project --no-build -- switch --device $deviceId --bits 16 --rates 44100,48000,88200,96000,176400,192000 --seconds 8 --output .tools/audio-stage1/switch-16.json

# 有効24-bit / 格納32-bitを明示して確認する場合（BTR17の今回の構成は非対応）
dotnet run --project $project --no-build -- switch --device $deviceId --bits 24 --container 32 --seconds 5 --output .tools/audio-stage1/switch-24-in-32.json

# PCM境界値・可逆なビット幅拡張・不正変換の拒否・形式構造体の検証
dotnet run --project $project --no-build -- self-test
```

`matrix`は`IsFormatSupported`で受理された形式のみ、**同一形式のまま**`IAudioClient.Initialize`まで確認する。16/24/32-bit PCMのWAVEFORMATEX・WAVEFORMATEXTENSIBLE、および有効24-bit/格納32-bitを別々に記録する。非対応は非対応のまま記録し、レート・チャンネル数・ビット幅の代替を勝手に選ばない。

`switch`は音源ファイルを再生せず、WASAPIにSilentフラグ付きのバッファを供給する。100 msを基準にイベント駆動ストリームを開き、ドライバーのバッファ整列要求時だけクライアントを再作成する。開始前にプリフィルし、ハードウェアクロックで指定時間までの出力を確認する。イベント待ちは2秒、クロック進行には指定時間+5秒の上限を設ける。各レートの終了後に排他資源を解放する。

## デコード検証

```powershell
# 専用ディレクトリへ2秒の非無音WAVとWindows標準エンコーダーによるFLAC/AACを生成
dotnet run --project $project --no-build -- fixtures --directory .tools/audio-stage1/media
dotnet run --project $project --no-build -- decode --input .tools/audio-stage1/media --output .tools/audio-stage1/decode.json
# 既存ファイル1本も指定可能（ローカルのみ、ディレクトリは直下のみ）
dotnet run --project $project --no-build -- decode --input 'C:\Audio\sample.flac' --output .tools/audio-stage1/sample.json
```

`fixtures`の出力先は生成音源用の専用ディレクトリを指定する。同名の生成音源は上書きする。外部プログラムのダウンロードは不要。圧縮形式を生成できない環境では失敗を表示して終了コード1を返す。ファイル名の`16`/`24`は**基準WAVのビット数**を示す。今回のWindows FLACエンコーダーは16-bit WAVからも24-bit FLACを生成したため、名前を実際の入力形式とみなさない。AACは44.1/48 kHzの16-bit基準WAVから生成する。

デコードはNAudioの公開Media Foundation相互運用APIでSource Readerを使い、ストリームのMajorType/SubType/レート/チャンネル数/ビット数を取得する。映像ストリームを含むファイルは拒否する。拡張子はディレクトリ列挙の絞り込みだけに使う。

PCMだけを要求する試行と、ネイティブのPCMビット数（不明なら32-bit）を要求する試行を別に行う。レートを指定せず、デコーダーが実際に返すPCM形式を記録する。Source Readerの音声処理・リサンプラーは有効化しない。HE-AAC等でネイティブ値と出力が異なる場合も、両者を独立して記録する。形式変更通知が来た場合は停止する。

全PCMのフレーム数・SHA-256を求める。WAV自身、同名WAVが隣にあるFLAC、`name.alac.m4a`に対する`name.wav`を比較対象にできる。比較で許すのは同じレート・チャンネル数での整数PCMビット幅の拡張だけ。PCM不一致は終了コード1になる。AAC等の非可逆圧縮はサンプル一致の合否に使わない。これらの音源はDACには出力しない。

## ログと限界

- 入力形式は`InputStreams`、デコード結果は`DecodedPcm`、排他初期化引数は`Initialized`、共有ミックス形式は`SharedMixFormat`。各値を混同しない。
- ログの`DacDisplay`は常に`unverified`。目視結果は人による別記録に残す。
- 終了コード: 0=完了（matrixの非対応形式を含む）、1=失敗、2=switchで非対応形式あり、130=取消。Ctrl+Cは操作間・無音ストリームのイベント待ち・デコードのサンプル間で確認する。ネイティブAPI呼び出しそのもののハングを強制中断する機能はない。
- 第1段階の`switch`/`decode`は無音の出力形式切り替えと独立したデコードを検証する。音源PCMのリアルタイム供給は下記の第3段階コマンドで確認する。ギャップレス、物理出力のビットパーフェクト、機器取り外しの受入は別途必要。
- 実機結果・採用判断は[第1段階の検証記録](../../docs/audio-sample-rate-stage1-validation.md)を参照。

```powershell
# ARM64はx64機でも発行できるが、実行の確認にはARM64実機が必要
dotnet publish $project -c Release -r win-arm64 --self-contained false -o .tools/audio-stage1/arm64
```

## 第3段階の本番エンジン検証

`engine`はWinUIアプリと同じ`WasapiPlaybackEngine`を使用し、**実際のデコードPCMをDACへ出力**する。検証用音源はほぼ無音で、WAV/FLACの先頭と末尾に±1 LSBの目印がある。生成先には専用フォルダーを指定する。同名ファイルを上書きする。

```powershell
dotnet run --project $project -- engine-fixtures --directory .tools/audio-stage3/media --seconds 8
dotnet run --project $project --no-build -- engine --input .tools/audio-stage3/media --device $deviceId --output .tools/audio-stage3/playback.json
dotnet run --project $project --no-build -- engine --input .tools/audio-stage3/media --device $deviceId --exercise true --output .tools/audio-stage3/exercise.json
# 以下のモノラル拒否の期待値はBTR17の第1段階の実測に依存する
dotnet run --project $project --no-build -- engine-lifecycle --input '.tools/audio-stage3/media/01 44.1k.wav' --device $deviceId --output .tools/audio-stage3/lifecycle.json
```

4曲は44.1 kHz/16-bit WAV → 96 kHz/24-bit FLAC → 192 kHz/24-bit WAV → 44.1 kHz AAC M4A。`engine`は全曲の排出完了をハードウェアクロックで確認し、WAV/FLACの出力PCMを参照WAVとフレーム数・SHA-256で比較する。AACは非可逆なので一致判定の対象外。`--exercise true`は一時停止、停止中の中間位置へのシーク、再開を確認する。この実行には全曲ハッシュ比較を適用しない。

`engine-lifecycle`は選択機器不在、BTR17でのモノラル拒否、float WAV拒否、準備前/中の取消、12回の実エンジン交換、ログ無効時の再生、停止中に別エンジンで同じ機器を初期化できることを確認する。任意の`--video LOCAL_MP4`で、M4A拡張子でも映像を検出する検証も追加できる。

入力・デコードPCM・排他初期化成功形式・機器IDを独立して記録する。DAC本体表示の確認は別途必要。送出直前のPCM比較であり、DAC内部処理やアナログ出力のビットパーフェクトは保証しない。[第3段階の検証記録](../../docs/audio-sample-rate-stage3-validation.md)も参照。

## 第4段階の追加形式

`engine-extra-fixtures`は指定した整数PCM WAVからWindowsのMedia FoundationエンコーダーでMP3とADTS AACを生成する。生成先は専用フォルダーとし、同名ファイルは上書きする。外部プログラムは実行しない。

```powershell
dotnet run --project $project -- engine-extra-fixtures --input '.tools/audio-stage3/media/01 44.1k.wav' --directory .tools/audio-stage4/generated
dotnet run --project $project --no-build -- engine --input .tools/audio-stage4/generated --device $deviceId --output .tools/audio-stage4/compressed-playback.json
dotnet run --project $project --no-build -- engine --input .tools/audio-stage4/generated --device $deviceId --exercise true --output .tools/audio-stage4/compressed-seek.json
```

`engine`のディレクトリ対象は既存の7音声拡張子へ拡張した。OGG/Vorbis・OpusもWindowsのMedia Foundationが対応する場合に限る。今回の環境では両方とも`0xC00D36C4`で拒否された。未対応を標準再生への自動切り替えで隠さず、追加デコーダーも同梱しない。対応表と取得した検証用サンプルの出典・ハッシュは[第4段階の検証記録](../../docs/audio-sample-rate-stage4-validation.md)を参照。

## 第5段階のHTTP/DLNA検証

```powershell
dotnet run --project $project -- http-engine --directory .tools/audio-stage3/media --device $deviceId --output .tools/audio-stage5/network.json
# 保存済み登録を読み取り、実NASへ接続する。--playがなければ音声は出力しない
dotnet run --project $project -- nas-inspect --library 'アプリのLocalStateパス' --name mora --output .tools/audio-stage5/nas.json
# 音声を含む子フォルダーを限定して探索し、短時間の再生・停止中シーク・再開を行う
dotnet run --project $project -- nas-inspect --library 'アプリのLocalStateパス' --name mora --play true --device $deviceId --output .tools/audio-stage5/nas-playback.json
```

`http-engine`は拡張子のないURLを返す制御DLNA/HTTPサーバーを立ち上げ、本番エンジンでWAV/FLAC/M4Aを再生する。入力情報/実PCM/排他初期化形式を別々に記録し、WAV/FLACは送出PCMを参照WAVと照合する。遅延、切断、世代付きURLの失効と再取得、確認済み位置復元、停止中/再生中のシーク、取消、Range非対応も検証する。出力先機器は変更しない。

`nas-inspect --play true`は実音声が出る。元の音源・登録データは変更しない。`--spread true`で複数の子フォルダーから選び、`--skip`/`--limit`で検証数を限定できる。詳細ログはURLや曲名を含むため、生成物は`.tools`等の診断用ディレクトリに置く。実NASの接続失敗は各結果の`Status`/`Error`に記録する。コンソール終了コードだけでは成功と判断しない。

## 第6段階の連続再生・負荷測定

```powershell
# 16秒の4曲を用意した専用ディレクトリを指定する
dotnet run --project $project --no-build -- soak --directory .tools/audio-stage3/ui-media --device $deviceId --output .tools/audio-stage6/soak.json --minutes 10
```

`soak`はほぼ無音の44.1 kHz/16-bitステレオWAVを生成し、ローカル10分 → 4曲のレート切り替え → HTTP10分 → 4曲の切り替えを本番エンジンで実行する。`--minutes`は1〜60分で、各経路の連続再生時間に適用する。DACを他の検証と同時に使用しない。

各曲のハードウェアクロックの前進、末尾までの排出、WAV/FLACの全送出PCMハッシュを確認する。自然終了後は別エンジンで同じDACを初期化し、解放を検証する。AACは非可逆形式のためハッシュ一致の対象外。アナログ出力の先頭/末尾やクリック音は別途聴取が必要。

1秒ごとに負荷を測定し、約10秒ごとのメモリー・ハンドル・スレッド数と各曲のCPU平均をJSONへ保存する。CPUの100%は論理CPU 1個分で、マシン全体に対する値も併記する。数値は診断用ハッシュ、参照照合、HTTPサーバー、メモリー上の配信用音源を含む検証プロセス全体の値で、WinUIアプリ単体の負荷ではない。初回クロック観測時間は1秒間隔の測定上限であり、DACの再同期時間ではない。途中結果の`Status: running`を成功と扱わず、終了コード・`Status: passed`・10曲の結果を確認する。
