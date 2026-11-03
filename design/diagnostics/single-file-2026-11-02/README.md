# 単一EXEの静的検査

macOS ARM64上でWindowsのEXEを実行せずに検査する診断用C#プログラム。製品の依存やGpss.slnには追加しない。SDK 10.0.403 / .NET 10.0.14を使用した。[配布仕様](../../10-single-file-distribution.md)参照。

リポジトリ直下から、publish後に次を実行する。

```sh
dotnet run --project design/diagnostics/single-file-2026-11-02/InspectBundle.csproj -c Release --disable-build-servers -p:UseSharedCompilation=false -- artifacts/win-x64/desktop/Gpss.Desktop.exe src/Gpss.Desktop/bin/Release/net10.0-windows/win-x64 src/Gpss.Desktop/obj/Release/net10.0-windows/win-x64/singlefilehost.exe artifacts/win-x64/cli/Gpss.Cli.exe src/Gpss.Cli/bin/Release/net10.0/win-x64 src/Gpss.Cli/obj/Release/net10.0/win-x64/singlefilehost.exe
```

各EXEのフォルダーが1ファイルだけであること、x64 PE、.NET bundle v6の署名/ヘッダー/全エントリー範囲、パス重複、圧縮なし、self-contained runtimeconfig、GPSS/WPF依存を検査する。全収録ファイルのSHA256をバンドル前の入力と比較する（通常のDLL/設定はbin、single-file向けにSDKが書き換えたdeps.jsonはobjのpublish用出力）。Native singlefilehostはCoreCLR/JITを含み、個別のcoreclr.dll/clrjit.dllエントリーはないため、ヘッダー位置8byte以外のhost全体をSDKが作ったバンドル前hostと照合する。全ファイルを1つの巨大な配列へ展開しない。

出力JSONには各EXEと全収録ファイルのSHA256、件数、runtime版数、host検証byte数を保存する。これはWindows起動・自己展開・ネイティブAPI・WPF描画試験ではない。PowerShell配布スクリプトはmacOSでは実行せず、同じコマンド/コピー/ZIP操作を代替実行した。

仕様の参照元: [.NET single-file](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)、[Manifest](https://github.com/dotnet/runtime/blob/v10.0.0/src/installer/managed/Microsoft.NET.HostModel/Bundle/Manifest.cs)、[FileEntry](https://github.com/dotnet/runtime/blob/v10.0.0/src/installer/managed/Microsoft.NET.HostModel/Bundle/FileEntry.cs)、[Bundler](https://github.com/dotnet/runtime/blob/v10.0.0/src/installer/managed/Microsoft.NET.HostModel/Bundle/Bundler.cs)。

生ログは `single-file-bundle-validation.json`、`single-file-publish-{desktop,cli}.log`、`single-file-build.log`、`single-file-locked-restore-after-publish.log`、`single-file.trx`。GUI/CLIのlockへビルド専用ILLink.Tasks 10.0.14を追加し、他14件のlockは変更していない。過去の性能測定に付属するsource-sha256はその測定時点の記録として保持している。
