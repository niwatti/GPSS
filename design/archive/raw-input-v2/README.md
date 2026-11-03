# GPSS C# / Windows Raw Input 設計 v2

更新日: 2026-10-28。今回の変更範囲は設計と移行計画。**C#版の実装は未着手であり、移行段階の実装上限は未指定**。

## 方針

アプリケーション、解析、入力取得、デモ、CLI、テストの現行ソースをC#へ移行する。Windows 11 x64 / .NET 10 LTS / WPFを採用し、入力はWindows Raw Input APIとHIDパーサーAPIで取得する。Python、PySide6、pygame、PyInstallerへの実行・ビルド依存を最終的に除く。XAML、プロジェクト設定、PowerShell等の構成・ビルドファイルは用途に応じて残す。

C#からWin32 APIをP/Invokeで呼び出すため技術的に実現可能。ただし取得できる情報はWindowsドライバーが公開するHIDレポートに限る。Raw Input化で必ず高精度・高レートになるとは限らず、センサー内部のADC値を保証しない。XUSBのHID公開例も公式資料にあるが、全機種・USB/Bluetooth・動作モードの互換性を一律に保証しない。未列挙・軸未公開・未対応レポートは理由を表示する。別APIへ黙って切り替えない。

## 文書と実装指示

1. [機能・計算仕様](01-specification.md): 表示・30秒窓・計算の意味。
2. [構成・契約](02-architecture.md): C#プロジェクト、スレッド、インターフェース。
3. [移行段階と受入条件](03-stages.md): **M1〜M6**の累積計画。
4. [エージェント実行手順](04-agent-workflow.md): 所有範囲、並行実装、停止条件。
5. [Raw Input / HID取得詳細](05-raw-input.md): 登録、パース、軸割当、時刻と欠落。
6. [実装状況](implementation-status.md): 現行Python版と新C#版の状態を分離。

今後の指示例: 「C#移行M3まで実装してください」。M1〜M3を累積実装し、M4以降には進まない。旧「段階3まで」の許可はPython版に対する過去の指示であり、新C#実装の許可や完了として転用しない。今回はソースを書き換えず、ユーザーが指定する移行上限を待つ。

## 機能と計測上の決定

- 左右に大きな円、現在位置の点とX/Y数値。
- 最新30秒の各軸の絶対差分を用い、ゼロを除いた最頻差分から `levels=2.0/step`、`bits=log2(levels)` を算出。小数1桁の四捨五入。
- 方式は`IStepEstimator`とregistryで差し替え可能。X/Y別の値と、それぞれ低い方を左右代表値として表示。
- 最新30秒の**標準偏差・平均・平均からの最大絶対偏差**を各軸で常時表示し、波形・平均線・標準偏差帯を図示。
- Raw Inputは**イベント駆動**。固定1,000Hzポーリングと同じ値を繰り返す人工サンプルは廃止。受信レートは実測し、描画最大60fps・解析10Hzと分離する。
- HIDのUsage/Report ID/Link Collectionで軸を識別。軸インデックス0〜3を前提にしない。論理範囲から[-1,1]へ変換し、整数値と範囲も保持する。
- 単一プロセス内の専用Raw Input受信スレッド、解析タスク、WPF UIスレッドに分ける。Pythonの入力子プロセス/IPC/heartbeat構成は廃止。

## 現行版と移行完了の定義

Python v1は旧段階1〜3を実装済みで、macOS上で80テスト合格。Windows実機は未確認。旧設計と当時の検証記録を[archive/python-v1](../python-v1/README.md)に保存した。現行のPythonソースとdataは今回変更しない。

M4で旧GUI段階3相当をC#へ移植、M5で未実装だった震え表示を追加し、M6で既存CLI・CSV保存もC#へ移植して配布経路を切り替える。PythonはM6の検証完了まで比較・復元用に保持する。最終的に現行製品のソースとテストをC#に統一し、旧Python資産は復元用アーカイブへ退避する。既存dataは保全する。

## 公式根拠

2026-10-28確認。取得設計の詳細な根拠は[Raw Input設計](05-raw-input.md#公式資料)に記載。

- [Raw Input API](https://learn.microsoft.com/en-us/windows/win32/inputdev/raw-input): HID入力を受信するWin32 API。
- [DirectInput and XUSB Devices](https://learn.microsoft.com/en-us/windows/win32/xinput/directinput-and-xusb-devices): XUSBドライバーのHID公開と機器種別ごとの軸の対応例。
- [.NETサポートポリシー](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core): .NET 10はLTS。実装開始時に最新の対応パッチ/SDKを確認し固定する。

注記（2026-11-02）: このディレクトリは旧C# Raw Input設計の履歴。本文の旧Python資産保持方針は後続のユーザー指示で変更され、旧Pythonファイルは削除済み。[現行の整理記録](../../14-legacy-python-cleanup.md)を参照。
