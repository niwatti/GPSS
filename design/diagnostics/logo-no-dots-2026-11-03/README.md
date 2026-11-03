# ロゴ内の二点除去の検証

2026-11-03。内蔵image_genの編集で、白・ティールのリング内にある青い点を両方取り除いた。背景タイル・リング・透過は維持し、現行のPNG・9サイズICOを更新した。[現行資産と編集プロンプト](../../../assets/branding/README.md)参照。

- `before-asset-sha256.json`: 修正前のPNG・ICOのハッシュ。
- `asset-validation.json`: 修正後のPNG・ICOのハッシュ、1254pxのマスター、透過、16／20／24／32／40／48／64／128／256pxのRGBA確認。元の点の鮮やかな青に相当する画素の検査結果も記録した。
- `logo-no-dots-build.log`: 全16プロジェクト、警告0・エラー0でクロスビルド。WPF19ケースはコンパイルのみ。
- `logo-no-dots-publish-*.log`: GUI／CLIの単一EXE再生成。
- `branding-validation.json`: 更新ICOの全9フレームを両EXEのRT_GROUP_ICON／RT_ICONに、同じICOをWPF.g.resourcesに照合した結果。
- `bundle-validation.json`: 全バンドル入力とnative hostの照合。
- `source-sha256.json`: 現行ソース・設定・資産のハッシュ。
- `logo-no-dots-lock-check.json`: 置換前の標準出力のロック確認。

画像内容の編集はimage_genを使い、Pillowでは形式変換とICOへのリサイズだけを行った。計算・入力・描画等のC#コードは変更していない。機能・性能試験の追加は行わず、資産・ビルド・埋込内容の検査を行った。Windows実起動・実表示は未確認。

出力先は `artifacts/win-x64/` に統一したまま更新した。`TestResults/logo-no-dots-staging/` は一時生成先で、置換と配布検査の後に削除する一時領域。旧ZIPは `artifacts/GPSS-win-x64-before-logo-dot-removal.zip` に保全した。配布は `artifacts/GPSS-win-x64.zip` を更新し、ZIP自体の検査結果は循環ハッシュを避けてこのディレクトリのZIP外に保存する。
