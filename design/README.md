# GPSS C# / HID直接読み取り・XInput 設計 v3

更新日: 2026-11-02。C#移行の指定上限は**M6**、追加指示により性能改善も実施。M1〜M6のコードを実装し、クロスビルド、Core/入力モック/CLI試験、合成負荷と配布物を確認。Windows実機とWPF実行の受入は未確認。詳細は[実装状況](implementation-status.md)。

2026-11-01の表示性能報告に対しR1〜R3を実装し、R4計測機構とモック/仮想負荷確認を追加した。Windows WPF実行・性能受入は未完了。[設計](07-performance.md)、[実装と測定状況](09-performance-implementation.md)を参照。

## 方針

アプリケーション、解析、入力取得、デモ、CLI、テストの現行ソースをC#へ移行する。Windows 11 x64 / .NET 10 LTS / WPFを採用し、入力はHIDレポートの直接読み取り（CreateFileW + ReadFile / Overlapped I/O）を主方式、XInputGetStateを互換用とする。Python、PySide6、pygame、PyInstallerへの実行・ビルド依存を最終的に除く。XAML、プロジェクト設定、PowerShell等の構成・ビルドファイルは用途に応じて残す。

C#からWin32 APIをP/Invokeで呼び出して実現する。InputLagScopeの取得方式を参考にし、WM_INPUT方式のRaw Input専用設計を置き換える。取得値は機器/ドライバー/APIが公開する値であり、センサー内部のADC値や全機器の互換性を保証しない。HIDを推奨するが、未対応時は理由とXInput候補を提示してユーザーが選択する。計測中の黙ったAPI切替・二重取得は行わない。

## 文書と実装指示

1. [機能・計算仕様](01-specification.md): 表示・30秒窓・計算の意味。
2. [構成・契約](02-architecture.md): C#プロジェクト、スレッド、インターフェース。
3. [移行段階と受入条件](03-stages.md): **M1〜M6**の累積計画。
4. [エージェント実行手順](04-agent-workflow.md): 所有範囲、並行実装、停止条件。
5. [HID直接読み取り / XInput取得詳細](05-input-acquisition.md): 非同期I/O、状態更新、軸割当、時刻と欠落。
6. [CSV仕様](06-csv-format.md): legacy/v2、時間と欠測の定義。
7. [性能調査・改善設計](07-performance.md): 計測根拠、描画抑制、増分集計、R1〜R4の受入。
8. [1920×1080向けレイアウト](08-desktop-layout.md): スクロール不要の配置、DPI対応、確認手順。
9. [性能改善の実装・検証](09-performance-implementation.md): R1〜R4の実装範囲、最新計測。
10. [単一EXE配布](10-single-file-distribution.md): バンドル設定、自己展開、配布検証。
11. [30秒波形の保持修正](11-history-coverage-fix.md): 描画上限で過去の波形が消える問題の原因と対策。
12. [8kHz通常モードの推定修正](12-xinput-observed-step-fix.md): XInput番号飛びによる差分全除外の原因と修正。
13. [ステップ精度ヒストグラム](13-step-histogram.md): 各入力差分のbit数、4軸合算、最新30秒、1bit幅の表示。
14. [Python版資産の整理](14-legacy-python-cleanup.md): 削除対象と現行C#で保持する資産。
15. [追加変更後の試験記録](15-verification-2026-11-02.md): 旧計算・通常モードを含む当時の検証。
16. [平均ステップ精度・低遅延固定](16-mean-precision-low-latency.md): 現行アルゴリズム、設定移行、GUIプレビュー。
17. [計測・蓄積期間の選択](17-measurement-window-selection.md): 10秒／30秒／1分／5分、保存・切替・保持容量。
18. [ロゴ・アイコン](18-logo-and-icons.md): ロゴ資産、EXEと各ウィンドウへの埋め込み。
19. [出力先統合](19-output-consolidation.md): 標準出力先への統合、分割・一時出力の整理、ロック時の扱い。
20. [実装状況](implementation-status.md): 実装・自動試験・Windows未確認を分離。

今後の指示例: 「C#移行M3まで実装してください」。M1〜M3を累積実装し、M4以降には進まない。旧「段階3まで」の許可はPython版に対する過去の指示であり、新C#実装の許可や完了として転用しない。現在の指定はM6まで。旧Python資産は後続のユーザー指示により削除した。[整理記録](14-legacy-python-cleanup.md)参照。

## 機能と計測上の決定

計測・蓄積期間は10秒／30秒／1分／5分から選択し、未設定時は30秒。平均精度・ヒストグラム・震え・波形で共通にする。変更時は履歴をリセットする。

- 左右に大きな円、現在位置の点とX/Y数値。
- 選択期間内の各非ゼロ有効差分を `bits=log2(2.0/delta)` へ換算し、丸め前のbit数を算術平均する。表示のみ小数1桁に四捨五入。
- 方式は`IStepEstimator`とregistryで差し替え可能。X/Y別の平均と、両軸の有効差分件数で重み付けした平均を左右に表示。
- 選択期間内の**標準偏差・平均・平均からの最大絶対偏差**を各軸で常時表示し、波形・平均線・標準偏差帯を図示。
- HIDは読み取ったレポート、XInputは初回状態とpacket番号が変わった状態を記録。同じXInput状態を呼出し回数だけ複製しない。HID受信/秒とXInput状態更新検出/秒・API呼出し/秒を区別し、描画最大60fps・解析10Hzと分離する。
- HIDのUsage/Report ID/Link Collectionで軸を識別。軸インデックス0〜3を前提にしない。論理範囲から[-1,1]へ変換し、整数値と範囲も保持する。
- 単一プロセス内の取得処理、解析タスク、WPF UIスレッドを分離。取得は低遅延だけに固定し、GUIのモード選択を廃止。HIDの短時間ポーリング後の完了待機とXInputの上限付き高速取得を使用する。Pythonの入力子プロセス/IPC構成やRaw Inputの受信HWNDは採用しない。

## 現行版と移行完了の定義

C#移行はM1〜M6まで実装済み。旧Python版の過去の実績は移植時の比較材料として扱い、C#の受入結果とは区別する。2026-11-02、ユーザー指示により旧ソース・仮想環境・移行アーカイブ・旧Python設計を削除した。測定CSVとC#で実行する比較fixtureは現行資産として保持する。[整理対象](14-legacy-python-cleanup.md)参照。

旧Raw Input専用設計は[archive/raw-input-v2](archive/raw-input-v2/README.md)に保全した。現在はv3を正本とする。

## 参照資料

2026-10-28確認。取得設計の詳細な根拠は[取得設計](05-input-acquisition.md#検証と参照)に記載。

- [InputLagScope取得コード](https://github.com/608/InputLagScope/blob/main/src-tauri/src/capture.rs): HID直接取得とXInput状態取得の分岐。
- [Obtaining HID Reports](https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/obtaining-hid-reports): ReadFileによる継続取得。
- [XINPUT_STATE](https://learn.microsoft.com/en-us/windows/win32/api/xinput/ns-xinput-xinput_state): packet番号と状態変化。
- [DirectInput and XUSB Devices](https://learn.microsoft.com/en-us/windows/win32/xinput/directinput-and-xusb-devices): XUSBドライバーのHID公開と機器種別ごとの軸の対応例。
- [.NETサポートポリシー](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core): .NET 10はLTS。実装開始時に最新の対応パッチ/SDKを確認し固定する。
