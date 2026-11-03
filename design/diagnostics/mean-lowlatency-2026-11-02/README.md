# 平均bit方式・低遅延固定の検証資料

2026-11-02、macOS 14.6.1 ARM64 / SDK 10.0.403 / runtime 10.0.14。[現行設計](../../16-mean-precision-low-latency.md)参照。

- `mean-lowlatency-build.log`: 全16プロジェクト、WPF本体と19ケースの試験を含めてクロスビルド。警告0・エラー0。
- `mean-lowlatency.trx`／`mean-lowlatency-tests.log`: Core89、HID21、XInput14、Application32、CLI13、計169ケース合格。WPFは未実行。
- `mean-lowlatency-application-final.*`: Controllerでも旧Normal指定をLowLatencyへ固定する検査を加えた最終Application32ケース。全169ケースのうちApplicationの結果をこの最終実行で更新する。
- `mean-lowlatency-{quantized,random16}.jsonl`: 各300秒相当、100／1k／8kHz、4軸。入力追加／失効はappend、統計・平均bit・投影・分布snapshotはanalyzeに計時する。計測用の明示GCを含む。
- `verification.json`: 回帰件数と6条件の仮想負荷の集計。Windows・実ブラウザーの実行はfalse。
- `mean-lowlatency-publish-*.log`: lock付きwin-x64自己完結・単一EXEの生成。既存の依存lock16件は変更しない。
- `bundle-validation.json`: [InspectBundle](../single-file-2026-11-02/README.md)で全収録ファイルとpre-bundle hostを照合した結果。
- `source-sha256.json`: ソース・試験・project・lockと会話用プレビューのハッシュ。

再現は `scripts/Test-CSharp.sh`、Windowsでは `scripts/Test-CSharp.ps1 -IncludeDesktop`。負荷はReleaseで `dotnet tools/Gpss.Benchmarks/bin/Release/net10.0/Gpss.Benchmarks.dll --seconds 300 --pattern quantized` と `--pattern random16` を直列で実行する。現在のpipeline計測器もLowLatency設定へ変更したが、今回のログには低遅延の実時間5分測定を含めない。

会話用プレビューは構文・静的検査のみ。ブラウザーアクセスホストが利用できず、描画・注釈送信を実ブラウザーで確認できなかった。実WPFや8kHz機器・Windows単一EXEの初回展開も未確認。以前のfragmented性能目標未達は未解決のまま残す。

旧配布は `artifacts/GPSS-win-x64-before-mean-bit.zip` に保全し、新版は `artifacts/GPSS-win-x64.zip`。ZIP自体の検査結果は循環ハッシュを避けて配布ZIP外の `package-validation.json` に記録する。
