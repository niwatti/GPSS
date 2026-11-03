# 30秒波形保持の再現・検証資料

2026-11-02、macOS 14.6.1 ARM64 / SDK 10.0.403 / runtime 10.0.14。Windows入力・WPFの実行試験ではない。[原因と修正](../../11-history-coverage-fix.md)参照。

`history-before.trx` と `history-before-tests.log` は修正前の4件の再現失敗。1k/8kで30秒の原データが残っていても8192点の新着だけになることと、汎用縮約の端点消失を確認した。`history-fixed.trx` は最終135件すべて合格。`history-initial-validation*` は途中で既存XInput試験の待機競合が出た記録で、最終結果とは区別する。

```sh
dotnet build Gpss.sln --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false
sh scripts/Test-CSharp.sh
dotnet build tools/Gpss.Benchmarks/Gpss.Benchmarks.csproj -c Release --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false
dotnet tools/Gpss.Benchmarks/bin/Release/net10.0/Gpss.Benchmarks.dll --seconds 60 --pattern quantized
dotnet tools/Gpss.Benchmarks/bin/Release/net10.0/Gpss.Benchmarks.dll --seconds 60 --pattern fragmented
```

`fragmented` は毎回continuityを変える256段階波形。1k/8k/100Hz、4軸、各600回の解析。`maxHistoryCoverageLossSeconds` は原データの保持期間から投影の保持期間を引いた最大値で、最終全条件0秒。内部の省略点や実機の欠落0を意味しない。取得上限flagと描画省略flagも分離して記録する。タイマー外で生存ヒープ計測用GCを実施するため、仮想負荷のGC合計にはその診断用GCも含む。

`*-uncached.jsonl` は全区間再走査の中間案で、現行版の性能ではない。最終の `history-benchmark-{quantized,fragmented}.jsonl` を使用する。旧版の配布は `artifacts/GPSS-win-x64-before-history-fix.zip` に保存した。

単一EXEのバンドル検査は[InspectBundle](../single-file-2026-11-02/README.md)を使用する。`history-bundle-validation.json` は新版の全収録ファイルとhostの照合結果、`source-sha256.json` は今回のソースと依存lockのハッシュ。ビルド/試験/負荷測定は同時に走らせていない。
