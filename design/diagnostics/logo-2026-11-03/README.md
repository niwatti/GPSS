# ロゴ・アイコンの検証資料

2026-11-03、macOS 14.6.1 ARM64 / .NET SDK 10.0.403 / runtime 10.0.14。[設計](../../18-logo-and-icons.md)と[生成指示・元画像](../../../assets/branding/README.md)参照。

- `asset-validation.json`: 元PNGとICOのハッシュ、透過、9サイズの確認。
- `logo-icon-sizes.png`: 実際のICOから16〜64pxを抽出した表示と、同じ画素を拡大した検査用画像。
- `logo-build.log`: 全16プロジェクトのクロスビルド、警告0・エラー0。WPF19ケースはアイコン確認を含めてコンパイルのみ。
- `logo-cli.trx`／`logo-cli-tests.log`: ApplicationIcon設定後のCLI13ケース合格。
- `logo-publish-*.log`: GUI／CLIそれぞれのwin-x64単一EXE生成。
- `VerifyBranding.csproj`／`Program.cs`: ポータブルなPE・WPF資源検査。元ICOの9フレームを、EXEの全RT_ICONと唯一のRT_GROUP_ICONへ照合し、Desktop.g.resources内のassets/gpss.icoも同じバイト列であることを検査する。
- `branding-validation.json`: 上記検査の結果。単にファイルの存在だけを確認するものではない。
- `bundle-validation.json`: 既存の[InspectBundle](../single-file-2026-11-02/README.md)で全収録ファイルとpre-bundle hostを照合した結果。
- `source-sha256.json`: ソース・project・lock・画像・ICO・配布スクリプトのハッシュ。依存lock16件は変更なし。

```sh
dotnet build Gpss.sln --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false
dotnet build design/diagnostics/logo-2026-11-03/VerifyBranding.csproj -c Release --disable-build-servers -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false
dotnet design/diagnostics/logo-2026-11-03/bin/Release/net10.0/VerifyBranding.dll assets/branding/gpss.ico src/Gpss.Desktop/bin/Release/net10.0-windows/win-x64/Gpss.Desktop.dll TestResults/logo-staging/desktop/Gpss.Desktop.exe TestResults/logo-staging/cli/Gpss.Cli.exe
```

ロゴは内蔵image_genで生成した。Pillowによる処理はICOへの形式変換・リサイズと検査用画像だけであり、実行やC#ビルドのPython依存は追加していない。

Windowsの実起動、WPFのICO decoder実行、各DPIのタイトルバー・タスクバー・Alt+Tab表示、PowerShell配布スクリプト自体は未実行。PEとWPF資源の静的検査をWindows実表示の受入済みとは扱わない。

新しい生成済み成果物は `artifacts/win-x64-logo/`、ZIPは `artifacts/GPSS-win-x64.zip`。旧ZIPは `artifacts/GPSS-win-x64-before-logo.zip` に保全した。ZIP自身のハッシュと検査は循環参照を避けてZIP外の `package-validation.json` に記録する。
