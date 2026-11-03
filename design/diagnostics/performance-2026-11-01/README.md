# 2026-11-01 性能調査の再現資料

このディレクトリは最適化前の調査記録。後続の性能改善実装後は、下記プロジェクトを再ビルドすると現在のDLLを参照するため、旧結果の再現にはならない。旧アセンブリを固定した前後比較は[改善後の再現資料](../performance-optimized-2026-11-01/README.md)を使用する。

調査当時は製品コードの最適化は行っていない。M6のReleaseアセンブリを参照し、既存処理を計測した調査用プログラム。ソリューションや配布対象には追加しない。別プロセスで実行し、未使用の診断用global tool等を導入する必要はない。

リポジトリ直下でSDK 10.0.403を使用する。

```sh
dotnet restore Gpss.sln --locked-mode --disable-parallel --disable-build-servers -m:1
dotnet build src/Gpss.Cli/Gpss.Cli.csproj -c Release --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false
dotnet run --project design/diagnostics/performance-2026-11-01/Investigation.csproj -c Release --disable-build-servers -p:UseSharedCompilation=false
dotnet run --project design/diagnostics/performance-2026-11-01/Investigation.csproj -c Release --disable-build-servers -p:UseSharedCompilation=false -- --realtime
```

両方の試験を同時実行しない。出力はJSON Lines。`stages.jsonl`と`realtime.jsonl`は今回の実行結果、`m6-baseline.jsonl`は2026-10-28の旧負荷試験結果で、再実行結果ではない。調査対象ソースと使用アセンブリのハッシュは`baseline-sha256.json`へ記録した。調査後のレイアウト変更前の版を示し、後続のUIソースと同じハッシュであることは要求しない。

macOS 14.6.1 ARM64 / .NET 10.0.14 / Workstation GC / 8論理CPU。Windows実機やWPFは実行していない。

引数なし: 4軸、1k/8k観測/秒、1/10/30秒窓を事前生成。256段階の反復波形（quantized8）と、Random(608)の独立した0..65535の値（noise16）を使用する。noise16は多数の差分バケットを生む負荷条件であり、実コントローラーのノイズモデルではない。各処理を5回warmup後、20回反復し、スレッドの割当量・時間・プロセスのGen2回数を記録する。分位点は少数反復の概算であり合否判定には使用しない。fullは4軸のsnapshot/projection/modal/jitter/recentRateをまとめて計測する。段階を別々に計測するとJIT/GC/配列プール状態が変わるので、段階別の時間・割当量は単純合計でfullと一致しない。

`--realtime`: WindowsXInputApiを呼ばず、毎回packet番号とsigned16軸を変更するmock IXInputApiをXInputSourceへ注入。取得は実装どおりNormal/1ms、GpssControllerも本体実装を使用する。40秒を実時間で実行し5秒間隔で出力する。実測状態更新は約0.8〜0.9k/秒で、1,000Hzの実機検証ではない。CPUは1論理コア=100%換算で、GUIの全CPU比表示とは異なる。GC停止は区間合計、各行のスナップショット年齢はその瞬間の値でp95ではない。両試験ともWPF描画・ネイティブXInput・ETW・キュー深度・ユーザー操作遅延は測定していない。

詳しい原因分析と改善仕様は[07-performance.md](../../07-performance.md)。

保存先の相対参照プロジェクトもReleaseビルド成功（警告0/エラー0）。今回の新しい診断プログラムのみのビルドであり、製品の103試験を再実行した結果ではない。
