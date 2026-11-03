# 不要になった旧Python版資産の削除

更新: 2026-11-02。ユーザーの明示指示により、現行C#の実行・ビルド・検証で参照されないPython時代の資産を削除した。従来の移行アーカイブ保持方針を変更する。

## 削除対象

- `archive/python-v1/`: 旧スクリプト、Pythonパッケージ、Python試験、pyproject、pygame/PySide6/PyInstaller設定、旧ビルド/起動スクリプト、pycache。
- `archive/gpss-python-v1-source.zip`: 旧Python版復元ZIP。
- `design/archive/python-v1/`: 旧Python版の設計・実装記録。
- `README.python.md`: 旧Python版の復元・実行手順。
- `.venv/`: Python版の仮想環境。
- `.pytest_cache/`: Python試験キャッシュ。
- `src/gpss.egg-info/`: Python版のインストールメタデータ。
- `.gitignore` のPython専用ルールも整理する。

## 現行用途のある資産

測定CSV `data/` はC# CLIの読み戻し等に使う。比較fixture `tests/fixtures/core-parity-v1.json` と `PythonParityTests.cs` はPythonを呼ばずにC#計算の移植一致を確認する現行資産として保持する。C#の旧解析アセンブリを含む `archive/csharp-m6-analysis-baseline.zip` と性能診断資料、旧Raw InputのC#設計もPython版資産とは別のため保持する。

上記7か所の削除を完了し、削除前後でCSV/fixture計23ファイルのSHA256一致を確認した。空ディレクトリに残ったmacOSメタデータも除去している。確認はファイル整理の整合性だけであり、今回試験・ベンチマーク・実機確認は実施しない。現在のbuild/runスクリプトはC#用のため保持する。

旧Python版への現行文書のリンクを除去し、配布ZIPにも整理後の文書を反映する。削除結果は `TestResults/python-cleanup.json` に記録する。
