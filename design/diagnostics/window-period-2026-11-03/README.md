# 計測・蓄積期間選択の検証資料

2026-11-02〜03、macOS 14.6.1 ARM64 / SDK 10.0.403 / runtime 10.0.14。[現行設計](../../17-measurement-window-selection.md)を参照。

## 回帰試験

`window-period-build.log` はWPF本体と19ケースの試験を含む全16プロジェクトのビルドで、警告0・エラー0。`window-period.trx` と `window-period-tests.log` はCore100、HID21、XInput14、Application40、CLI13、計188ケース合格。Windows/WPFの実行は含まない。

追加19ケースは4期間の境界・全指標への適用・失効・設定保存・旧設定省略時の30秒・不正値・切替時の旧値除去を確認する。ページの部分末尾・周回・リセット、空の5分窓を最大サイズで確保しないこと、低レート長時間運転で期限切れページを解放することも含む。WPF既存ケースは3つの選択欄と30秒既定値、4期間の横軸計算へ更新したが、コンパイルのみ。

共有ドライブが一時切断されたため作業を中断し、ユーザーによる再接続後にソースを確認、全体ビルドと188ケースを実行して配布を更新した。

`window-period-initial-recheck.*` は再接続後の初回再実行で、旧Normal指定の試験が「50msあたり約4回/ms以下」というCPU依存の上限で1件失敗した記録。低遅延では1µsのburst内に複数のAPI呼出しを許すため、この上限は仕様ではなかった。試験をモード固定・同一packetの重複除外・停止確認へ修正し、本体は変更せずに全件を再実行した。最終TRXと初回失敗は区別する。

## 仮想負荷

`window-period-{seconds}-{pattern}.jsonl` に各期間で100／1k／8kHz・4軸の結果を記録する。

```sh
dotnet build tools/Gpss.Benchmarks/Gpss.Benchmarks.csproj -c Release --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false
dotnet tools/Gpss.Benchmarks/bin/Release/net10.0/Gpss.Benchmarks.dll --seconds 75 --window-seconds 10 --pattern mixed
dotnet tools/Gpss.Benchmarks/bin/Release/net10.0/Gpss.Benchmarks.dll --seconds 90 --window-seconds 30 --pattern random16
dotnet tools/Gpss.Benchmarks/bin/Release/net10.0/Gpss.Benchmarks.dll --seconds 150 --window-seconds 60 --pattern quantized
dotnet tools/Gpss.Benchmarks/bin/Release/net10.0/Gpss.Benchmarks.dll --seconds 420 --window-seconds 300 --pattern quantized
```

全12条件で取得上限による切捨てなし、原データと投影の保持期間差の最大0秒、描画8,192点以内。mixedは状態不変でサンプルを出さない時間を含むため、観測点同士の保持期間は選択秒数より短くなり得る。期間差0秒は内部の描画省略や実機欠落0を意味しない。

5分窓・8kHzは軸ごと2,400,001点／300秒を保持し、リング周回を通過した。解析p99約0.45ms、計測用GC後の全体ヒープ推定約915MiB。密な長期間は正確な原データ保持にメモリを要する。明示GCを含む仮想時間の高速再生であり、Windowsの実時間CPU・GC停止率・実入力・WPF描画の受入ではない。以前の全欠測境界条件の性能問題は未解決扱い。

## 配布・同一性

`verification.json` は件数と負荷の集計、`source-sha256.json` はソース・project・lockのハッシュ。依存lock16件は従来から変更なし。`window-period-publish-*.log` はwin-x64自己完結・単一EXE生成、`bundle-validation.json` は全収録ファイルとpre-bundle hostの照合結果。Windows上でのEXE起動は未実施。

旧配布を `artifacts/GPSS-win-x64-before-window-period.zip` に保全し、新版を `artifacts/GPSS-win-x64.zip` とする。循環ハッシュを避けるため、ZIP自体の検査結果はZIP外の `package-validation.json` に保存する。

既存展開先のDesktop.exeを削除する際にSMBのResource busyを検出したため、旧 `artifacts/win-x64/` は置き換えず、`artifacts/win-x64-window-period/` を新規展開先とした。ZIPは新しいEXEと文書だけから生成する。旧展開先からの起動では新機能を使えない。
