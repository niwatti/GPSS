# 旧Raw Input専用設計の参照先

2026-10-28に、取得方式をHID直接読み取り＋XInput互換へ変更した。

- 現行の取得設計: [05-input-acquisition.md](05-input-acquisition.md)
- 旧Raw Input専用設計（過去資料）: [archive/raw-input-v2/05-raw-input.md](archive/raw-input-v2/05-raw-input.md)

WM_INPUT/GetRawInputDataを使う旧手順は現計画の実装対象ではない。実装段階は[03-stages.md](03-stages.md)のM1〜M6を使う。
