# GPSS ロゴとアプリアイコン

2026-11-03。「ロゴを考え、EXEとウィンドウ左上のアイコンへ設定する」という指示に基づく。

## モチーフと資産

名称はTwin Scope。左右のアナログスティックを二つの測定リングで表す。暗い角丸タイルに白・ティールのリングを載せる。ユーザーの追加指示でリング内の二つの青い点を取り除き、リングのみの表現へ更新した。文字を省き、16pxの小さい表示でも形を判別できるようにする。

内蔵image_genでPNGを生成し、ICOへの形式変換と必要なサイズへのリサイズを行った。生成マスターは `assets/branding/gpss-logo.png`、使用するICOは `assets/branding/gpss.ico`。16／20／24／32／40／48／64／128／256pxの9フレームで、32bit RGBA・角丸タイル外側は透過。生成プロンプトと出所は[資産README](../assets/branding/README.md)へ保存する。既存の計算ロジックと入力取得には変更を加えない。

## EXEとウィンドウ

GUI・CLIのcsprojへ同じICOのApplicationIconを設定し、エクスプローラー等で使うWindows PEのアイコン資源に含める。GUIではさらにICOをWPF Resourceとして埋め込み、`AppBranding.CreateWindowIcon()` がassembly修飾のpack URIから読み込む。ディスク上の外部ICOやAssembly.Locationへ依存しない。

メイン画面、取得設定、軸割当、精度詳細の各Window.Iconに同じマークを設定する。各ウィンドウでOnLoad読込みとFreezeを行い、ネイティブのウィンドウサイズ・DPIに合うフレームを渡す。テストの複数STA間でdecoderを共有しないよう、読み込みを各ウィンドウの作成スレッドで行う。

配布の `assets/branding/` は再利用用の元画像・ICO・READMEで、起動には不要。アプリのフォルダーは引き続き単一EXEだけとする。現行の生成済み出力先は `artifacts/win-x64/`、配布ZIPは `artifacts/GPSS-win-x64.zip`。ロゴ作成時の一時出力先 `artifacts/win-x64-logo/` は後続の統合指示で整理した。[出力先統合](19-output-consolidation.md)参照。旧ZIPは `artifacts/GPSS-win-x64-before-logo.zip` に保全する。

## 確認

PillowでICOの全9サイズ・RGBA形式と透過を確認し、16〜64pxの実データを並べた検査用画像で形を確認する。全16プロジェクトは警告0・エラー0でクロスビルドし、WPF19ケースにはアイコンの読み込み・サイズ確認を含める。WPF実行はWindows専用のため、この環境ではコンパイルのみ。

ポータブルな検査コードで、GUI／CLIのPEに含まれるRT_GROUP_ICONとRT_ICONを元ICOの全フレームへバイト単位で照合し、WPFの.g.resourcesに同じICOが含まれることを確認する。既存のバンドル検査では全収録ファイルとpre-bundle hostを照合する。Windows実行や実際のタスクバー描画をこの静的確認の代わりに受入済みとはしない。

実行結果は両EXEとも唯一のアイコンgroupに9フレームがあり、元ICOと全バイト一致。WPF埋込資源も同じICOだった。GUI389件／CLI182件の収録ファイルとnative hostの照合も成功した。CLI回帰13ケースは合格。計算・入力処理を変えていないため、今回のアイコン変更で188件全体の再実行や負荷試験は追加していない。直前の期間選択の188件合格と、今回の検証を分けて記録する。

WindowsではZIPから各EXEだけを空フォルダーへ移して起動し、エクスプローラーのアイコン、メイン画面・各ダイアログの左上、タスクバー、Alt+Tabで確認する。100／125／150／200%DPI、明るい／暗い背景でも輪郭を確認する。同名の旧EXEと同じ場所へコピーした場合、Windowsのアイコンキャッシュに旧表示が残る可能性は実機で切り分ける。DLLや画像の外部配置が不要なことも確認する。

ログ・資産ハッシュ・ICO／PE／WPF資源の照合結果は[検証資料](diagnostics/logo-2026-11-03/README.md)へ保存する。

## リング内の二点の除去

2026-11-03、内蔵image_genの編集で両方の点を除去し、周囲の紺色で埋めた。リング・接続形状・角丸タイル・透過を維持する指示を用いた。現行PNG・ICOを更新し、標準の `artifacts/win-x64/` と配布ZIPへ反映する。出力先は再分割しない。実測スティックの位置を表示するGUI本体の点は、このロゴ修正の対象ではない。

現行版の確認は[点除去の検証資料](diagnostics/logo-no-dots-2026-11-03/README.md)へ記録し、点があった初版の検証記録と区別する。
