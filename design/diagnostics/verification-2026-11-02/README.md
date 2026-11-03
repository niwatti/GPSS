# 追加変更後の検証資料

2026-11-02。macOS 14.6.1 ARM64、.NET SDK 10.0.403、runtime 10.0.14。結果の解釈と未確認事項は[試験記録](../../15-verification-2026-11-02.md)を参照する。

## 再現方法

依存は各projectのlockで固定。ここではNuGetの監査通信を無効にしてキャッシュから復元しており、依存の脆弱性監査ではない。全て直列で実行し、外付けSMBボリュームでのビルド共有を避けた。VSTestはローカルのtesthost通信を許可した環境で実行する。

```sh
dotnet restore Gpss.sln --locked-mode --disable-parallel --disable-build-servers -m:1 -p:NuGetAudit=false
dotnet build Gpss.sln --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false
for part in Core HidDirect XInput Application Cli; do
  dotnet vstest "tests/Gpss.$part.Tests/bin/Debug/net10.0/Gpss.$part.Tests.dll" /Logger:trx
done
dotnet build tools/Gpss.Benchmarks/Gpss.Benchmarks.csproj -c Release --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false
for pattern in quantized random16 mixed fragmented; do
  dotnet tools/Gpss.Benchmarks/bin/Release/net10.0/Gpss.Benchmarks.dll --seconds 300 --pattern "$pattern"
done
dotnet tools/Gpss.Benchmarks/bin/Release/net10.0/Gpss.Benchmarks.dll --pipeline --seconds 300
```

仮想負荷は各パターンで1k/8k/100Hz・4軸を実行する。ヒストグラムの上限、量子化入力の全件計数、欠測境界の除外を毎解析で検査する。各virtual_summaryのfinalHistogramCounts／finalHistogramBinsで最終分布を確認できる。解析と追加／失効は別の計時。明示GCを含むためgcPauseMsを実機の停止率として扱わない。

実時間負荷はmock XInput→本体Controllerを通常／1ms設定で動かし、17msごとに表示スナップショットを参照する。pipeline_intervalのhistogramCount／histogramBinCountを記録する。実際の呼出し／状態更新Hzは測定値であり、1kHz実機の保証ではない。CPU比は全論理CPU比。WPF描画とネイティブ入力は含まない。5秒ごとのキュー観測から瞬間的な最大値や破棄0を断定しない。

## ファイル

- `test-summary.json` と各suiteの最終TRX／ログ: 161件合格、失敗0・スキップ0。WPF19件はコンパイルのみ。
- `verification-application-initial.*`: 10Hz解析周期に対し1ms後の更新を期待した新テストの初回失敗。時刻設定だけを修正した最終31件合格と区別する。
- `verification-benchmark-*.jsonl`: 仮想300秒×12条件。8kHz fragmentedはp99約159msで性能目標未達。
- `verification-pipeline-300.jsonl`: 実時間300秒のモック。本番の入力・描画性能とは区別する。
- `verification-bundle-validation.json`: [InspectBundle](../single-file-2026-11-02/README.md)で現行GUI/CLIの全収録ファイルとpre-bundle hostをSHA256照合した結果。EXEのWindows起動試験ではない。
- `cleanup-validation.json`: 削除対象7パスが存在せず、保持したCSV22件とfixture1件のSHA256が削除前と一致。
- `verification-locks-before.json`／`verification-locks-after.json`: lock16件の同一性。
- `source-sha256.json`: 今回の本体・試験・ベンチマーク・project・lockのハッシュ。bin/objは含めない。

アプリ本体は試験によって変更していない。配布EXEはヒストグラム実装時のものを照合し、最新の設計・試験記録をZIPへ反映する。PowerShell配布スクリプト自体とWindows単一EXEの自己展開は未実行。
