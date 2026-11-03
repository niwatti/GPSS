# 配布出力先統合の検証資料

2026-11-03。[統合設計](../../19-output-consolidation.md)参照。アプリ本体とEXEのバイト列はロゴ版から変更していない。

- `output-consolidation-lock-check.json`: 整理前の14個の配布・一時EXEをリネームして元に戻せたことを確認した結果。
- `output-consolidation.json`: 最新版の全manifestファイルを標準パスへ照合コピーし、配布側7フォルダー・TestResults側3フォルダーを削除した記録。削除エラーなし。
- `branding-validation.json`: 標準パスの両EXEで9つのアイコンフレームと、WPF埋込ICOを検査した結果。
- `bundle-validation.json`: 標準パスから全バンドル内容とpre-bundle hostを再照合した結果。
- `verification.json`: 唯一の配布ルート、各アプリ1EXE、ロゴ版と同じSHA256、一時出力削除とソース不変の確認。
- `publish-script-sha256.json`: 更新したPublish-Windows.ps1のハッシュ。

標準パスからの検査例（SMBでの共有ロックを避けるため直列で実行）:

```sh
dotnet design/diagnostics/logo-2026-11-03/bin/Release/net10.0/VerifyBranding.dll assets/branding/gpss.ico src/Gpss.Desktop/bin/Release/net10.0-windows/win-x64/Gpss.Desktop.dll artifacts/win-x64/desktop/Gpss.Desktop.exe artifacts/win-x64/cli/Gpss.Cli.exe
dotnet design/diagnostics/single-file-2026-11-02/bin/Release/net10.0/InspectBundle.dll artifacts/win-x64/desktop/Gpss.Desktop.exe src/Gpss.Desktop/bin/Release/net10.0-windows/win-x64 src/Gpss.Desktop/obj/Release/net10.0-windows/win-x64/singlefilehost.exe artifacts/win-x64/cli/Gpss.Cli.exe src/Gpss.Cli/bin/Release/net10.0/win-x64 src/Gpss.Cli/obj/Release/net10.0/win-x64/singlefilehost.exe
```

初回に同じSMB上のEXEへ二つの検査を同時実行すると共有ロックで片方が失敗したため、検査を直列化した。配布先を再分割したり、EXEを変更したりして対処していない。

アプリ本体を変えず、出力配置と配布スクリプトだけを変更したため、機能・性能試験の再実行は行っていない。PowerShellがこの環境にないため、ロック時のPowerShell実行とWindows実起動は未確認。最新版のEXEの同一性と配布物の静的検証を区別する。

ZIPは標準フォルダーから生成し、全manifestハッシュ・CRC・内容一致を確認する。循環ハッシュを避けるため、ZIP自体の `package-validation.json` はZIP外のこのディレクトリに保存する。旧ZIPのアーカイブは削除していない。
