# C#移行v3 実装・検証状況

更新日: 2026-11-03。C#移行の指定上限は**M6**、追加指示により性能改善も実施。M1〜M6のコードとWindows用GUI/CLIの自己完結型配布物を実装した。**Windows実機とWPF実行の受入は未確認**。実装済みとWindows受入済みは区別する。

## ロゴ・アプリアイコン

二つの測定リングからなるロゴを制作し、16〜256pxの9サイズのICOへ変換した。追加指示によりリング内の青い点を両方除去した。GUI／CLIのApplicationIcon、メイン画面・取得設定・軸割当・精度詳細のWindow.Iconへ設定し、ICOをWPF資源に埋め込む。後続指示で出力先を `artifacts/win-x64/` に統合した。実Windowsでの表示は未確認。[設計と検証](18-logo-and-icons.md)と[出力先統合](19-output-consolidation.md)参照。

## 計測・蓄積期間の選択（現行）

10秒／30秒／1分／5分を選択でき、初期値30秒、最後の選択を保存・復元する。平均bit・ヒストグラム・震え3指標・波形を同じ期間へ連動させ、切替時はepochと履歴をリセットする。保持容量を期間に応じて拡張し、原データは遅延確保・空ページ解放する。188ケース合格、ビルド警告0・エラー0。Windows実画面と実入力は未確認。[設計・検証](17-measurement-window-selection.md)参照。

## 平均ステップ精度・低遅延固定

後続指示により、精度を最新30秒の各差分の丸め前bit数の平均へ変更した。X/Yの平均と、全有効差分件数で重み付けしたスティック平均を表示する。取得はGUI／CLIとも低遅延へ固定し、通常モード選択を除去、旧設定を自動移行する。169ケース合格、全体ビルド警告0・エラー0、WPF19ケースはコンパイルのみ。[計算・移行・会話でのGUI注釈](16-mean-precision-low-latency.md)参照。

## 平均方式変更前の試験状況（2026-11-02、履歴）

追加変更後の指示により、全体クロスビルドと回帰試験を実施した。警告0・エラー0、161ケースすべて合格（Core82、HID21、XInput14、Application31、CLI13）。WPF19ケースはコンパイルのみ。XInput通常／低遅延の状態番号飛び、4軸合算ヒストグラム、30秒窓、削除後のCSV／fixture保全を確認した。

仮想300秒×12条件で波形保持期間の短縮は0秒。ただし8kHzで全観測を別continuityにする条件は解析p99約159msで目標未達。過去の良好な性能値から現行コードの全条件合格を推定しない。[最新の試験記録と残課題](15-verification-2026-11-02.md)を参照する。

実時間5分のmock XInput通常／1ms＋本体Controllerでは、解析p95 4.62ms／p99 20.34ms／最大23.65ms、40秒以降の割当8.69〜9.03MiB/秒。実測更新は約782Hzであり、1kHz実機やWPF描画の受入結果ではない。配布GUI389件／CLI182件の収録ファイルとhostを照合し、既存EXEを保って最新文書・検証資料をZIPへ反映した。

## 2026-11-01 性能問題の追記

XInput・通常・1,000Hzで約10秒後に表示が重くなる報告を受けた。ソース調査と追加計測で、全窓の毎回コピー/集計による割当・GC増加を確認した。UIは同じ統計/波形を17msごとに更新する経路がある。実時間40秒のmock XInput＋本体Controller試験では割当が約25.8→265.9MiB/秒へ増加した。これはWindowsでの表示遅延を直接再現した試験ではない。

M6の全量再計算とライブヒープ/p95単体の評価では表示性能を十分評価できていなかった。下記の過去の試験・数値は当時の実績として保持するが、**M5/M6の表示性能を受入済みとは扱わない**。[調査・改善設計](07-performance.md)と[生ログ/再現資料](diagnostics/performance-2026-11-01/README.md)を追加した。当時の調査では性能改善コードを変更していなかった。後続作業でR1〜R3を実装し、R4記録機構とmacOSモック/仮想負荷確認を追加した。Windows WPF実行・性能受入は未完了。M6時点の103試験・性能値は当時の記録として保持し、最新のR1〜R4受入根拠には使わない。現在の検証結果は[09-performance-implementation.md](09-performance-implementation.md)。

## 2026-11-01 1920×1080レイアウト変更

ユーザー指示により、メインを最大化する有限Gridへ変更し、ScrollViewerを廃止した。左右カードは円/精度を横並び、震え3指標はX/Y表、2つの波形は可変高さ。推定詳細はライブ更新の別ウィンドウへ移した。最小1180×760DIPのcanvasを必要時だけ全体縮小し、表示領域へ収める。[仕様とWindows確認](08-desktop-layout.md)を追加した。

最終コードの全体クロスビルド成功（警告0/エラー0）。WPF試験19ケースはcompileのみで、Windows STA実行は未実施。過去のレイアウト時16ケース記録は履歴として保持する。現在の性能実装・計測状況は[09-performance-implementation.md](09-performance-implementation.md)。配布ZIPはレイアウト更新版を生成済みで、旧版を`artifacts/GPSS-win-x64-M6-baseline.zip`へ保全した。

## 2026-11-02 単一EXE配布

GUI/CLIそれぞれをDLL・.NETランタイム込みのEXEへ変更した。GUI133.69MiB/CLI70.50MiB、各フォルダーは1ファイルだけ。全バンドル入力とのSHA256照合、lock付き復元、警告0/エラー0のビルド、130件の回帰試験合格を確認した。Windows起動と自己展開の実行、PowerShell自体の実行は未確認。[設定・検証結果](10-single-file-distribution.md)参照。

## 2026-11-02 30秒波形の保持修正

描画8,192点の上限で古い側を捨てる処理を、窓全期間の縮約へ変更した。区切りが多い場合も最古点/最新点と各時刻バケットの極値を保持し、増分集計で負荷を抑える。最終135件合格、6条件の仮想負荷で描画期間短縮0秒。単一EXEを再生成し、収録ファイルの整合性を確認した。[原因・設計・検証](11-history-coverage-fix.md)参照。

## 2026-11-02 8kHz通常モードのステップ表示修正

XInputのpacket番号の非連番を差分境界にしていたため、通常モードでは全4軸の有効差分が0件になる経路があった。非連番は参考診断として残し、実際に取得した隣接値の差分を計算するよう修正した。既知のキュー破棄・無効値・epoch境界は維持。GUI/CLIをビルドし、実装時はユーザー指定により試験を見送った。その後の回帰試験でXInput14件が合格したが、Windows実機は未確認。[原因・根拠・確認範囲](12-xinput-observed-step-fix.md)。

## 2026-11-02 ステップ精度ヒストグラム

各取得差分のbit値を最新30秒・4軸合算で、1bit幅のヒストグラムとして表示する機能を追加。edge追加/失効の増分カウンターを使い、左右カード下へ1つのグラフを置く。実装時はユーザー指定により試験を見送り、その後の指示で回帰試験・負荷測定を実施した。WPF画面確認は未実施。[集計単位と配置](13-step-histogram.md)。

## 到達範囲

| 段階 | コード | 検証 |
| --- | --- | --- |
| M1 構成・契約・取得経路 | 実装済み | SDK/依存固定、全体クロスビルド。Windows実機PoC未確認 |
| M2 Core・デモ・試験移植 | 実装済み | Python同一fixture15ケースを含む計算試験、デモ統合 |
| M3 HID直接 / XInput | 実装済み | 記述子/状態機械/モック。実Win32と機種別入力未確認 |
| M4 WPF位置・精度GUI | 実装済み | WPF本体とSTAテストをクロスビルド。実行未確認 |
| M5 震え3指標/グラフ | 実装済み | 統計・間引き・無効区間試験、5分相当の合成負荷。Windows描画/実時間連続運転未確認 |
| M6 CLI・設定・配布・切替 | 実装済み | CLI/CSV/設定試験、既存CSV読み戻し、win-x64生成。クリーンWindows起動未確認 |

M1の実機確認を済んだものとして扱っていない。現在の環境で進められる実装・合成/モック・クロスビルド・配布生成を完了し、実機依存項目を[Windows確認手順](windows-m6-validation.md)へ残した。


## 性能改善R1〜R4の最新状態

R1 UI更新世代/頻度分離、描画・静的資源キャッシュとbounded計測、R2 worker専用窓/scratch・一時配列削減・最新位置分離、R3増分窓統計/最頻session/描画投影を実装済み。全16プロジェクトbuildは警告0・エラー0、Core55/HID21/XInput12/Application29/CLI13の計130ケースが合格。WPF19ケースはcompileのみでWindows実行していない。

R4向けにGUI `--performance-log PATH`、5秒JSONL、上限8件の非同期writer、16MiB上限、終了時drain/flush、およびmacOS mock/仮想負荷の計測を用意した。Windows実機・WPF描画、性能目標の受入は未完了。5分の実時間モックでは定常割当8.78〜9.00MiB/秒、4軸解析p95 2.67ms。改善後GUI/CLIの自己完結型配布物を再生成した。条件・生ログ・制約は[09-performance-implementation.md](09-performance-implementation.md)に記録した。

## 実装内容

16プロジェクトの`Gpss.sln`、SDK 10.0.403、依存lockファイル、C#実行/テスト/配布経路を用意した。

- Core: 4軸独立30秒窓、300,000点/軸上限、epoch/continuity、母標準偏差・平均・平均からの最大絶対偏差、最頻差分推定とregistry。描画用縮約は統計から分離。
- Input: 上限8,192フレーム/16MiBのキュー。HID直接のSetupAPI/Capabilities/1〜32bit signed/unsigned/Overlapped I/O/CancelIoEx、XInput初回とpacket更新、通常/明示的低遅延モード、C#デモ。M6でtrigger補助値とopen時の記述子再照合を追加。
- Application: 単一writer解析タスク、100ms統計、epoch/CASによる旧表示隔離。描画Snapshotは1024バケットのfirst/min/max/last、最大8192点/軸。
- Desktop: 左右の円と現在点、軸/代表bit、診断・割当プレビュー。各X/Yの3指標を常時表示し、位置波形・平均線・標準偏差帯を最新30秒で図示。欠測境界、空窓、保持上限を表示。
- Settings: `%LOCALAPPDATA%/GPSS/settings.json`、SchemaVersion=1、同一ディレクトリの一時ファイルとFlush後の置換。HIDのパス/VID/PID/全記述子のSHA256照合、XInputはAPI共通割当。破損時は警告し既定値へ復帰。選択はヒントだけ保存し自動接続しない。
- CLI/Recording: 有限観測件数、準備/Enter開始、全期間オンライン3指標、逐次全軸CSV、時間上限/Ctrl+C/入力失敗終了。旧8列互換とメタ情報付きv2、既存CSV解析。[CSV仕様](06-csv-format.md)参照。
- Publish: GUI/CLIのself-contained win-x64、各1つのEXEへDLL/ランタイムを内包。trim/AOT/内部圧縮無効、依存ライセンスを同梱したZIP。[単一EXE配布](10-single-file-distribution.md)。root build/runスクリプトをC#へ切替。

機種別専用デコーダーの追加口はあるが、未確認機種の専用profileは登録していない。DS4/DualSense/Switch等の全モードを対応済みとはしない。HIDの切断はreadエラーと約1秒ごとの再列挙で確認し、CM_Register_Notificationは使用していない。

## ビルドと自動テスト

macOS 14.6 / ARM64、SDK **10.0.403**、ランタイム10.0.14。作業SDKとNuGetキャッシュは`/private/tmp/`を使用し、グローバルSDKは変更していない。xUnit 2.9.3、adapter 3.1.4、Test.Sdk 17.14.1。

```sh
dotnet restore Gpss.sln --locked-mode --disable-parallel --disable-build-servers -m:1
dotnet build Gpss.sln --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false
sh scripts/Test-CSharp.sh
```

最終コードで全16プロジェクトのロック付き復元とビルドが成功し、**警告0・エラー0**。M6時点の記録は103件。後続の全体検証では**130件すべて合格**（Core55、HID21、XInput12、Application29、CLI13）。M6のログは`TestResults/m6-build.log`と`TestResults/csharp-m6.trx`、性能改善後は`TestResults/performance-build.log`と`TestResults/performance.trx`。デモ100件のCLI記録と読み戻しでも3指標が一致した。WPF試験19ケースはcompileのみで、macOSでは実行していない。公式NuGetから復元し、各projectのlockで固定した。共通RuntimeIdentifiersにwin-x64を明示し、通常restoreとRID付きpublishの両方で同じlockを使う。

Python v1の同一入力fixture15ケースは`tests/fixtures/core-parity-v1.json`に保持し、C#実行時にPythonを呼ばず比較する。CLIはデモのlegacy/v2記録、時間上限、入力エラー、複数機器選択、欠測、25列幅、全22既存CSV×4軸を試験する。設定は保存/読戻し、破損、未知schema、異なるパス/記述子、文化圏、反転、XInput共通割当を試験する。

HID補助値の試験はfake decodeからframeへの受け渡しで、HidPの実動作ではない。XInput trigger境界はmock APIを使用する。モックのキャンセル/解放順の合格はWindowsカーネルI/Oの検証ではない。

外部ドライブ上の並列.NETプロセスでファイル共有エラーが出たため直列ビルドとした。テストランナーのlocalhostソケットにはsandbox外実行許可を使用した。生成ログは`TestResults/`に保存（gitignore対象）。

## 合成負荷試験（M6時点の履歴）

以下は最適化前の記録。最新の600仮想秒/実時間モック結果は[09](09-performance-implementation.md)を参照。

`tools/Gpss.Benchmarks`をReleaseで実行する。各300秒の仮想時刻を使い、4軸の窓追加/期限切れ/統計/最頻推定/描画縮約を100msごとに再計算する。1k/8kHz HID相当と、100Hz状態更新・30秒状態不変が混在するXInput相当を含む。上限/欠落/空窓は別の決定的Core試験でも確認する。

これは実時間5分のWindows入力→WPF通し試験ではない。ネイティブ受信・UI描画・スケジューリングを含まない。解析時間と保持メモリの目安として扱う。全量Snapshotを再計算する参照方式の割当/GC負担は、2026-11-01の通常XInput条件の問題報告を受け、優先的な改善対象とした。増分集計等の仕様は07に記載。


| 系列（各300秒） | 解析p95 / 最大 | 最大点数/軸 | 保持ヒープ40秒→終了 | 総割当 |
| --- | --- | --- | --- | --- |
| 1000Hz HID-like | 3.92 / 13.31ms | 30,001 | 6.17→6.23MiB | 39.79GiB |
| 8000Hz HID-like | 28.60 / 62.92ms | 240,001 | 48.35→48.41MiB | 290.85GiB |
| 100Hz XInput-like updates/unchanged | 0.72 / 1.27ms | 3,001 | 1.10→1.16MiB | 7.25GiB |

全系列でp95<100ms。最終GC後の計測でも窓への参照を保持し、ライブヒープを比較した。描画へ渡す最大点数は2851/3107/2287点/軸。全GC世代の回数と累積割当は`TestResults/m6-performance.jsonl`に記録した。総割当は累計であり常駐メモリ量ではない。

## 配布物（M6時点の記録）

旧M6版（現在の`artifacts/GPSS-win-x64-M6-baseline.zip`）にGUI/CLIと.NET 10.0.14ランタイム、操作説明、CSV仕様、Windows確認手順、製品/依存ライセンスを含む。EXEとネイティブDLLのx64 PE形式、runtimeconfigのincludedFrameworks、必須ランタイムファイル、ライセンス、全ファイルSHA256とZIP CRCを確認した。配布ZIPは636ファイル、99,334,077 bytes（約94.7MiB）。隣にZIP自身の`.sha256`を出力した。これらの静的確認をWindowsでの起動成功とは扱わない。

生成は`scripts/Publish-Windows.ps1`（root build.ps1/build.batから呼出し）。今回macOSでは同じdotnet publishコマンドとコピー/ZIP生成を実行した。PowerShellスクリプト自体のWindows実行も受入対象。

## 旧Python資産の整理

M6移行時には復元用アーカイブを作成して保全した。その後、2026-11-02のユーザー指示により、旧ソース・テスト・設定・アーカイブ・仮想環境・キャッシュ・旧文書を削除した。従来の「Windows全受入まで保持」という方針は最新指示で変更した。`data/`の測定CSVと `tests/fixtures/core-parity-v1.json` は現行C#の検証用途で保持する。[削除一覧](14-legacy-python-cleanup.md)参照。これは試験実行の追加ではない。

## 担当

GPT-6 Lunaの子3人が震えUI、CLI/CSV、設定保存を分担。親が契約/入力補助値/統合/性能試験/配布/文書を担当し、失敗した試験を修正して全体を再検証した。指定M6まで実装し、追加機能や未確認機種の対応を推測で加えていない。
