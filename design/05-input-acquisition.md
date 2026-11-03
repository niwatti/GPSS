# HID直接読み取り / XInput取得詳細 v3

更新日: 2026-10-28。C#移行M1/M3/M6の正本。入力取得は**HID直接読み取りを主方式、XInputを互換用**とする。Windows Raw Input API（WM_INPUT/GetRawInputData）は現計画では実装しない。M4までのコードを実装済み。Windows実機の確認は未実施。

## InputLagScopeから採用する方針

参照実装はHIDをCreateFileW + ReadFileのOverlapped I/Oで読み、XInputはXInputGetStateの状態パケット更新を取得する。専用取得処理・取得直後の単調時刻・上限付きキューという構造を採用する。Rust/Tauriを導入する意味ではなく、C# / .NET 10 / WPFからWindows APIを呼ぶ。

参照実装の約2msビジーウェイトや高頻度XInputループを常時動作の既定値にはしない。GPSSでは通常待機を標準とし、低遅延モードを明示選択できる設計にする。汎用ペイロードをバイトごとに8bit軸とみなす推測、符号の正負で異なる正規化、入力遅延測定用のPico/シリアル/GPIOは採用しない。

## 列挙・入力源の選択

- HID: HidD_GetHidGuid→SetupDiGetClassDevs/SetupDiEnumDeviceInterfaces/SetupDiGetDeviceInterfaceDetailでdevice pathを列挙。メタデータ用ハンドルと読み取り用ハンドルを区別し、VID/PID/製品名/TLC/Capabilitiesを取得する。HidD_GetPreparsedData、HidP_GetCaps、HidP_GetValueCapsを使用する。
- XInput: XInputGetStateでslot 0〜3を確認する。slotは永続個体IDではない。XInput 1.4を基本とし、DLL不在/API失敗は明示する。XInput 1.3や9.1.0への追加対応は必須にしない。
- HIDとXInputの両方に同じ機器が見えることがある。確実な対応情報がない限り自動で同一個体と断定せず、「HID:製品名/path」「XInput:slot」を別候補として表示する。同時に両方の値を混ぜない。
- 初回選択時は、読み取りが成功し4軸を正しく解釈できるHIDを推奨する。HID候補がない/権限不足/形式未対応ならXInput候補と理由を提示し、ユーザーが選択する。計測中の自動API切替は行わない。
- 切断・再接続・方式変更・slot/機器選択・軸割当・取得モード変更・リセットは新epochとし、旧履歴を破棄する。再接続時はHIDのdescriptorを取り直す。
- HIDの到着/除去は再検索・選択中の約1秒ごとの再列挙と読み取りエラーで照合する。M4実装ではCM_Register_Notificationは使用しない。XInputはユーザーの再検索でslot一覧を取得し、選択slotのERROR_DEVICE_NOT_CONNECTEDで切断表示。ReadFile未完了やXInputの状態不変だけでは切断としない。

## HID: Overlapped I/Oのライフサイクル

1. 選択pathをCreateFileWでGENERIC_READ、FILE_SHARE_READ | FILE_SHARE_WRITE、OPEN_EXISTING、FILE_FLAG_OVERLAPPEDで開く。失敗時はWin32 error codeと理由を残す。
2. Caps.InputReportByteLengthに従いバッファを確保する。上限1MiBを検証し、不正/巨大な長さを切り詰めて処理しない。初期実装は1本の未完了ReadFileを維持し、完了後すぐ次のreadを発行する。
3. 同期完了、ERROR_IO_PENDING、その他のエラーを分ける。PendingはOVERLAPPEDの専用イベントを待ち、GetOverlappedResultで完了と実バイト数を確認する。HidD_GetInputReportを繰り返すポーリングで代用しない。
4. 完了を確認した直後にStopwatch tickを記録し、バッファをコピー/所有権移転して上限付きキューへ送る。受信完了を観測した時刻であり、USB転送時刻やハードウェア生成時刻ではない。
5. 停止/選択変更/タイムアウト処理ではCancelIoExを要求した後、成功完了・ERROR_OPERATION_ABORTED・他の終了結果を回収する。CancelIoExが戻っただけでは完了していない。バッファ/OVERLAPPEDを完了前に解放・再利用しない。
6. 未完了I/Oがなくなってからイベント、Preparsed Data、device handleを解放する。SafeHandle等で二重解放を避ける。無応答ドライバーの終了待機中もUIを固めず、未完了リソースは完了まで所有したまま診断する。Thread.Abortで回避しない。

バッファやOVERLAPPEDには適切なpin/非管理メモリの寿命を与え、ReadFile呼出し中だけのpinで非同期期間をカバーしたとみなさない。I/Oのキャンセル競合、切断、完了直前停止をM3で試験する。HIDの入力レポートは長さ/Report IDごとに検証し、Raw InputのRAWHID/dwCount構造として解析しない。

## HIDパース・軸値

軸キーはUsagePage/Usage/LinkCollection/ReportId。Range/NotRange、LogicalMin/Max、BitSize、IsAbsolute、HasNullを検証する。初期版は1〜32bitのスカラー絶対値軸を汎用対応とする。HidP_GetUsageValueが返す値は必要に応じて符号拡張し、unsignedの論理最大値や32bit境界もlong/ulongで扱う。HidD_GetPreparsedDataで得たメモリはHidD_FreePreparsedDataで解放する。

既定候補は左X/Y、右Rx/Ry。Z/Rzや別LinkCollectionを使う機器はM3の診断・割当UIで確認する。別Report IDの軸は欠測にせず、当該レポートが含む軸だけをサンプル化する。

DS4/DualSense/Switch等の汎用HID情報だけでは読めない形式は、確認済みの機種別デコーダーを追加できるregistryを設ける。Report ID、長さ、VID/PID、接続方式、対応版をキーにし、全4軸の整数値/範囲をfixtureで検証してから登録する。汎用パーサーが失敗した際に任意の各バイトを軸値とみなさない。初期対応機種はM1実機調査で記録し、全機種対応を前提にしない。

機種固有の初期化が出力レポートを必要とする場合は、確認済みprofileだけが必要な内容を送る。バイブレーション等の不要な出力や汎用的な書き込みは行わない。未確認の初期化は未対応理由として示す。

## XInput: 状態更新の取得

- 選択slotのXInputGetStateを専用取得処理で呼ぶ。最初の成功状態は基準サンプルとして1回記録し、その後はdwPacketNumberが変わった場合だけ新しい状態サンプルを作る。ポーリング回数だけ同じ状態を複製しない。
- XINPUT_GAMEPADのLX/LY/RX/RYはsigned 16bit値を保持し、固定の論理範囲[-32768,32767]から線形変換する。トリガーは必要なら別軸として[0,255]を保持する。APIの返す値がハードウェアADCの精度であるとは断定しない。
- packet numberは状態更新識別子であり、USBレポート連番ではない。ボタン/トリガーだけの更新でもスティック値を含む状態を記録するため、スティックのゼロ差分が生じ得る。番号が同じなら記録しない。
- 最新状態APIなのでポーリング間に変わった中間値は回収できない。番号の飛び/巻き戻り等は参考診断として数えるが、それだけでは差分の連続性を切らない。同一epochで実際に取得できた隣接する有効値から差分を作る。番号差からUSB欠落数や既知のキュー破棄数を算出せず、未観測値の補間や番号差によるステップ差の除算も行わない。uint32の周回を考慮する。実際のキュー破棄・無効値・パース失敗・セッション変更では引き続き差分を切る。[8kHz通常モード修正](12-xinput-observed-step-fix.md)参照。
- 状態が変わらなくてもAPI成功は接続/取得処理の正常確認として別に記録する。最終API成功年齢と最終状態更新年齢を分ける。slot再利用の個体識別は保証せず、再接続でepochを更新する。

## 取得モードとCPU

取得は**低遅延のみ**とし、GUIのモード選択を廃止する。HIDは最大約2msの完了ポーリング後にイベント待機、XInputは上限付き高速ポーリングと休止／譲歩を用いる。上限／休止の設定は残す。通常モード用の目標間隔は旧設定の互換フィールドとして読み込むだけで、新しい取得には使わない。

ControllerとInputSourceBaseの両方で開始設定をLowLatencyへ正規化し、以前保存されたNormal設定や旧呼出しから通常経路へ戻らないようにする。CLI・デモも新規フレームのmodeはLowLatency。enumのNormalは過去CSV・設定の読込み互換のため保持する。OSの優先度や電源設定を変更せず、停止・選択命令とキャンセルを継続して処理する。[変更と検証](16-mean-precision-low-latency.md)参照。

## 時刻・統計・レートの意味

各入力にBackendKind（HidDirect / XInput / Simulation）、取得モード、DecoderId、QpcTicks、受信/更新連番、ContinuityIdを付ける。元整数を保存し、`2.0 * (value-min)/(max-min) - 1.0`で一度だけdoubleへ変換。中心の正負別スケール、追加デッドゾーン、平滑化、格納幅への精度補正はしない。例えば0..255ならstep=2/255、段階数255、表示約8.0bitとなる。

| 入力源 | 記録するサンプル | 表示する頻度 |
| --- | --- | --- |
| HID直接 | 読み取ったレポート内の有効軸。同値の再受信も保持 | HIDレポート受信/秒、軸別サンプル/秒 |
| XInput | 初回状態とpacket番号が変化した状態内の各軸 | 状態更新検出/秒、API呼出し/秒、軸別サンプル/秒 |

XInputにHIDレポート受信Hz/USBポーリングHzを表示しない。APIを選び直すとサンプル分布が変わるため、履歴と統計を混合せずリセットする。震えは観測サンプル等重みで計算し、固定周期/時間重みの値と同じとみなさない。XInputで30秒状態更新がなければ統計は空、接続状態は別に維持し、位置は「最終状態」と年齢を表示する。

アプリの上限付きキューでの破棄は既知欠落として計上する。HIDドライバー内や機器側の全欠落、XInputの全中間状態の回収は保証しない。各差分のbit数の平均は取得API経由の推定値であり、ハードウェアの公称bit数ではない。

## 検証と参照

M1でHIDのopen/read/CapabilitiesとXInputのslot/get_stateのPoCを別々に行う。M3でI/O即時完了/pending/キャンセル競合/切断、誤ったレポート、複数Report ID、signed/unsigned、API失敗、同一packet、packet周回/飛び、キュー飽和を試験。M6で実機/接続方式/CPU負荷/終了/同型機を確認する。合成/モックと実機合格を分ける。

2026-10-28に確認した設計根拠:

- [InputLagScope HID実装](https://github.com/608/InputLagScope/blob/main/src-tauri/src/windows_hid.rs)、[入力経路](https://github.com/608/InputLagScope/blob/main/src-tauri/src/capture.rs)、[解析](https://github.com/608/InputLagScope/blob/main/src-tauri/src/protocol.rs)。取得方式を参照し、コードをそのまま転用する計画ではない。転用時は当該版のライセンス表示条件を確認する。
- [Microsoft: Obtaining HID Reports](https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/obtaining-hid-reports)
- [Microsoft: CancelIoEx](https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-cancelioex)
- [Microsoft: XINPUT_STATE](https://learn.microsoft.com/en-us/windows/win32/api/xinput/ns-xinput-xinput_state)

API詳細・SDK/依存の対応版は実装開始時にも公式資料で確認する。Picoを使用する入力遅延測定は現在のGPSS要件に追加しない。

## M4実装で確認した差異

正のTask.Delayが1ms未満で同期完了するのを避け、通常XInput待機と低遅延XInputの休止は最低1ms単位に切り上げる。設定のµs値どおりの厳密な待機は保証しない。明示的な休止0はTask.Yieldで制御を譲る。M4では起動中の設定だけを扱い、永続保存はM6。

HIDのパース失敗で対象軸が特定できる場合は当該軸だけContinuityIdを進める。対象が不明なレポート破損やキュー破棄は保守的に全軸の連続性を切る。無効/部分軸のレポートもレポート件数として記録し、軸のないフレームをゼロ値で埋めない。

右スティック候補がRx/RyとZ/Rzの両方あるHIDでは、自動割当を確定しない。トリガーを右スティックと取り違えないよう、診断画面で生値を確認して割り当てる。
