# 性能改善後の再現資料

2026-11-01〜02、macOS 14.6.1 ARM64 / .NET SDK 10.0.403 / runtime 10.0.14 / Workstation GC / 8論理CPU。製品コードはR1〜R3改善後。Windows入力APIとWPFを実行した結果ではない。実装・解釈は[09](../../09-performance-implementation.md)。

リポジトリ直下から実行する。ビルド/試験/前後の負荷測定を同時に走らせない。

```sh
dotnet restore Gpss.sln --locked-mode --disable-parallel --disable-build-servers -m:1
dotnet build tools/Gpss.Benchmarks/Gpss.Benchmarks.csproj -c Release --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false
dotnet tools/Gpss.Benchmarks/bin/Release/net10.0/Gpss.Benchmarks.dll --seconds 600 --pattern quantized
dotnet tools/Gpss.Benchmarks/bin/Release/net10.0/Gpss.Benchmarks.dll --seconds 600 --pattern random16
dotnet tools/Gpss.Benchmarks/bin/Release/net10.0/Gpss.Benchmarks.dll --seconds 600 --pattern mixed
dotnet tools/Gpss.Benchmarks/bin/Release/net10.0/Gpss.Benchmarks.dll --pipeline --seconds 300
```

virtualは各1k/8k/100観測/秒、4軸、10Hz解析。各6000回を全件保持した百分位（最大10,000イベント）と最大値を記録する。生存ヒープは40仮想秒と終了時にfull GCし、窓の参照を保持して測る。GC時間・回数にはこの診断用GCが含まれる。steadyAllocatedBytesPerSimSecondは40仮想秒後からの割当/仮想秒で、実秒あたりではない。WPFやネイティブ入力は含まない。

pipelineは本体XInputSource/GpssController、Normal/1ms、Random(608)のsigned16値を毎回変更するmock API。表示snapshotを17msごとに読むが、画面を描かない。実際の呼出し/観測レートを出力し、厳密な1kHz取得とは扱わない。CPUは全CPU比（8論理CPU、1コア換算は×8）。5秒区間の解析時間はRevisionを重複排除して集計し、排出時間は表示snapshotの観測値。p95/p99は全workerイベントのトレースではない。heapAtEndBytesはfull GC前の推定値で、生存ヒープとは区別する。区間GC停止は合計値であり1回ごとのp99ではない。キュー値は5秒時点の値で、区間内の最大値ではない。

## 改善前の正確なアセンブリで比較する

旧M6の4つのmanaged DLLを `archive/csharp-m6-analysis-baseline.zip` に保全した。製品の実行時依存ではない。ZIPを同名フォルダーへ展開するとBefore.csprojの既定参照先になる。DLLは元の調査の `baseline-sha256.json` と照合済み。ソースの旧 `Investigation.csproj` を現在のDLLへ向けて再ビルドすると旧版の比較にならないため、固定DLLを使う。

```sh
unzip archive/csharp-m6-analysis-baseline.zip -d archive/csharp-m6-analysis-baseline
dotnet run --project design/diagnostics/performance-optimized-2026-11-01/Before.csproj -c Release --disable-build-servers -p:UseSharedCompilation=false -- --realtime
```

この比較プログラムは[元のProgram.cs](../performance-2026-11-01/Program.cs)をリンクし、40実秒・5秒区間のJSONLを出力する。旧版CPUは1コア換算、改善後pipelineは全CPU比である。保持窓を満たした35〜40秒の割当/GCを比較する。両者とも同じRandom(608)とNormal/1msを使うが、OSスケジューリングにより観測Hzは異なる。`--reference` は現在の参照計算経路も測る機能で、旧M6バイナリの再現ではない。

## 保存ファイル

- `performance-before-realtime.jsonl`: 旧アセンブリで40実秒。
- `performance-after-pipeline.jsonl`: 改善後300実秒。
- `performance-virtual-{quantized,random16,mixed}.jsonl`: 各600仮想秒×3レート。
- `performance.trx`、build/restoreログ: 130件の試験、全体ビルド、固定依存復元。
- `source-sha256.json`: 使用した現行C#・プロジェクト・lockのハッシュ。

実時間モックは2026-11-02、それ以外は2026-11-01に測定。ディレクトリ名は改善着手日で固定している。旧DLLアーカイブはソースリポジトリ側の比較資産で、Windows製品ZIPには同梱しない。
