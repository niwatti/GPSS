# Windows Raw Input / HID取得詳細

対象: C#移行M1（調査/PoC）、M3（製品入力層）、M6（互換性確認）。本書は設計であり、Raw Input版は未実装。

## 取得できるもの

Raw Input APIでWindowsのHID入力レポートを受信し、HIDパーサーで軸値を読む。USB転送やセンサーADCを直接読むAPIではない。ドライバーの変換・機器のファームウェア処理・接続方式の影響は残る。

XUSBドライバーにもHIDインターフェースの公開例があり、Xbox系を一括で「Raw Input不可」とは扱わない。一方、すべてのXInput互換機・仮想機器・USB/Bluetoothモードが同じHIDを公開するとは仮定しない。Raw Inputで見える機器ごとにTLC/軸/レポートを確認する。取得できない機器は未対応と表示し、XInput/DirectInput/GameInputへ自動フォールバックしない。将来の追加入力源はIInputSourceで拡張できるが、今回の移行対象はRaw Input。

## 列挙・登録・メッセージ

1. 専用スレッドで受信用message-only HWNDを作成し、メッセージループを開始する。WndProc delegateをウィンドウ破棄まで保持する。
2. `GetRawInputDeviceList`で列挙し、`GetRawInputDeviceInfo`のRIDI_DEVICENAME/RIDI_DEVICEINFO/RIDI_PREPARSEDDATAから情報を取得する。サイズ照会後の実取得でもサイズ変化/切断を検証する。
3. UsagePage=0x01（Generic Desktop）、Usage=0x04（Joystick）と0x05（Game Pad）を`RegisterRawInputDevices`に登録。対象HWNDを指定してRIDEV_INPUTSINK | RIDEV_DEVNOTIFYを使う。キーボード/マウスを登録せず、RIDEV_NOLEGACYも使わない。
4. 初期列挙とWM_INPUT_DEVICE_CHANGEの到着/除去を突き合わせる。同じ物理機器の複数TLCを独立に識別し、TLC間の軸を自動合成しない。選択したデバイスハンドルの入力だけを計測へ渡す。
5. WM_INPUTでは`lParam`のHRAWINPUTから`GetRawInputData`をサイズ照会→取得する。参照先はWndProc内で読み切り、コピー済みデータだけをキューへ渡す。`RAWINPUTHEADER.dwType == RIM_TYPEHID`を検証。
6. RAWHIDの`dwSizeHid * dwCount`をchecked演算し、取得バッファの範囲内か確認する。1つのWM_INPUTに複数のレポートが含まれるため、各レポートを順に切り出す。先頭1個だけを処理しない。
7. RIM_INPUTの後処理に必要な`DefWindowProc`を正しく呼ぶ。現在のメッセージを読み取る前にcleanupしない。初期実装はGetRawInputData方式に統一する。将来GetRawInputBufferを使う場合は現在のメッセージとの二重処理/読み飛ばしを試験してから追加する。
8. アプリ全体で登録を1か所に所有する。同じプロセス内で複数のウィンドウ/部品が同じクラスへ登録しない。停止時はRIDEV_REMOVEとNULL targetで解除する。

message-only HWNDでのバックグラウンド受信とホットプラグはM1 PoCでWindows実機検証する。登録失敗、デバイス消失、API失敗は理由とWin32 error codeを残す。

## HIDパースと軸割当

- Preparsed Dataをデバイスの接続世代ごとに保持し、`HidP_GetCaps`、`HidP_GetValueCaps`でInput value capabilitiesを読む。バッファの所有/寿命と解放方法を取得APIに合わせる。RIDI_PREPARSEDDATA用の自前バッファをHidD_FreePreparsedDataに渡さない。
- 軸キーはUsagePage/Usage/LinkCollection/ReportId。CapabilitiesのRange/NotRangeの両方を展開する。Report ID、ReportLength、絶対値軸かどうか、BitSize、論理範囲、Null値を検証する。
- 初期版は1〜32bitのスカラー絶対値軸を対象とする。ReportCount>1のvalue array、相対軸、ベンダー独自の不透明なペイロード等は検出し、汎用パーサーで読めない理由を明示する。バイトoffsetを推測して読み取らない。
- `HidP_GetUsageValue`は符号付けやスケール変換をしない。LogicalMinが負ならBitSizeに従い符号拡張する。unsignedのLogicalMax解釈も含め、32bit境界のシフトをulong/longで扱う。論理値をそのまま保持するため、物理単位への変換で量子化し直さない。
- `HIDP_STATUS_INCOMPATIBLE_REPORT_ID`など、別Report IDに属する軸が今回ないケースをパース破損と混同しない。当該レポートが本来含む軸のパース失敗/Null/範囲外値は欠測として連続性を切る。
- 変換式は `normalized = 2.0 * (logicalValue - logicalMin) / (logicalMax - logicalMin) - 1.0`。min/maxが有効か検証し、範囲外をclampして正常値にしない。double変換前の整数と範囲を保存する。軸反転は変換後に1回だけ適用。
- 既定候補は左X/Y=0x30/0x31、右Rx/Ry=0x33/0x34。Z/Rzを右スティックに使う機器もあるため、Usageだけでトリガーと断定しない。候補が一意でない/軸不足なら「軸割当が必要」とし、M3の診断・割当画面でユーザーが確認する。軸ごとの生値と反応を見ながら4軸を選べるようにする。永続保存はM6。

0..255を線形変換するとステップ差は2/255、段階数はユーザー指定式では255となり、表示は約8.0bit。HIDのBitSize=8（格納幅）や値の種類数256を推定段階数へ代入しない。符号付き範囲のゼロが正規化後に厳密な0にならない場合も、中心補正やデッドゾーンを無断で追加しない。

## レポート・サンプル・時刻

- WM_INPUTを処理した時点のStopwatch tickを受信時刻として記録する。ハードウェア生成時刻ではない。RAWHID内の各レポートは同じ受信時刻でもReportIndexで順序を保持し、架空の1ms間隔を割り当てない。
- レポートが含む軸だけAxisReadingを生成する。別Report IDで左/右が届く場合も軸別に窓と差分を管理し、欠けた軸の前回値を複製して件数を増やさない。画面の位置点はX/Yそれぞれの最後の有効値から合成し、取得時刻の差を確認可能にする。
- 同じ値のレポートも受信したら保持する。差分ゼロだけをステップ推定から除き、震えの計算には含める。レポートが来ない時間には新しいサンプルを生成しない。
- 受信連番はアプリが付けた順序。デバイス側の全欠落を検出する連番ではない。アプリキューでの破棄/既知のパース失敗は連続性IDに反映し、その境界を跨いで差分を作らない。
- 受信が100ms途切れても、静止時にレポートが来ない機器があり得るため、自動的に切断/不連続とはしない。実切断、選択/割当/接続世代の変更、既知の欠測で境界を作る。旧Python版の100msルールはRaw Input版へ持ち込まない。
- 現在時計で窓を期限切れにし、30秒無受信なら統計はデータなしとなる。接続は維持されていても点には「最終受信値」と年齢を表示し、更新し続けているように見せない。受信ループ自体の異常と単なる無受信は分ける。
- レート表示は「受信HIDレポート/秒」「軸別サンプル/秒」を分ける。UI更新fps、WM_INPUTメッセージ数、USB公称ポーリングレートと混同しない。

## P/Invokeと検証

User32.dllとHid.dllのユーザーモードAPIをC#から呼ぶ。LibraryImportを基本とし、必要な箇所のみDllImport/明示的マーシャリングを使用する。BOOL/BOOLEAN/USHORT/ULONG/LONG/HANDLEのサイズ、union、構造体alignmentをWindows SDKに合わせる。プラットフォーム依存値をintに固定せず、不要なPack=1も指定しない。ネイティブ呼出しの例外をWndProc境界から外へ漏らさない。

試験は2層に分ける。Core側ではデコード済み値と期待値のfixtureを全OSで試験。HID parser/Preparsed Data/構造体レイアウト/実レポート再生はWindows専用試験にする。Preparsed Dataはopaqueであり、異なるOS/ドライバーでも移植可能なHID descriptorバイナリと同一視しない。実機fixtureは機種、VID/PID、接続方式、OS、ドライバー、取得日、軸マッピングを記録する。

M1で少なくとも1台のHID公開機器により取得可否を判定する。M3/M6で異なる軸配置、signed/unsigned、複数Report ID、USB/Bluetooth、抜き差し、複数機器、バックグラウンド、複数レポート/WM_INPUTを検証する。手元にない機器や形式は合成/モックと明記し、実機対応済みに数えない。

## 公式資料

2026-10-28確認。以下を根拠にGPSS固有の選択・上限・スレッド設計を定めた。

- [Raw Input](https://learn.microsoft.com/en-us/windows/win32/inputdev/raw-input)
- [RegisterRawInputDevices](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerrawinputdevices)
- [RAWHID](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-rawhid)
- [WM_INPUT](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-input)
- [HidP_GetUsageValue](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidpi/nf-hidpi-hidp_getusagevalue)
- [HIDP_VALUE_CAPS](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidpi/ns-hidpi-_hidp_value_caps)
- [DirectInput and XUSB Devices](https://learn.microsoft.com/en-us/windows/win32/xinput/directinput-and-xusb-devices)
