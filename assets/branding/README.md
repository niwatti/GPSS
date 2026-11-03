# GPSS ロゴ — Twin Scope

左右のスティックを二つの測定リングで表す。白とティールのリングを暗い角丸タイルに載せ、文字を入れず、ウィンドウ左上・タスクバー・エクスプローラーで同じマークを使う。ユーザー指示により、初版にあったリング内の青い点は両方取り除いた。

- `gpss-logo.png`: 内蔵image_genで生成した1254×1254のマスター。角丸タイルの外側は透過。
- `gpss.ico`: 16／20／24／32／40／48／64／128／256px、32bit RGBAの9フレーム。生成画像のリサイズとICO形式への変換のみ行った。
- アイコンはGUI／CLIのApplicationIconへ指定し、WPFには同じICOをResourceとして埋め込む。各ウィンドウはpack URIで読み込む。実行時に外部画像ファイルやPythonは不要。
- 配布の `assets/branding/` はロゴの再利用用で、アプリの起動には不要。

2026-11-03、内蔵画像生成ツールを使用。CLI/APIフォールバックは使用していない。取得元の生成画像をこのプロジェクトへコピーして保持した。

## 現行版の編集プロンプト

内蔵image_genの編集を使用し、角丸タイル外側の透過を維持した。CLI/APIフォールバックは使用していない。

```text
Use case: precise-object-edit. Edit the attached GPSS logo, do not redesign it. Remove ONLY the two solid cobalt-blue circular dots: one inside the left white ring, and one inside the right teal ring. Fill the removed dot areas seamlessly with the exact surrounding dark navy interior so both rings have completely empty dark navy centers. Preserve all other details: the two ring shapes and their connection, their white and teal colors, their size and stroke thickness, the dark navy rounded-square tile, its shape and position, the existing composition, scale, texture and shading. Preserve the transparent pixels outside the rounded-square tile. Do not add any new dots, symbols, highlights, crosshairs or text. Return only the edited app icon.
```

編集設定: `transparent_background=true`。初版と現行版のハッシュ・確認結果は[点除去の検証資料](../../design/diagnostics/logo-no-dots-2026-11-03/README.md)へ保存する。

## 初版生成プロンプト（履歴）

```text
Use case: logo-brand. Asset type: final Windows application icon and logo for GPSS (Game Pad Stick Stats), a tool that measures gamepad analog stick positions, step precision and jitter. Create ONE original, production-ready centered square app icon, no text, no letters. Concept: two bold circular measurement rings representing left and right analog sticks, horizontally paired with a small off-center solid position dot inside each ring. Make the two rings feel like one distinctive connected instrument / game controller mark. Very simple, flat, crisp vector-like geometry, thick consistent strokes, generous negative space, exceptionally clear at 16x16, 24x24 and 32x32. Use the application's existing cobalt blue (#2563EB) and teal (#0F766E), with white for contrast, on a deep navy rounded-square tile. Make one ring white and one bright teal, cohesive rather than decorative. Tile fills roughly 90 percent of the square canvas with modest even margin; transparent pixels OUTSIDE the rounded-square silhouette, opaque deep navy interior. Front view, no perspective, no shadows, no gloss, no gradients, no glow, no texture, no thin crosshair lines, no extra buttons or objects, no surrounding presentation board, no watermarks. Deliver only the single isolated finished app icon on a genuinely transparent background.
```

生成設定: `transparent_background=true`。Windowsでの実表示は別途受入確認とする。[ロゴ・アイコン設計](../../design/18-logo-and-icons.md)参照。
