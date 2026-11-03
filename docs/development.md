# 開発環境とビルド方法

[README](../README.md) · [内部構成](architecture.md)

## 開発環境

- Windows 10 1809以降 / Windows 11
- .NET 10以上のSDK
- WinApp CLI 0.7.1以上
- Windowsの開発者モード

アプリのターゲットは.NET 10、Windows SDK 26100、最低OSバージョン17763です。`global.json`ではSDK 10.0.100を基準に、上位メジャーへのロールフォワードとプレビュー版を許可しています。現在の開発環境では.NET SDK 11 RCを利用しています。通常のJITビルドにC++ツールチェーンは不要です。

## ビルド・起動

リポジトリのルートからPowerShellで実行します。

```powershell
winget install --id Microsoft.WinAppCli --exact
dotnet build .\ContinuousPlayer.slnx -p:Platform=x64
winapp run .\ContinuousPlayer --detach
```

Visual Studioでは`ContinuousPlayer.slnx`を開き、x64またはARM64を選びます。Releaseビルドは次のとおりです。

```powershell
dotnet build .\ContinuousPlayer.slnx -c Release -p:Platform=x64
# ARM64向けのクロスビルド
dotnet build .\ContinuousPlayer.slnx -c Release -p:Platform=ARM64
```

ReleaseはJIT / ReadyToRunを使用します。アプリ起動後のフォルダー登録・再生手順は[READMEの使い方](../README.md#使い方)を参照してください。

## 自己完結型でビルドする

.NETランタイムとWindows App SDKを同梱するには、`SelfContained=true`と`WindowsAppSDKSelfContained=true`の両方を指定します。`--self-contained true`は.NET側の指定です。Windows App SDK側も同梱する必要があります。[Microsoftの自己完結型配置ガイド](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)

リポジトリのルートから、対象アーキテクチャを指定して発行します。

```powershell
# x64
dotnet publish .\ContinuousPlayer\ContinuousPlayer.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:WindowsAppSDKSelfContained=true -p:PublishSingleFile=false -o .\ContinuousPlayer\bin\self-contained\win-x64

# ARM64
dotnet publish .\ContinuousPlayer\ContinuousPlayer.csproj -c Release -r win-arm64 --self-contained true -p:Platform=ARM64 -p:WindowsAppSDKSelfContained=true -p:PublishSingleFile=false -o .\ContinuousPlayer\bin\self-contained\win-arm64
```

発行先はそれぞれ`ContinuousPlayer/bin/self-contained/win-x64/`、`ContinuousPlayer/bin/self-contained/win-arm64/`です。実行ファイルと依存DLLなどを含む複数ファイルで発行します。自己完結型のJIT / ReadyToRun発行にC++ツールチェーンは不要です。

### 配布用MSIXを作成する

このアプリはパッケージIDを使うMSIX構成です。発行先のEXEを単独で配布・起動する手順ではなく、配布時はMSIXを作成してインストールします。

```powershell
# .NETとWindows App SDKを同梱した、署名前のx64パッケージを作成
winapp package .\ContinuousPlayer\ContinuousPlayer.csproj --arch x64 --self-contained -p SelfContained=true --no-sign

# ARM64向け
winapp package .\ContinuousPlayer\ContinuousPlayer.csproj --arch arm64 --self-contained -p SelfContained=true --no-sign
```

MSIXの生成先はコマンドの出力に表示されます。`winapp package`の`--self-contained`はWindows App SDK側だけを指定するため、.NET側の`-p SelfContained=true`も渡します。[WinApp CLIのパッケージ作成手順](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md)

`--no-sign`で作成したMSIXは、インストール前に署名が必要です。署名証明書を用意している場合は、`--no-sign`を`--cert .\devcert.pfx`に置き換えると署名付きで作成できます。証明書のSubjectは`Package.appxmanifest`のPublisher（現在は`CN=AppPublisher`）と一致させ、インストール先で信頼される証明書を使用します。
