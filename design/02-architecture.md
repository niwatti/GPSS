# C#構成・モジュール契約 v3

## 採用技術とプロジェクト

Windows 11 x64 / .NET 10 LTS / WPF（`net10.0-windows`、`UseWPF=true`）。解析Coreと共通契約は`net10.0`でWindows APIに依存させない。C#はSDK既定の安定版言語機能を使い、previewは使わない。SDKは10.0.403をglobal.jsonに固定し、NuGet依存も各プロジェクトのpackages.lock.jsonへ固定した。検証環境はmacOS 14.6 / ARM64。Windows実行は未確認。テストはxUnitを基本とする。

```text
gpss/
  Gpss.sln
  global.json
  Directory.Build.props
  src/
    Gpss.Core/              # record、窓、震え、ステップ推定、方式registry
    Gpss.Input.Abstractions/# IInputSource、共通入力イベント
    Gpss.Input.HidDirect/   # SetupAPI/HID、Overlapped ReadFile、パース
    Gpss.Input.XInput/      # XInputGetState、packet変化、取得モード
    Gpss.Input.Simulation/  # C#デモ、fixture再生、偽クロック
    Gpss.Application/       # セッション、解析ループ、表示Snapshot
    Gpss.Desktop/           # WPF/XAML、ViewModel、描画
    Gpss.Cli/               # 旧CLIの測定/CSV保存機能を移植（M6）
  tests/
    Gpss.Core.Tests/
    Gpss.HidDirect.Tests/   # PInvokeレイアウト/I/OライフサイクルはWindows
    Gpss.XInput.Tests/      # 状態不変/更新/切断/slot/packet周回
    Gpss.Application.Tests/
    Gpss.Desktop.Tests/     # STA/Windowsが必要な描画・操作試験
    Gpss.Cli.Tests/
    fixtures/              # 入力値と期待値、HIDレポート、実機別記録
  scripts/                 # .NET restore/test/publish
  data/                    # 既存CSVを保全
  design/
```

依存方向はCore←Input.Abstractions←入力実装、およびCore/Input.Abstractions←Application←Desktop/CLI。Desktop/CLIの起動時に具体的入力源を組み立てる。HidDirect/XInputがWPFを参照したり、CoreがWin32をimportしたりしない。旧Python資産はユーザー指示により削除済み。[整理記録](14-legacy-python-cleanup.md)参照。

## スレッドとデータフロー

```text
HID専用取得処理                         XInput専用取得処理
  ReadFile → 完了待機/結果回収 → HIDデコード/正規化              GetState → packet番号の更新検出 → 正規化
                    選択した入力源だけ稼働
                             ↓ bounded InputFrame queue
解析タスク（単一writer）
  軸別の選択期間窓 → 推定/震え → immutable表示Snapshot
                             ↓ 最新Snapshotをatomic交換
WPF UIスレッド
  最大60fpsで最新状態を描画、数値/解析は10Hz
```

取得処理はUIに依存しない。HIDの通常待機とXInputのポーリングを区別し、低遅延モードだけ短いビジーウェイト/高速ポーリングを使える。起動・停止・選択・リセット命令は独立した制御経路で扱う。詳細は[05-input-acquisition.md](05-input-acquisition.md)。Raw Input登録やWM_INPUTの受信ループは実装しない。

観測キューは最大8,192観測かつ合計16MiBを上限とし、`TryWrite`相当で待機しない。上限到達時は新着を落として選択機器のLossGenerationを更新する。破棄件数は独立したatomicカウンターで保持する。後続の成功レポートへ欠落情報を反映し、同じ欠落を連番と二重計上しない。巨大・不正な1入力レポートの上限は1MiBとし、切り詰めて解析せずエラー/欠落を明示する。

解析タスクは入力と現在時刻による期限切れを順序処理する。現在位置Snapshotは到着ごとに更新可能とし、数値計算結果は100msごと。大量データでUIへレポートごとのDispatcher処理を投げない。解析が追いつかない場合も受信とUIをブロックしない。

終了: CancellationToken→取得停止→HIDはCancelIoExと完了回収、XInputはポーリング終了→キュー完了→解析終了→リソース解放。HIDのバッファ/OVERLAPPED/handleは未完了I/Oがなくなるまで所有する。停止要求と完了回収の競合も試験する。終了待ちをUIで同期的に行わず、無応答時も診断を表示する。Thread.Abortで未完了I/Oの寿命を打ち切らない。

## 共通契約（M1で型を確定）

public契約はimmutableなrecord/readonly structを基本とする。nint等のネイティブハンドルやポインターは入力実装層に閉じ込め、Coreには公開しない。

| 型 | 内容 |
| --- | --- |
| DeviceIdentity | BackendKind、接続セッションID、HIDのpath/VID/PID/version/TLC、またはXInput slot。API間の個体対応やslot/pathの永続性を断定しない |
| AxisDescriptor | HIDはUsagePage/Usage/LinkCollection/ReportId/BitSize、両方式でLogicalMin/Max、IsAbsolute、読み取り可否。XInputは固定の軸ID、HID固有項目はnullable |
| AxisMapping | LX/LY/RX/RY→AxisDescriptorのキー、反転、profile ID。適用でepoch変更 |
| InputFrame（InputObservationを兼ねる） | BackendKind、Epoch、連番、QpcTicks、取得モード、DecoderId、LossGeneration、HIDレポートまたはXInput状態/PacketNumber。nativeポインターを渡さない |
| AxisReading | 軸名、元の符号解釈済み整数、正規化double、上記の受信識別情報、ContinuityId |
| AxisReading集合 | InputFrame.Readingsに同じHIDレポートまたはXInput状態の軸値を保持。軸がない場合は省略し、他軸の古い値をコピーして埋めない |
| InputStatus | Epoch、探索/接続/未対応/切断/障害、デバイス一覧、選択、理由。データとは別経路で伝える。最終API成功と最終サンプル更新を区別 |
| AxisWindowSnapshot | Epoch、軸、Generation、NowTicks、サンプル列、連続性、期間、破棄/上限情報 |
| AxisJitter | Count、Mean、PopulationStdDev、MaxDeviation。空の場合はnullableで無効を示す |
| StepEstimate | MethodId、Status、Step/Levels/Bits、NonzeroCount、ModalCount/Ratio、Tie、ExcludedCount、Message |
| AnalysisSnapshot | Epoch、Generation、WindowEnd、入力源/取得モード/観測の重み種別、4軸の計算、左右の平均bit数、各軸件数/保持時間/最終更新年齢 |

時間は`Stopwatch.GetTimestamp()`と`Stopwatch.Frequency`を使い、変換は専用`IMonotonicClock`に集約する。整数tickで比較し、秒数は表示時に変換。注入クロックで窓の試験を可能にする。同時刻の複数レポートは受信順序で区別できる。

`IInputSource`はStartAsync/StopAsync/SelectDeviceAsync/SetModeAsync/ResetAsyncと、入力/状態の読み出し契約を持つ。入力源factoryはBackendKindからHidDirect/XInput/Simulationを構築し、軸値の観測種別（HidReport / StateChange）を公開する。方式変更時は旧sourceの停止・未完了I/O回収後に新sourceを開始し、epochを更新する。Fake/デモ/記録再生も同じインターフェースに従う。状態イベントのepochより古いデータを解析・表示しない。選択・リセットはack前に旧表示を消し、確定した新epochまで旧データを取り込まない。

`IStepEstimator`は`MethodId`と`Estimate(ReadOnlySpan<double> differences)`相当の純粋なAPI。registryはmethod ID→factory。現行方式は`mean_bits_v1`。各差分→bitの換算は共通関数に集約する。`MeanBitStepEstimator`は追加／失効に合わせて補償付きbit和と件数を更新し、全窓を再走査しない。`ModalCount/Ratio/Tie`は旧方式用の互換フィールドで、平均方式では表示・評価に使わない。詳細は[16](16-mean-precision-low-latency.md)。

## 性能・診断

1k/8kレポート毎秒の合成入力を負荷試験に用いる。これは実機レートの保証ではない。軸別上限は選択秒数W×10,000+1点で、8kHz×W秒に余裕を持たせる。原データはページ単位で遅延確保・空ページ解放し、期間切替時はworkerが窓を生成する。[17](17-measurement-window-selection.md)参照。入力観測キューも含め、メモリ使用量/割当量/GC/解析所要時間を実測する。上限に達した場合は選択期間の完全保持と表示しない。

M6時点では全件再計算と描画間引きであり、増分集計は未実装だった。2026-11-01の性能報告を受け、R1〜R3で描画generation/更新頻度の分離、worker専用窓/scratch、ブロック統計と最頻バケットの増分更新を実装した。R4計測機構とmacOSモック/仮想負荷確認も実施したが、Windowsの実機WPF性能受入は未完了。[07-performance.md](07-performance.md)は設計の正本、[09-performance-implementation.md](09-performance-implementation.md)は実装・検証記録とする。解析タスクの長い仕事をUIへ移さない。

診断にはBackendKind、HID report数、XInput API呼出し/状態更新数、軸別サンプル数、パース失敗、既知のアプリ内欠落、packet飛びの疑い、解析周期、最終取得/更新年齢、CPU使用率を記録する。XInputのpacket非連番は参考診断に留め、USB欠落数へ換算せず、差分の不連続境界にも使わない。既知のアプリ内破棄・無効値・epoch変更は従来どおり不連続境界とする。[根拠と修正](12-xinput-observed-step-fix.md)参照。受信無音や状態不変だけで切断を断定しない。どちらもハードウェア時刻/全レポート回収を保証しない。

## 配布

M6で`dotnet publish`によるwin-x64 self-contained配布へ移行した。2026-11-02の追加指示によりGUI/CLIそれぞれ単一EXEへ変更する。single-fileとネイティブDLL自己展開を有効、trim/AOTと内部圧縮は無効とする。[単一EXE配布設計](10-single-file-distribution.md)参照。Python/.NETランタイムの事前導入が不要なWindows環境で確認する。署名、インストーラー、自動更新、ARM64は今回の必須範囲外。既存buildスクリプトはM6まで維持し、切替時にC#版のビルドを指すよう更新する。

入力デコーダーはHID汎用/確認済み機種別をregistry化する。XInput状態は固定の軸定義を使う。取得源選択とデコーダー選択を分離し、UIやCoreへ機種別バイト解析を持ち込まない。CSV/設定には取得源・取得モード・デコーダー・正規化情報を保存して再現可能にする。

## M4時点の実装上の補足

HIDパースと正規化は取得側の専用処理で行い、コピー済みレポートとimmutableなAxisReadingをInputFrameとして上限付きキューに渡す。UIにはネイティブポインターを渡さない。Applicationは約4ms周期で最大8msを目安にキューを読み、解析を100msごと、WPF表示を17msごとに更新する。設定/epoch変更と解析Snapshot公開の競合はCASで旧Snapshotの上書きを防ぐ。

HIDの抜き差し検出は、ReadFileエラーと最大約1秒ごとのSetupAPI再列挙を併用する。現実装ではCM_Register_Notificationは使わない。無受信だけでは切断しない。API/モード/割当の切替は旧取得の停止とI/O完了回収後に新取得を開始する。独自機種profile用registryはあるが、未確認の機種別デコーダーは登録していない。

## M5/M6とR1〜R3の実装補足

Applicationは4軸の完全な窓で統計を計算後、描画専用`HistoryProjection`へ縮約する。1024時間バケットごとのfirst/min/max/lastと連続区間境界を保ち、最大8192点/軸とする。上限超過時は全時間バケットの代表点へ縮約し、最古・最新点を保つ。描画点の省略を警告し、表示期間の古い側を切り捨てない。[波形保持修正](11-history-coverage-fix.md)参照。WPFはさらに幅に応じて間引き、統計には戻さない。グラフは選択した10／30／60／300秒幅、4軸は別々の波形、平均線と標準偏差帯を重ねる。

`Gpss.Recording`がlegacy/v2 CSVを担当し、`Gpss.Cli`は観測を逐次保存、オンライン集計する。補助値lt/rtはInputFrameのAuxiliaryに保持し、4軸の精度計算へ混ぜない。HIDは明示割当、XInputはAPI値、未取得は欠測。[CSV仕様](06-csv-format.md)参照。

`SettingsStore`はSchemaVersion付きJSONを同一ディレクトリの一時ファイルへ書きFlush後に置換する。HIDプロファイルはパス/VID/PID/全軸記述子のSHA256で照合し、open時にも記述子の変化を検出する。XInputはAPI共通の割当だけを保存する。選択機器はヒントとして記録し、自動接続しない。

現行製品ツリーはC#へ切替済み。旧Pythonの復元用アーカイブは、後続のユーザー指示により削除した。`scripts/Publish-Windows.ps1`がGUI/CLIのself-contained配布、依存ライセンス同梱、ZIP化を直列に行う。

## 2026-11-01 性能改善の契約と実装状況

R1〜R3の製品実装を反映済み。R4用ログ計測機構とmacOS上のモック/仮想負荷確認も追加した。Windows WPFの実行・性能受入は未完了。[実装と検証](09-performance-implementation.md)。

M6の17ms全UI更新・100ms全窓コピーをR1〜R3で改善した。現在は最大60Hzの現在位置、10Hzの統計/波形、5Hzの診断を別generationで扱う。全体Snapshot参照が更新されただけでは数値や波形を再構築しない。画面の静的資源/Geometryをキャッシュし、最新要求だけを渡す。

最新位置は取得元の小さなepoch付き状態から公開し、重い解析の終了を待たない。統計は単一writerのbounded窓で保持し、窓view/scratchをUIに共有しない。R2では一時配列とSortを除き、R3ではCount/Mean/M2/Min/Maxのブロック集計、edgeの追加/失効による最頻差分、時間固定ブロックの描画縮約へ移した。取得データの欠落と描画点の省略は別flagで示す。

現在値・統計・描画にEpoch/Revision/計算対象時刻を持たせる。将来の推定器には増分sessionをオプションで提供し、stateless参照経路を残す。通常時の選択期間全体・元観測・数値仕様を保つ。処理予算・数値誤差・取消・メモリ所有権は[性能改善設計](07-performance.md)に従い、Windows通し性能試験を完了条件とする。
