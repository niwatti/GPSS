# Windows単一EXE配布

更新: 2026-11-02。ユーザー指示により、GUIとCLIをそれぞれDLL・.NETランタイムを含む単一EXEにまとめる。GUIとCLIの機能/エントリーポイントは既存のまま、各 `Gpss.Desktop.exe` / `Gpss.Cli.exe` を出力する。

## 構成

- `win-x64` 指定時に `PublishSingleFile=true`、`IncludeNativeLibrariesForSelfExtract=true` を両アプリのcsprojで有効にする。互換性アナライザーは通常ビルドでも常時有効とし、暗黙のホストOS向けRIDがlockへ混在することを避ける。
- win-x64 / self-containedでpublishし、.NET事前導入や隣接DLLを不要にする。
- `DebugType=embedded` と `DebugSymbols=true` をpublish時に参照プロジェクトへも渡す。`CopyOutputSymbolsToPublishDirectory=false` と出力検査により、別置きPDBを配布物へ残さない。
- trim/AOTと `EnableCompressionInSingleFile` は無効。既存WPF/Win32の動作を保ち、内部圧縮による起動時の展開処理を増やさない。
- 今回の.NET 10 Windows用singlefilehostはCoreCLR/JITをEXE本体へ含む。WPFのネイティブDLLは起動時にWindowsの `%TEMP%\.net` 配下へ自動展開する（`DOTNET_BUNDLE_EXTRACT_BASE_DIR` による変更可）。GUIの全DLLをメモリから実行する方式ではない。現在のCLIは追加のネイティブDLLをバンドルしない。初回の展開先は書込み可能である必要がある。
- `hid.dll` / `setupapi.dll` / `kernel32.dll` / `xinput1_4.dll` はWindowsのシステムDLLを利用する。OSのDLLを再配布しない。
- 設定、CSV、任意の性能ログは引き続き外部ファイルへ保存する。説明書・製品/依存ライセンス文書はZIPへ同梱する。

[Microsoftのsingle-file仕様](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)に従う。通常のビルドでも互換性アナライザーを有効にするため、SDKのビルド専用 `Microsoft.NET.ILLink.Tasks` が両アプリのlockへ追加される。アプリのランタイム依存として配布しない。

## 生成と更新

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

`scripts/Publish-Windows.ps1` が直列で次を行う。

1. 出力先直下の一意なstagingフォルダーへlock付きpublish。
2. 全ファイルを列挙し、目的のEXEが1件だけであることを確認。失敗時は既存の配布フォルダーを維持。
3. 成功したアプリの生成済みフォルダーを置換し、旧フォルダー配布のDLL/PDB/JSONを除去。
4. runtimeconfigはEXE内へ入るため、MSBuildのTargetDirにあるバンドル前runtimeconfigからruntime版数を得て、公式NuGet runtime/hostパッケージのライセンスをコピー。
5. 説明書、ファイルSHA256 manifest、ZIPとZIP自身のSHA256を生成。

出力は `artifacts/win-x64/desktop/Gpss.Desktop.exe`、`artifacts/win-x64/cli/Gpss.Cli.exe`、`artifacts/GPSS-win-x64.zip`。各EXEは単独で起動可能な構成とする。再配布用ZIPには関連するライセンス文書を保持する。変更前の性能改善版は `artifacts/GPSS-win-x64-before-single-file.zip` へ保全する。

## 検証（単一EXE導入時の記録）

以下のサイズ・バンドル・試験件数は導入時点の記録。後続の波形修正と最新の配布検証は[11](11-history-coverage-fix.md)を参照。

生成済み。GUIは **140,182,110byte（133.69MiB）**、CLIは **73,925,628byte（70.50MiB）**。各アプリの出力フォルダーにはEXEが1ファイルだけで、別置きDLL/PDB/JSONはない。

C#の診断プログラムで、x64 PE、bundle v6、GUI389件/CLI182件の全エントリーと範囲、内部圧縮なし、runtimeconfigを確認した。収録した全ファイルのSHA256をSDKのバンドル入力と照合した。CoreCLR/JITを含むnative hostも、ヘッダー位置8byte以外の9,991,680byteがバンドル前hostと一致した。runtimeはCore/WPFとも10.0.14。GUI/CLIの共通GPSS DLL6件が同一内容であることも確認した。

通常とpublish後のlock付き復元が成功し、全16プロジェクトのクロスビルドは警告0/エラー0。**130件の回帰テストを再実行し、全件合格**。WPF19ケースはコンパイルのみ。GUI/CLIの2つのlockにビルド専用ILLink.Tasks 10.0.14を追加し、他14件は変更していない。[診断コードと生ログ](diagnostics/single-file-2026-11-02/README.md)を保存した。

ZIPに説明書、runtime/hostライセンス、全ファイルSHA256 manifestを含め、ZIP CRCと全manifest値を照合して配布する。Windows上の実行とPowerShellスクリプト自体の実行は、このmacOS環境では未確認。従来の130件合格や性能改善の数値を単一EXE起動の受入へ転用しない。

Windowsでは.NET未導入の環境で、EXEだけを空フォルダーへコピーしてGUI/CLIを起動する。初回自己展開、2回目起動、WPF資源/HID/XInput/設定/CSV/性能ログ/終了を確認し、パスの空白・日本語を含めて実施する。起動時間と定常時の性能を分けて測定する。[Windows受入手順](windows-m6-validation.md)を参照。
