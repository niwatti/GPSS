# C#移行の複数エージェント実行手順

## 起動条件と上限

ユーザーがC#移行のM番号による上限を指定した後、親が複数の子エージェントを起動して実装する。2026-10-28にM6までの実装指示を受けた。現在はM1〜M6のコードを実装し、Windows実機の未確認項目を記録している。旧Python段階3の実装許可を新計画へ引き継がない。

```text
gpss/design/README.md と現行設計01〜04と05-input-acquisition.md、implementation-status.mdを読む。
C#移行M<N>までを累積実装する。M<N+1>には進まない。
親が共通契約/プロジェクト/統合を担当し、契約確定後に2人以上の子を起動する。
新規の子にはmodel="gpt-6-luna", fork_turns="none"を明示し、必要な契約と進捗を渡す。
Python資産とdataはM6の受入前に消さない。
担当外ファイルの編集は親へ提案し、所有権を調整してから実施する。
純粋計算・合成入力・Windows API・WPF・実機の検証を区別する。
Windows/機器がない試験を合格として報告しない。
根拠をimplementation-status.mdへ残し、指定上限で停止する。
```

## 子エージェントのモデル指定

2026-10-28のユーザー確認により、今後新しく起動する子エージェントは **GPT-6 Luna（`gpt-6-luna`）**を使用する。親エージェントのモデルは変更しない。最初のGPT-5.6 Luna指定は、この確認でGPT-6 Lunaへ更新した。

起動時は `model="gpt-6-luna"` と `fork_turns="none"` を明示する。新規コンテキストに設計の場所・担当範囲・共通契約・受入条件・進捗を引き継ぐ。既存の子エージェントのモデルを変更したとは扱わず、モデルを揃える必要がある作業は新規の子へ引き継ぐ。

指定モデルが利用できなくなった場合は別モデルへ黙って置換せず、その制約を報告する。M5/M6実装ではLunaの子3人に震えUI、CLI/CSV、設定保存を分担した。親が共通契約、入力補助値、統合、性能試験、配布と文書を担当した。

## 所有権

親+子最大3人を想定。利用可能枠が少なければバッチを分ける。同じプロジェクト/共有設定を複数人が同時編集しない。

| 担当 | 所有 | 責任 |
| --- | --- | --- |
| 親/統合 | solution、global.json、共通モデル/インターフェース、Application、設定、CLI、ビルド/CI、README/design | 契約、依存、統合、上限、Windows受入と記録 |
| 子A/Core | Gpss.Coreの計算/窓（共通モデル除く）、Core.Tests | 軸別時間窓、震え、推定器、数値一致 |
| 子B/入力 | Gpss.Input.HidDirect、Gpss.Input.XInput、対応Tests、取得fixture | PInvoke、非同期read/キャンセル、HID解析、XInput packet、取得可否 |
| 子C/UI・デモ | Gpss.Desktop、Input.Simulation、Desktop.Tests | WPF、軸割当、デモ、表示。M2はデモ/fixture比較に集中 |

入力方式を追加してもCore/UIに機種別バイト解析を持ち込まない。HID/XInputの試験で必要なWindows・実機の有無を明記する。契約ファイルと.csprojは原則親が編集する。解析担当がInput.Abstractionsを勝手に変更しない。親は型、時刻単位、Report ID/順序、nullable、epoch/continuity、エラー、終了責任を先に配布する。

## 実行順序

1. 親は指定M上限、AGENTS.md、既存差分、.NET/Windows/実機の有無を確認する。現在のPython実績を読み、新版の完了と混同しない。
2. M1で共通契約とソリューションを親が作成。子BがHID直接/XInputのPoC、他の子がfixture/既存振る舞いの対応表を作る。Windowsが利用できなければ環境に依存しない仕事を先に進め、実機PoCは未確認のまま明示する。
3. M2は子AのCore移植と子Cのデモ/fixtureを並行。親が同一系列のPython/C#期待値比較とApplicationを統合する。旧100msルール等、新仕様への変更を試験に反映する。
4. M3は子Bの取得製品化と子Cの最小診断/割当UIを並行。親は選択/リセット/切断/欠落の経路を接続。M1の取得可否確認をスキップして対応機種を断定しない。
5. M4は子CのWPF本画面、子Aの推定器拡張試験、子Bの受信負荷/機器試験を分担。親が実入力→解析→GUIの通し試験を行う。
6. M5は子Aの計算/性能、子Cの震え表示/間引き、子Bの負荷fixtureを分担。表示間引きと統計入力を混ぜない。
7. M6は親がCLI/配布/切替、子が入力互換性/GUI/受入を分担。移行時は内容ハッシュ付きの復元用アーカイブを作成して退避した。後続のユーザー指示により、旧Python資産と復元アーカイブは削除済み。[整理記録](14-legacy-python-cleanup.md)を参照し、現行C#のfixtureと測定データを維持する。
8. 子は変更ファイル、試験コマンド、結果、未確認事項を返す。親がレビューと全体検証を行い、不備を担当へ戻して解消する。実装上限と受入済み範囲を記録して停止する。

## 子への指示例

```text
担当: HID直接/XInput。許可上限: M3。
必読: design/01-specification.md,02-architecture.md,03-stages.md,05-input-acquisition.md。
所有: src/Gpss.Input.HidDirect、src/Gpss.Input.XInputと対応tests/fixture。
契約/solution/csproj/他UIは親へ変更提案し直接編集しない。
非同期readの寿命/キャンセル、Report ID、signed/unsigned、XInput不変/更新/周回/飛び、切断とqueue lossを検証。
完了/更新観測時刻をハードウェア時刻と称さず、固定1kHzへの再サンプリングをしない。
XInputのAPI呼出し数・状態更新数とHIDレポート受信数を区別する。
Windows API試験とモック試験、実機機種を分けて報告する。
完了時にAPI、変更一覧、試験と未確認点を送る。
```

実装判断は設計範囲で自律的に進める。HID直接/XInput以外の入力方式を追加する、時間重み付き震えへ変更する、推定式を変える等の要件変更は親へ報告し、設計とユーザー意図を確認してから進める。上限未指定、権限不足、Windows/機器不足は根拠付きで示し、実行可能な独立作業は継続する。

## 2026-11-01 性能改善時の追加手順

[07-performance.md](07-performance.md)、[09-performance-implementation.md](09-performance-implementation.md)、[再現資料](diagnostics/performance-2026-11-01/README.md)を参照する。R1〜R3は実装済み。R4の計測機構は追加済みだがWindows実測・受入が残る。比較測定は同時に走らせない。

M6時点の103試験と解析単体p95は過去の値として保持し、性能改善の合格根拠に転用しない。R1〜R3の実装とR4 Windows受入を区別し、Windows表示を測れていない間は操作性改善を断定しない。新規子は既存方針どおりgpt-6-lunaを指定する。
