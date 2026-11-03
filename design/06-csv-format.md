# CLIとCSV仕様（M6）

CLIは選択軸の観測件数Nまで測定し、取得区間全体の母標準偏差・平均・平均からの最大絶対偏差を出力する。GUIの最新30秒統計とは計算区間が異なる。NはAPI呼出し回数ではない。準備期間とEnter待ちのフレームは破棄し、測定開始時刻より古いフレームも除外する。無受信・XInput状態不変でも既定300秒（準備/待機を含む）の時間上限、Ctrl+C、切断/エラーで終了する。

CSVはUTF-8、カンマ区切り、小数点`.`、必要時に引用符を使用する。逐次書き込みし、全記録をメモリに蓄積しない。タイムアウト等でも取得済みCSVは閉じる。時間切れ終了は完全なN件の測定結果として扱わない。

## legacy（既定）

ヘッダーは旧版と同じ8列。

```csv
timestamps,elapsed_time,lx,ly,rx,ry,lt,rt
```

1取得フレームにつき1行。`timestamps`は入力開始からの単調経過ミリ秒、`elapsed_time`は準備/Enter待ちを除いた測定開始からの単調経過ミリ秒。両者はUTC時刻ではない。旧版もミリ秒だったが、固定1kHzのサンプリング周期・同じ件数で同じ時間長は保証しない。実際の行数、選択軸の件数、経過時間を併せて読む。

各軸は論理整数範囲全体から`2*(raw-min)/(max-min)-1`へ正規化し、設定した反転を適用する。HIDの部分Report IDで届かなかった軸は、その行ではNaNであり直前値を補完しない。lt/rtはHID公開軸の明示割当、またはXInput trigger（0..255）から同じ[-1,1]へ変換する。未公開/未割当はNaN。XInputの無押下triggerは-1であり、欠測の0埋めはしない。

`--analyze PATH --axis rx`はこの形式を読み、有限値のみで3指標を計算する。既存22ファイルの全4軸を自動試験で読み戻す。旧Windows版のCR CR LF改行、空行、引用符を扱う。時間は有限・非負・非減少、非空行はヘッダーと同列数を要求する。NaN/空の軸は欠測として除外する。

## v2（`--format v2`）

25列の長形式。`schema_version=2`。1フレームを軸ごとの複数行で表し、同じepoch/sequenceを共有する。補助値は`record_type=auxiliary`、軸は`axis`、観測値なしは`frame`。欠けた軸の行は生成しない。

| 列 | 内容 |
| --- | --- |
| schema_version | 2 |
| timestamp_ms / elapsed_time_ms | Stopwatch単調クロック由来ミリ秒 / 測定開始からのミリ秒。v2 timestampはクロック基点の値でlegacyの入力開始基点とは異なる |
| backend / mode / decoder_id | 入力方式、取得モード、デコーダー |
| report_id_usage | 解釈した軸/補助値のUsage Page、Usage、Link Collection、Report ID。記述子がなければunknown |
| epoch / sequence / dropped_count | 接続世代、観測順序、既知のアプリ内破棄数。USBパケット欠落数とは異なる |
| record_type / continuity_id / axis | 行種別、連続区間ID、lx/ly/rx/ry。補助値には連続区間IDがないため空欄 |
| raw_value / logical_min / logical_max / normalized_value | 元整数、論理範囲、正規化値。取得できないメタ情報は空欄 |
| normalization / inverted / link_collection | linear_minmax_v1、反転フラグ、Link Collection |
| auxiliary_name / auxiliary_raw_value / auxiliary_value / auxiliary_logical_min / auxiliary_logical_max | lt/rt行の元整数・値・範囲。軸行では空欄 |

正規化式はlegacyと共通。XInputのslot番号を恒久的な機器識別子として保存・復元しない。新形式は取得情報の保全用であり、現在の`--analyze`はlegacy専用。新たな解析器はschema_versionとrecord_typeを確認し、欠測・連続性・方式の違いを保つこと。
