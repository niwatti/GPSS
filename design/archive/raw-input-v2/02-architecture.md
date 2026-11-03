# C#構成・モジュール契約 v2

## 採用技術とプロジェクト

Windows 11 x64 / .NET 10 LTS / WPF（`net10.0-windows`、`UseWPF=true`）。解析Coreと共通契約は`net10.0`でWindows APIに依存させない。C#はSDK既定の安定版言語機能を使い、previewは使わない。SDKとNuGet依存はM1で実環境を確認してglobal.jsonとlockファイルへ固定する。テストはxUnitを基本とする。

```text
gpss/
  Gpss.sln
  global.json
  Directory.Build.props
  src/
    Gpss.Core/              # record、窓、震え、ステップ推定、方式registry
    Gpss.Input.Abstractions/# IInputSource、共通入力イベント
    Gpss.Input.RawInput/    # Win32/HID PInvoke、専用HWND、パース
    Gpss.Input.Simulation/  # C#デモ、fixture再生、偽クロック
    Gpss.Application/       # セッション、解析ループ、表示Snapshot
    Gpss.Desktop/           # WPF/XAML、ViewModel、描画
    Gpss.Cli/               # 旧CLIの測定/CSV保存機能を移植（M6）
  tests/
    Gpss.Core.Tests/
    Gpss.RawInput.Tests/    # PInvokeレイアウト/Win32統合はWindows
    Gpss.Application.Tests/
    Gpss.Desktop.Tests/     # STA/Windowsが必要な描画・操作試験
    Gpss.Cli.Tests/
    fixtures/              # 入力値と期待値、HIDレポート、実機別記録
  scripts/                 # .NET restore/test/publish
  data/                    # 既存CSVを保全
  design/
```

依存方向はCore←Input.Abstractions←入力実装、およびCore/Input.Abstractions←Application←Desktop/CLI。Desktop/CLIの起動時に具体的入力源を組み立てる。RawInputがWPFを参照したり、CoreがWin32をimportしたりしない。移行中は`src/gpss`と新C#ディレクトリを併存させる。

## スレッドとデータフロー

```text
Raw Input専用スレッド
  HWND_MESSAGE + GetMessage/DispatchMessage
  WM_INPUT → GetRawInputData → 選択HIDのレポートをコピー・検証
                     ↓ bounded report queue
解析タスク（単一writer）
  HIDデコード → 軸別30秒窓 → 推定/震え → immutable表示Snapshot
                     ↓ 最新Snapshotをatomic交換
WPF UIスレッド
  最大60fpsで最新状態を描画、数値/解析は10Hz
```

UIウィンドウを受信先にしない。ドラッグやレイアウト処理の負荷から受信を分離する。登録はプロセスで一元管理する。起動・停止・選択・リセット命令は制御経路で扱い、データキューが満杯でも命令と切断が埋もれない。Raw Input詳細は[05](05-raw-input.md)。

レポートキューは最大8,192レポートかつ合計16MiBを上限とし、`TryWrite`相当で待機しない。上限到達時は新着を落として選択機器のLossGenerationを更新する。破棄件数は独立したatomicカウンターで保持する。後続の成功レポートへ欠落情報を反映し、同じ欠落を連番と二重計上しない。巨大・不正な1メッセージの上限は1MiBとし、切り詰めて解析せずエラー/欠落を明示する。

解析タスクは入力と現在時刻による期限切れを順序処理する。現在位置Snapshotは到着ごとに更新可能とし、数値計算結果は100msごと。大量データでUIへレポートごとのDispatcher処理を投げない。解析が追いつかない場合も受信とUIをブロックしない。

終了: CancellationToken→受信スレッドへのWM_APP停止命令→登録解除（RIDEV_REMOVE、target=NULL）→HWND/クラス破棄→キュー完了→タスク終了。受信スレッドで取得したリソースは同スレッドのfinallyで解放する。終了待機は有限時間とし、応答がないときもUIを固めず診断を残す。Thread.Abortや子プロセスterminateは使わない。

## 共通契約（M1で型を確定）

public契約はimmutableなrecord/readonly structを基本とする。nint等のネイティブハンドルやポインターはRawInput層に閉じ込め、Coreには公開しない。

| 型 | 内容 |
| --- | --- |
| DeviceIdentity | 接続セッションID、device path、VID/PID/version、TLCのUsagePage/Usage、製品名。pathの同一性やVID/PIDだけを永続個体IDと断定しない |
| AxisDescriptor | UsagePage、Usage、LinkCollection、ReportId、BitSize、LogicalMin/Max、IsAbsolute、HasNull、読み取り可否 |
| AxisMapping | LX/LY/RX/RY→AxisDescriptorのキー、反転、profile ID。適用でepoch変更 |
| ReceivedReport | Epoch、受信連番、QpcTicks、PacketId、ReportIndex、ReportBytes、LossGeneration。nativeポインターを渡さない |
| AxisReading | 軸名、元の符号解釈済み整数、正規化double、上記の受信識別情報、ContinuityId |
| InputFrame | 同じレポートから得た軸のimmutable集合。軸がない場合は省略し、他軸の古い値をコピーして埋めない |
| InputStatus | Epoch、探索/接続/未対応/切断/障害、デバイス一覧、選択、理由。データとは別経路で伝える |
| AxisWindowSnapshot | Epoch、軸、Generation、NowTicks、サンプル列、連続性、期間、破棄/上限情報 |
| AxisJitter | Count、Mean、PopulationStdDev、MaxDeviation。空の場合はnullableで無効を示す |
| StepEstimate | MethodId、Status、Step/Levels/Bits、NonzeroCount、ModalCount/Ratio、Tie、ExcludedCount、Message |
| AnalysisSnapshot | Epoch、Generation、WindowEnd、4軸の計算、左右代表値、各軸件数/保持時間/最終受信年齢 |

時間は`Stopwatch.GetTimestamp()`と`Stopwatch.Frequency`を使い、変換は専用`IMonotonicClock`に集約する。整数tickで比較し、秒数は表示時に変換。注入クロックで窓の試験を可能にする。同時刻の複数レポートは受信順序で区別できる。

`IInputSource`はStartAsync/StopAsync/SelectDeviceAsync/ResetAsyncと、入力/状態の読み出し契約を持つ。Fake/デモ/記録再生も同じインターフェースに従う。状態イベントのepochより古いデータを解析・表示しない。選択・リセットはack前に旧表示を消し、確定した新epochまで旧データを取り込まない。

`IStepEstimator`は`MethodId`と`Estimate(ReadOnlySpan<double> differences)`相当の純粋なAPI。registryはmethod ID→factory。初期方式は`modal_delta_v1`。Step→Levels→Bitsは共通関数にし、方式やUIからの重複実装を避ける。量子幅qはversion付き設定として保持し、変更時に窓内の差分を再評価する。

## 性能・診断

1k/8kレポート毎秒の合成入力を負荷試験に用いる。これは実機レートの保証ではない。軸別上限は各300,000点で、8kHz×30秒に余裕を持たせる。Raw受信キューも含め、メモリ使用量/割当量/GC/解析所要時間を実測する。上限に達した場合は完全な30秒と表示しない。

M2では正しさ優先の参照実装、M5では必要に応じて期限切れ時の最頻バケット件数更新、安定な移動分散、min/max描画間引きにより性能を確保する。解析タスクの長い仕事をUIへ移さない。

診断には受信WM_INPUT数、HID report数、軸別サンプル数、パース失敗、既知のアプリ内欠落、解析周期、最後の受信からの時間を記録する。受信無音だけで切断を断定しない。Raw Inputには通常ハードウェアの連番/時刻がないため、OSや機器側での全欠落検出を保証しない。

## 配布

M6で`dotnet publish`によるwin-x64 self-containedディレクトリ配布へ移行する。最初はsingle-file/trim/AOTを有効にしない。Python/.NETランタイムの事前導入が不要なWindows環境で確認する。署名、インストーラー、自動更新、ARM64は今回の必須範囲外。既存buildスクリプトはM6まで維持し、切替時にC#版のビルドを指すよう更新する。
