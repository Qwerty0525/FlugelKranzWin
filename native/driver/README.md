# FlugelKranzWin SteamVR driver

Windows x64 用の server driver です。[導入・設定](../../WINDOWS.md) と [操作仕様](../../README.md) はそれぞれの文書を参照してください。この派生リポジトリは Windows / SteamVR を対象とし、Linux 版は [親リポジトリ](https://github.com/ReinaS-64892/FlugelKranz) が扱います。

## 姿勢の流れ

```text
HMD / controller / tracker の DriverPose_t
  ├─ 変換前の物理姿勢 → 共有メモリー → C# Core の操作計算
  └─ 飛行変換を適用 → SteamVR → ゲームの仮想姿勢

DirectMode_009 SubmitLayer
  └─ フレーム時刻に対応する物理 HMD 姿勢を mHmdPose へ設定
       → 画像・投影・予測時間を保持して転送
```

[Kawaii Move Assist](https://github.com/ReinaS-64892/reina_s_kawaii_move_assist) の姿勢更新フック方式を参考に、MinHook で `IVRServerDriverHost_005 / _006::TrackedDevicePoseUpdated` をフックします。元の world-from-driver と飛行変換を `qRotation` / `vecPosition` へ合成し、world-from-driver を恒等変換にします。線速度・角速度なども同じ座標系へ回転し、body-local の頭部校正と姿勢時刻は保持します。

恒等変換の指令では元の `DriverPose_t` をそのまま転送します。Chaperone setter は呼びません。フックを通る HMD、コントローラー、トラッカーへ変換が作用しますが、他ドライバーとの共存や全機種への対応を意味するものではありません。

## 座標系

右手系、メートル、+Y 上、-Z 前方です。積は右側を先に適用します。

接続時の Standing → Raw を `S`、Raw 空間の物理姿勢を `R`、Core の飛行変換を `D` とすると、次の関係になります。

```text
Core が読む物理姿勢： P = S⁻¹ R
ドライバーへの指令： T = S D S⁻¹
ゲーム側の姿勢：     S⁻¹ T R = D P
```

物理姿勢キャッシュは `world-from-driver * body * driver-from-head` で構成します。Core は変換前のキャッシュを読み、飛行結果を入力へ再適用する循環を防ぎます。各デバイスの通知時刻は独立しており、HMD と両手が完全に同時刻のサンプルとは限りません。

## IPC と接続状態

セッションローカルの共有メモリー `Local\FlugelKranz.Driver.State.v1`（4704 bytes、64 device slots）を `Local\FlugelKranz.Driver.Mutex.v1` で保護します。対応するレイアウトは `Transform.h` と `src/FlugelKranz.OpenVR/DriverConnection.cs` にあります。

変換を所有できるクライアントは 1 つです。heartbeat が 500 ms 途絶えると所有権を解除し、恒等変換へ戻します。復帰には再接続が必要です。C# 側では 100 ms より古い物理姿勢を追跡中と扱いません。

OFF 中も heartbeat と変換を保持します。通常終了時は変換を解除します。SteamVR 自体の停止・異常終了時の復元を保証する仕組みではありません。

## 表示補正

ゲームへ渡す飛行後の HMD 姿勢を、そのまま配信・再投影用の物理姿勢として扱うと、HMD 内に黒い縁や暗転が生じることがありました。MirrorView が正常でも起きたため、姿勢更新とフレームの表示補正を別々に扱います。

HMD の登録と `GetComponent` を観測し、`IVRDriverDirectModeComponent_009::SubmitLayer` をフックします。補正時はレイヤーをコピーし、両眼の `mHmdPose` を変更します。テクスチャ、depth、projection、bounds、prediction interval は保持します。

設定は SteamVR 起動時に読みます。`driver_flugelkranz` のソース既定値は `loadPriority=100`、`observeFrames=true`、`correctFramePose=true`、`timeBasedFramePose=true` です。実機で改善を確認した設定例は [導入ガイド](../../WINDOWS.md#フレーム姿勢補正の設定) を参照してください。

- `correctFramePose=true`：補正フックを有効にします。
- `timeBasedFramePose=true`：下記の時刻ベース方式を使用します。`correctFramePose=true` が必要です。
- `observeFrames=true`：補正を無効にした状態でも観測用フックを有効にします。補正無効時は元のレイヤーをそのまま転送します。

- `fallbackFramePose=false`（既定）：`true` にすると、どの方式でも補正できなかったレイヤーを飛行後の姿勢のまま転送せず、下記の代替補正をかけます。`correctFramePose=true` が必要です。

`observeFrames` と `correctFramePose` の両方を false にして再起動すると、これらのフレームフックを追加しません。`observeFrames=false` だけでは、補正中のログが無効になるわけではありません。SteamVR の同名ユーザー設定は配布ファイルの既定値より優先されます。

### 時刻ベース方式

128 件の HMD 履歴に、変換後の姿勢と元の `DriverPose_t` を保存します。

```text
物理サンプルの時刻 = pose-update 受信時刻 + poseTimeOffset
求めるフレーム時刻 = SubmitLayer 受信時刻
                     + flHmdPosePredictionTimeInSecondsFromNow
```

対象時刻を挟む物理サンプルがあれば位置の線形補間と quaternion SLERP を使用します。そうでなければ線速度・角速度で最大 100 ms 前後まで予測します。履歴は受信後 250 ms 以内、補間区間は 100 ms 以内、最新 HMD サンプルは 50 ms 以内を必要とします。頭部 / IMU の校正を保持します。

求めた物理姿勢を各眼のフレーム姿勢へ設定します。飛行後の姿勢との類似度から飛行指令を選ぶ処理には依存しません。ただし、ローカル受信時刻と API の予測時間に基づく推定であり、SteamVR / VD 内部の予測を完全に再現するものではありません。条件を満たさず補正できない場合は元のレイヤーを転送します。

### 以前の履歴照合方式

`timeBasedFramePose=false` では、予測した変換後の HMD 履歴にフレーム姿勢を照合し、一致する飛行変換の逆変換を適用します。対応がない・曖昧な場合は元のレイヤーを転送します。厳密なフレーム ID による対応ではありません。

飛行変換が 250 ms 以上一定で、最新 HMD サンプルが 50 ms 以内なら、一定の逆変換を利用する保持中の経路もあります。変換変更・無効姿勢・100 ms 超のサンプル間隔で保持条件をやり直します。この方式では飛行回転と頭部運動の同時実行で補正が外れる報告があり、時刻ベース方式を追加しました。

### 補正できなかったレイヤーの代替補正（`fallbackFramePose`）

恒等変換のパススルー、保持中の経路、時刻ベース方式、履歴照合のいずれも使えないレイヤー（最新 HMD サンプルが 50 ms より古い、予測時間が範囲外など）は、通常は元のレイヤーをそのまま転送します。この場合、飛行後の姿勢が配信側へ渡ります。

`fallbackFramePose=true` では、恒等変換と HMD 履歴にある各飛行変換のうち、その逆変換をレイヤーにかけたときに最新 HMD 更新の物理頭部姿勢へ最も近くなるものを選び、両眼の `mHmdPose` から取り除きます。恒等変換が選ばれた場合は元のレイヤーをそのまま転送します。履歴が空の場合（HMD 姿勢が無効になった直後など）は、HMD に最後に確定した飛行変換を使います。

- よくある失敗（最新サンプルが古い）では、最新の変換が vrserver の持つ変換と同じなので、ほぼ正確に戻ります。
- 飛行開始直後に恒等変換で描画されたレイヤーなど、古い変換で描画されたレイヤーには最新の変換をかけません。
- 低フレームレートや配信遅延で描画姿勢と最新の HMD 更新のあいだに複数の指令が入る場合は、頭部の動きが飛行の 1 ステップより大きいと選択を誤り、数ステップ分ずれることがあります。

この経路のレイヤーは `FrameAudit` の `missed` に含まれ、`DriverAudit` では `fallback` に数えます。実機では未検証です。

## ログと実機確認

起動時の `FlugelKranz flags:` 行に、読み込んだ動作フラグの値をすべて記録します。設定ファイルにないフラグは、そのフラグ追加前の動作に当たるコンパイル時の値を使います。

`vrserver.txt` の `FrameAudit` は最大毎秒 1 レイヤーを記録します。

| 項目 | 意味 |
| --- | --- |
| `corrected=1` | そのレイヤーの姿勢メタデータを補正した。HMD の表示品質の保証ではない |
| `timed=1` | 時刻ベースの物理姿勢を使用 |
| `physicalDt` | 物理姿勢の時刻差に関する診断値 |
| `matchError=-1` | 時刻ベース方式では姿勢照合誤差を計算しない |
| `layers` / `missed` / `held` | 前の記録以降のレイヤー数 / 未補正数 / 保持中の経路の使用数 |

`driverAudit=true`（同梱の `default.vrsettings` とソース既定値はともに `false`。アプリの「詳細診断」で切り替え）では、`RunFrame` が `gate` を解放した後に毎秒 1 行の `DriverAudit` を記録します。FrameAudit にない値だけを持ちます。値は前の行以降の集計です。

| 項目 | 意味 |
| --- | --- |
| `refreshMiss` / `sampleMiss` | IPC ミューテックスを取れず、前の指令を維持した回数 / 物理サンプルを書かなかった回数 |
| `ownerRevoked` / `commandInvalid` | ハートビート切れで所有権を外した回数 / 生きたクライアントの指令が `valid()` を満たさなかった回数 |
| `command: changes` / `intervalMs[...]` | 採用した指令が変わった回数と、その間隔（steady clock）のヒストグラム |
| `virtualStepM` | HMD 更新ごとの、指令の変化による仮想頭部位置の移動量 |
| `readback: calls/window/pending/previous/ambiguous/residualMaxM` | 物理姿勢の読み戻し回数、転送中の機器に当たった回数、選んだ候補、2 候補の距離比が 2 未満だった回数、選んだ候補の残差の最大値 |
| `submit[forwarded,bypass,held,timed,matched,missed]` | レイヤーごとの経路の回数（`forwarded` は補正無効） |
| `pathChanges` / `jumpMaxM` / `jumpMaxDeg` | 経路が変わった回数と、そのとき前のレイヤーから渡した HMD 姿勢が動いた量の最大値（1 フレーム分の頭部運動を含む） |
| `headAgeMs` | SubmitLayer 時点の最新 HMD 更新の経過時間（steady clock） |
| `skew: n/skewed/maxM/meanM` | `RunFrame` ごとに、50 ms 以内に更新された各機器について、HMD に確定した変換と機器自身の変換がその機器の位置をどれだけ離して置くか。機器は自分の更新時点の指令を受けるため、指令が変わってから機器が次に更新されるまで、頭部に対する体の位置が 1 ステップずれる |
| `commandStepM` | 採用した指令が変わるたびの、物理頭部位置での移動量 |
| `updates: hmd/other` | HMD とそれ以外の機器の姿勢更新数 |

起動 10 秒後に SubmitLayer フックがなければ、`DriverAudit: ... render correction OFF` を一度記録します。このとき全レイヤーは飛行後の姿勢のまま転送されます。

複数レイヤーが 1 フレームに属する場合があるため、これらを HMD のフレーム数として解釈しないでください。未対応の Direct Mode バージョン、またはフック導入前に登録された HMD では記録されない場合があります。`loadPriority=100` は HMD 登録を早期に観測するための設定です。

2026-09-13、Quest 2 + Touch / VD で時刻ベース方式（`5b41cab`）による黒い縁・暗転・同時回転時のジッター改善の報告を得ました。その後、視界のふらつきと酔いやすさの追加報告があり、原因は未確定です。表示品質の問題は調査継続中です。他の HMD、ゲーム、FBT 構成の系統的な検証は未実施です。

## ビルド・テスト・依存コード

リポジトリのルートから実行します。

```powershell
./build-windows.ps1
ctest --test-dir artifacts/driver-build -C Release --output-on-failure
```

MSVC x64、Windows SDK、CMake、.NET 10 が必要です。`transform_tests`、`observer_tests`、`frame_tests` は座標合成・恒等変換・フレーム情報保持・物理姿勢の補間と予測を検証します。`driver_path_tests` は姿勢更新フックを偽の元関数で駆動し、恒等変換のパススルー、飛行変換の適用、読み戻し用の保留・確定の順序、古い指令の解除、未知の ABI サイズの素通しを検証します。`DriverHarness.h` は vrserver の代わりにドライバーログ・設定・デバイスプロパティ・アプリ側の共有メモリーと IPC ミューテックスを用意し、設定読み込みとフラグ行、`classify()`、`refresh()` の取得・取得失敗・無効指令・所有権解除、読み戻しの呼び出し元判定を実際のコードで検証します。同じ実行ファイルで `DriverAudit` の各カウンター、`readbackGenerations` の対応付け（転送をまたぐ読み戻し、向きだけで区別できる回転、確定順序の逆転）と 2 スレッドの負荷試験も行います。テストだけでは実機のフック互換性や表示品質を確認できません。

同梱コードの出典とライセンスを保持してください。

- OpenVR header / license：ValveSoftware/openvr `0924064316de3effbcd1acf1e309182a2deb1c05`。
- MinHook source / build files：TsudaKageyu/minhook `8af6b4acae5a9388fd742b56fa79ece89d96f823`。

## 恒等変換時のパススルー

飛行変換が連続 250 ms 以上ゼロで、最新 HMD サンプルが 50 ms 以内なら、フレーム姿勢を独自予測で置き換えず元のレイヤーをそのまま転送します。リセット直後は遅れた飛行フレームに対応するため補正を継続します。ログの `identityBypass=1` はこのパススルーを示し、未補正エラー数には含めません。OFF は変換を保持するため、OFF だけでこの条件になるとは限りません。2026-09-14、Quest 2 + Touch / VD でふらつき解消の実機報告を得ています。

## 保持中の予測を保持する補正

`timeBasedFramePose=true` でも、飛行変換が連続 250 ms 以上一定で最新 HMD サンプルが 50 ms 以内なら、両眼のフレーム姿勢へ一定の飛行変換の逆変換だけを適用します。元の SteamVR / HMD ドライバーの予測姿勢を保持し、独自の物理姿勢予測で置き換えません。変換の変更・追跡中断後は条件が揃うまで時刻ベース方式を使用します。恒等変換時の完全なパススルーは引き続き優先します。

`FrameAudit` の `timed` は設定値ではなく、そのレイヤーで実際に時刻ベース方式を試みたかを示します。保持中の逆変換では `timed=0`、`corrected=1` となり `held` の件数が増えます。更新中のフレームと飛行指令を厳密に対応付ける方式は未実装です。保持中の改善効果も同構成で実機確認済みです。

## HMD 姿勢を読み戻すソルバー（SlimeVR、rebocap）

SlimeVR のドライバーは `IVRServerDriverHost::GetRawTrackedDevicePoses` で HMD やコントローラーの姿勢を読み戻し、それを基準に骨格を組みます。飛行後の姿勢をそのまま渡すと、骨格が飛行後の頭部に合わせて作られたうえで、SlimeVR のトラッカーに飛行変換がもう一度適用されます。2026-09-14、Quest 2 + Touch / VD + SlimeVR で飛行を 33.6 m 移動させた状態を記録し、SlimeVR のトラッカーが飛行後の頭部から 0.4〜1.0 m、物理的な頭部から約 33 m の位置で送られていることを確認しました。VD の機器とコントローラーは逆の関係でした。

このドライバーは `GetRawTrackedDevicePoses` をフックし、呼び出し元のモジュールのファイル名が `physicalPoseDrivers`（カンマ区切り、大文字小文字を区別しない完全一致、既定値 `driver_slimevr.dll,driver_rebocap.dll`）に含まれる場合だけ、各機器へ最後に適用した飛行変換を取り除いた物理姿勢を返します。これにより SlimeVR の骨格は物理空間で一貫し、そのトラッカーにはほかの機器と同じ飛行変換が一度だけ適用されます。移動と回転のどちらでも同じです。呼び出し元ごとの判定は `FlugelKranz: raw poses for ...` として `vrserver.txt` に記録されます。空文字列にするとフックを追加しません。

### 読み戻しと転送中の姿勢の対応付け

ソルバーが読み戻す瞬間、その機器の姿勢更新が vrserver へ転送中のことがあります。vrserver が持つのは前の飛行変換の姿勢か新しい変換の姿勢か分からないため、間違った変換を取り除くと 1 回の読み戻しだけ飛行 1 ステップ分ずれます。

- `readbackGenerations=false`（既定、`45fdf83` の方式）：転送中の機器だけ、確定済みの変換と転送中の変換のうち、取り除いた位置がその更新の物理位置に近い方を使います。読み戻しが vrserver を呼んでいるあいだに転送が始まって終わると、転送中と見なされず新しい変換を古い姿勢から取り除きます。位置だけで比べるため、HMD を支点とする回転（ヘッドパイロット回転）では選択が実質ランダムです。同じ機器の転送が重なり確定の順序が入れ替わると、確定済みの変換が古い値に戻ります。
- `readbackGenerations=true`：機器ごとの転送に世代番号を付け、直近 8 世代の変換と物理姿勢（位置と向き）を保存します。読み戻しは vrserver を呼ぶ前と後で確定済みの世代を比べ、変化がなく転送中のものもなければ確定済みの変換を使います。そうでなければ、呼ぶ前に確定していた世代から最新の世代までの候補のうち、取り除いた姿勢が保存した物理姿勢に位置と向きの両方で最も近いものを使います。確定は新しい世代へだけ進み、古い転送が後から終わった場合は確定済みの変換を変えず、次の順序どおりの確定まで直近の候補すべてから選びます。

`readbackGenerations=true` でも、読み戻し 1 回のあいだに同じ機器の転送が 8 回を超えると候補が足りなくなり、`DriverAudit` の `overflow` に数えます。vrserver が転送を非同期に反映する場合、フック境界での世代の比較だけでは不十分ですが、この前提は未確認です。SlimeVR と rebocap の両方の実機で確認するまで既定値は false です。

rebocap も専用ドライバー `driver_rebocap.dll` が vrserver 内で HMD 姿勢を読み、デスクトップアプリがその頭部位置を基準に体を組むため、対象外では同じ二重適用が起きます。2026-10-27、Quest 3S + rebocap の利用者から、`physicalPoseDrivers` に `driver_rebocap.dll` を追加する前はトラッカーが HMD の約 2 倍移動し、回転では胸と腕がずれ、追加後は移動・回転とも正常になったと報告を得ました。その後の飛行時の `vrserver.txt`（HMD は SteamVR の `oculus` ドライバー経由）に `raw poses for driver_rebocap.dll: physical (flight removed)` が記録されており、読み戻しは `driver_rebocap.dll` 自身から行われ、物理姿勢が返されていることを確認しました。

`hmdAnchoredTrackingSystems` は、このフックが届かない経路（外部アプリの OpenVR クライアント API など）で HMD を読むソルバー向けの代替策です。一致した機器には、飛行後の HMD 位置を支点とする回転だけを適用します。移動の二重適用は防げますが、頭部と体幹で回転の基準が混ざるため、回転を含む飛行では姿勢がずれることを実機で確認しています。既定値は空です。`physicalPoseDrivers` の対象と同じソルバーには併用しないでください。
