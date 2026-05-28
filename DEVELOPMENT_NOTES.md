# KK_AibuVR 開発ノート

最終更新: 2026-05-24

---

## 1. プロジェクト概要

`KoikatuVR.exe` 用 BepInEx プラグイン。

| 機能 | 状態 |
|---|---|
| ハンド愛撫アイテム切り替え | ✅ 動作確認済み |
| 乳首突出管理（VR カレス中の沈み込み対策） | ✅ 動作確認済み |
| 舌カレス（HMD 近接 → 舌プロップ表示 + 愛撫効果） | ⏸ 実装済みだが無効化中（設計見直し中） |

---

## 2. ファイル構成

```
KK_AibuVR/
├── Plugin.cs               # BepInPlugin エントリポイント、設定値
├── Patches.cs              # Harmony パッチ群（アイテム切り替え）
├── TongueCaress.cs         # TongueCaressController (MonoBehaviour) + 各 ChaControl パッチ
├── AibuItemTracker.cs      # 手ごとのアイテムインデックス管理
└── DEVELOPMENT_NOTES.md    # このファイル
```

---

## 3. 実装済み機能

### 3.1 アイテム切り替え

**データフロー:**

```
Tab / VRボタン押下
  → VRHScene_Update_Patch.Postfix
      → GetCycleHands() で左右どちらの手を切り替えるか判定
      → AibuItemTracker.CycleNext(hand)
      → IsIdValidForZone で無効アイテムをスキップ（次の有効アイテムへ前進検索）
      → TryImmediateSwap → MI_SetItem.Invoke()
            → SetItem_Pre: LayerInfo.idObject を tracker.CurrentId に書き換え
            → SetItem（ゲーム本体）: 書き換えられた idObject でアイテムを選択
```

**IsIdValidForZone ルール:**

| id | muneL/R (kindInt≤3) | kokan/anal | siriL/R (kindInt=6/7) | 除外理由 |
|---|---|---|---|---|
| 0 (モミ) | ✔ | ✔ | ✔ | — |
| 1 | ✔ | ✔ | ✖ | siri でめり込む |
| 2 (電マ) | ✔ | ✔ | ✔ | — |
| 3 (バイブ) | ✔ | ✖ | ✖ | Animator Layer 6 が非胸ゾーンに存在しない → 不可視 |

**無効アイテムのスキップ（前進検索）:**

単純に index=0 へ折り返すと id=1 がスキップ対象のとき id=2（電マ）に到達できない。
そのため CycleNext 後に現在 id が無効なら、現在 index から前進して次の有効 id を探す。

```csharp
int searchFrom = state.CurrentIndex;
for (int i = 1; i < state.ItemIds.Count; i++)
{
    int tryIdx = (searchFrom + i) % state.ItemIds.Count;
    if (IsIdValidForZone(state.ItemIds[tryIdx], zoneKind))
    { state.CurrentIndex = tryIdx; break; }
}
```

**左右独立コントローラー割り当て:**

`IsPressDownSelectHand(btn, handNum, mode)` で左右別々に検出（handNum: 0=左, 1=右）。
ValueTuple が使えない環境（.NET Framework + 0Harmony.dll）のため `out` 引数で返す。

```csharp
private static void GetCycleHands(VRHandCtrl[] vrHands, out bool left, out bool right)
```

- キーボード → 両手同時
- `CycleButtonVR_L` → 左手のみ
- `CycleButtonVR_R` → 右手のみ

**即時切り替え（TryImmediateSwap）:**

`VRHandCtrl.SetItem` は旧アイテムを非表示にしないため `currentItem.obj.SetActive(false)` を先に呼ぶ。
SetItem 前に `NotifyItemSwitch()` で NipState をリセットして DisableShapeNip ブロックが邪魔しないようにする。

### 3.2 乳首突出管理

VR の DragAction は `cf_d_bnip01`（親ボーン）を DynamicBone force で毎フレーム内側に押し込む。
`ChangeNipRate` / `DisableShapeNip` 等の API はブレンドシェイプマスクを操作するだけでボーン位置を変えない。
`cf_j_bnip02.localPosition` を直接上書きすることで DragAction に毎フレーム対抗する。

**3層の保護:**

| 層 | 対象 | 手法 |
|---|---|---|
| 乳首ボーン位置 | `cf_j_bnip02_R/L` | LateUpdate(30000) で `localPosition = (0,0,protrusion)` を毎フレーム上書き |
| 乳輪形状マスク | DisableShapeNip (masks 6/7) | `DisableShapeNip_Patch` で disable=true をブロック + LateUpdate で `MI_DisableShapeNip(lr, false)` |
| 乳首スタンドマスク | `disableBustShapeMask[lr, 8]` | `UpdateShapeBody_Patch.Prefix` で毎回クリア |

**UpdateShapeBody タイミング問題（重要）:**

初回タッチフレームでは NipStateActive が false なので `DisableShapeBodyID_Patch` がブロックしない。
その結果 `disableBustShapeMask[lr, 8] = true` → UpdateShapeBody が 0.5f（flat）を書き込む。
これを `UpdateShapeBody_Patch.Prefix` で直接クリアすることで回避（reSetupDynamicBoneBust 等の副作用なし）。

**両手同時カレス:**

`_caressedSide`（文字列）だと1手しか追跡できない。`_caressedL`/`_caressedR`（bool）で独立管理。
`DoUpdateHandMode` のループに `break` を置かないことで両手を全件チェック。

**NipState のオン/オフ条件:**

- ON: `nowAnimStateName == "M_Touch" || "M_Idle"` かつ `useItem.obj.name` に "finger" を含む
- OFF: アニメーションが M_Touch/M_Idle から出た瞬間（デバウンスなし即時）
- グリッチ保護: M_Touch/M_Idle 中に finger アイテムを瞬間的に失った場合のみデバウンス 0.4s

**アイテム切替保護:**

SetItem は内部で `DisableShapeNip` を呼ぶ。NipStateActive=true のままだとブロックされて切り替え失敗。
`NotifyItemSwitch()` で NipState を先にリセットしてから `MI_SetItem.Invoke`。

### 3.3 舌カレス（無効化中 ⏸）

`TongueCaressEnabled = false` で完全無効化。再有効化は `TongueCaress.cs` でこの値を `true` に変更してビルド。
設定項目（Tongue Caress セクション）は Plugin.cs から削除済み → ConfigManager に表示されない。

**保存されている実装:**

| 機能 | 実装方法 |
|---|---|
| HMD 近接判定 | `cf_j_bust01_L/R`, `cf_hit_kokan`, `cf_hit_siri_L/R` ボーンとの距離 |
| ゾーン安定化 | 離脱デバウンス 0.80s / 再進入ブロック 1.50s / ゾーン変更デバウンス 0.50s |
| 舌プロップ表示 | `Renderer.enabled` トグル（SetActive は影バグあり） |
| Sita_ アニメーション | SetLayerWeight + CrossFade（Layer 31/32/37/46/47） |
| 快感ゲージ | `HFlag.FemaleGaugeUp(rate * deltaTime, false, false)` |
| ボイス反応 | `HFlag.voice.playVoices[0]` 直接設定 |
| ポーズ変化 | A_Touch（kokan）/ S_Touch（siriL/R）CrossFadeInFixedTime |
| 絶頂保護 | `nowAnimStateName.Contains("Orgasm")` で ApplyTouchPose を完全スキップ |

**アイテムサイクル末尾の舌モード:**

以前は item0→1→2→3(胸のみ)→舌モード→item0 のサイクルだったが、コメントアウト済み。
現在は末尾に達すると index=0 に折り返す4ループ。

**舌カレスの未解決課題（設計見直し時に対応）:**
- p_tang2 bind pose がアナル形状のまま（ゾーン別形状の解決手段が不明）
- 胸ゾーンのポーズ変化（M_Touch）未実装
- 手/舌共存不可（A_Touch/S_Touch が AibuCollider ごと移動 → 初回タッチ検出失敗 → 排他設計が必要）

---

## 4. 設定項目（全デフォルト値）

| セクション | キー | デフォルト | 説明 |
|---|---|---|---|
| Hand Caress | Enabled | true | false でアイテムサイクル + 乳首管理を全無効 |
| Hand Caress | Nipple Protrusion | 3.0（1.0〜8.0） | cf_j_bnip02 の localPosition Z オフセット。3.0≈6mm、8.0≈20mm |
| Controls | Cycle Key (Keyboard) | Tab | 両手同時サイクル |
| Controls | Cycle Button - Left Controller | Grip | 左手のみサイクル |
| Controls | Cycle Button - Right Controller | Grip | 右手のみサイクル |

---

## 5. Harmony v1 パッチの制約

### 禁止事項

| 禁止パターン | 症状 | 理由 |
|---|---|---|
| Postfix/Prefix に named method param 追加 | パッチが黙って失敗 | Harmony v1 の制約。`__instance`/`__result`/`__state`/`__args` のみ安全 |
| `VRHandCtrl.DragAction` へのパッチ | `IndexOutOfRangeException` | DMD が IL を正しく再コンパイルできない |
| `VRHandCtrl.IsAction()` のオーバーライド | ポーズが戻らない・グリップ移動停止 | IsAction() は複数システムが使用 |
| `VRHandCtrl.action` フィールドへの書き込み | コントローラー操作不能 | VRHandCtrl 内部の入力 FSM に干渉 |

### 安全に Postfix 確認済みのメソッド

- `VRHandCtrl.LoadItemObject`
- `VRHandCtrl.SetItem`（named param なしに限る）
- `VRHandCtrl.IsItemTouch`
- `VRHScene.Update`
- `HAibu.LateProc`
- `VRHScene.MapSameObjectDisable`
- `ChaControl.UpdateShapeBody`
- `ChaControl.DisableShapeNip`
- `ChaControl.DisableShapeBodyID`

---

## 6. 重要な技術知見

### AibuColliderKind 値（実機確認済み）

```
none=0, mouth=1, muneL=2, muneR=3, kokan=4, anal=5, siriL=6, siriR=7
reac_head=8, reac_bodyup=9, reac_bodydown=10
```

`s_kindToArea = { 0, 0, 1, 2, 3, 3 }` … kindTouch-2 → SetItem の areaIndex

### HFlag 重要フィールド（実機確認済み）

| フィールド | 型 | 用途 |
|---|---|---|
| `nowAnimStateName` | string | DragAction が書き込む現在のボディアニメーション状態名 |
| `isAibuSelect` | bool | アイテム選択 UI 開放中フラグ（タッチ反応抑制） |
| `voice.playVoices[0]` | int | ボイス再生トリガー（-1=無音） |

### nowAnimStateName 状態名（実機確認済み）

| 状態名 | ゾーン | 備考 |
|---|---|---|
| `"Idle"` | 全体 | DragAction 中も Layer 0 hash は Idle のまま（hash: 2081823275） |
| `"M_Touch"` | muneL/R | DragAction 中は Layer 0 hash が Idle のまま |
| `"M_Idle"` | muneL/R | カレス終了直後の残余ポーズ |
| `"A_Touch"` | kokan | DragAction 中は Layer 0 hash が Idle のまま |
| `"S_Touch"` | siriL/R | HasState(0, "S_Touch") = True 確認済み |
| `"K_Touch"` / `"K_Loop"` | 不明 | p_fingerL 使用時に観測 |

### Sita_ レイヤー番号（実機確認済み）

| レイヤー | 状態名 |
|---|---|
| 31 | Sita_mune_sawari_L |
| 32 | Sita_mune_sawari_R |
| 37 | Sita_kokan_sawari_F |
| 46 | Sita_siri_sawari_L |
| 47 | Sita_siri_sawari_R |

### 乳首ボーン階層（実機確認済み）

```
cf_j_bust03_R/L
  └── cf_d_bnip01_R/L          ← DragAction が DynamicBone force で内側に押し込む
        └── cf_j_bnip02root_R/L
              └── cf_j_bnip02_R/L  ← ここに localPosition Z オフセットを毎フレーム適用
                    └── k_f_munenipR/L_00〜03
```

### ApplyTouchPose の CrossFade ガード

DragAction 中は base layer の shortNameHash が常に Idle のまま。
自分で CrossFadeInFixedTime を呼ぶと hash は正しく変わる。
→ `_appliedTouchState` フィールドでガードし、hash が既に正しければ CrossFade しない。

### ボイス反応テーブル

| ゾーン | playVoices[0] |
|---|---|
| muneL / muneR | 112 |
| kokan | 114 |
| siriL / siriR | 118 |

---

## 7. 失敗アプローチ一覧

| アプローチ | 結果 | 教訓 |
|---|---|---|
| Postfix に named param `button` 追加 | クラス内の全パッチが黙って無効化 | Harmony v1 では named method param は使わない |
| `action = 2` を毎フレームセット | コントローラー操作不能 | `action` は VRHandCtrl の内部 FSM を直接制御する |
| `IsAction()` Postfix で常に true 返却 | ポーズが戻らない・グリップ移動停止 | IsAction() は HAibu 以外からも呼ばれる |
| `VRHandCtrl.DragAction` Postfix パッチ | IndexOutOfRangeException | Harmony v1 DMD の限界 |
| `nowAnimStateName` に Sita_ 状態名をセット | 効果なし | Sita_ はベースレイヤーに存在しない |
| `isAibuSelect = true` | 愛撫反応が抑制される | アイテム選択 UI フラグとして使われている |
| `ApplyTouchPose` で hash ガードなしに毎フレーム CrossFade | ビクッ連続 | DragAction は base layer hash を変えない → ガード必須 |
| `VRHandCtrl.Reaction(reac_bodyup=9)` | グリップ移動に副作用 | Reaction() は VRHandCtrl 内部状態を変化させる |
| `useItem = dicItem[0]` | 手愛撫の再開がブロックされる | `useItem != null` = 「カレス継続中」フラグ |
| `selectKindTouch = 0` のまま `nowAnimStateName = "M_Touch"` | 拒否モーション発生 | HAibu がゾーン種別なしのタッチ状態を無効と判断 |
| `selectKindTouch` を手愛撫中に上書き | DragAction のコントローラー移動効果消失 | DragAction のゾーン計算が狂う |
| SUPPRESS タイマーで手/舌共存 | Front_Dislikes（拒否反応）頻発 | SUPPRESS が DragAction と競合してアニメーターを毎フレーム Idle に強制 |
| 手/舌共存（SUPPRESS + HAND_ACTIVE ガード） | 使用中にハンド愛撫が不能 | A_Touch/S_Touch で AibuCollider が移動 → 初回タッチ検出失敗 |
| `CrossFadeInFixedTime("K_Touch")` を毎フレーム | 乳首消滅・カレス再開不可・メニュー操作不能 | HAibu/VRHandCtrl の状態管理と競合 |
| `ChangeNipRate` / `ChangeSettingNip` / `DisableShapeNip` で乳首突出 | 変化なし | これらは乳首ボーンを駆動しない。VR の乳首変形はボーン直接操作が必要 |
| `_mnpaSmr.enabled` トグル | 元々 enabled=True のため無効 / false にすると胸メッシュが消える | sharedMaterial を使わないと KoiKatsu のマテリアルを破壊するバグも発生 |
| `SetShapeBodyValue(13, 1f)` を毎フレーム | 両乳首が強制突出（左右判定なし） | ShapeBodyInfoFemale は左右共通で書き込む |
| 無効アイテムで index=0 に折り返し | siri ゾーンで電マ(id=2)に到達不可 | id=1 がスキップ対象のとき 0→1→skip→0 のループになる。前進検索が必要 |
| `(bool left, bool right)` ValueTuple を返す | CS8137/CS8179 コンパイルエラー | .NET Framework + このプロジェクト構成では ValueTuple 参照が解決されない。`out` 引数を使う |

---

## 8. 残課題

### 乳首補正リセット問題（未解決、回避策あり）

#### 症状
胸愛撫中に乳首突起補正が一時リセットされ（ボーン位置がゼロに戻る）、その後自動回復する。全モード共通。

#### ログで確認した事実
- リセット直前のログ: `zone=none useItem=null`（両手とも）
- `selectKindTouch` が一時的に `none` を返し、finger アイテム検出が途切れたことでデバウンス（0.4s）が満了 → `SetNipState(false)` が呼ばれる
- 確実なトリガーは不明。アニメーション遷移・物理演算による胸コライダーのズレ・モード切り替え等が疑われる

#### 試みた対処と結果

| 対処 | 結果 |
|---|---|
| デバウンスを 0.4s → 1.5s に延長 | 発生頻度変わらず（zone=none が 1.5s 以上続くケースあり） |
| finger チェック廃止、ゾーン検出のみに変更 | 乳首消え問題は変わらず、かつ非 finger アイテム（電マ・手）でも突起が発生してアイテム貫通という新問題が発生 |

#### 解決案（未実装）
**ゾーン別 2 段階デバウンス**:
- `selectKindTouch = none`（曖昧）→ 2.0s デバウンス
- `selectKindTouch = 明確に非 mune ゾーン（kokan/siri/anal）` → 0.4s デバウンス

物理ズレ等による一時的な `none` と、意図的に胸から離れた場合を区別できる。ただし `none` 状態が長く続く場合は効果が薄い可能性あり。

#### 現在の回避策
`Nipple Protrusion` のデフォルトを 1.0（= ボーンオフセット 0m）に変更。リセットが起きても視覚的変化がなくなる。

---

### 舌カレス設計見直し（優先度: 高）

再開時に判断が必要な未解決点:
1. **舌の形状**: p_tang2 bind pose がアナル形状のまま。ゾーン別形状の取得方法未確定
2. **胸ゾーンのポーズ変化（M_Touch）**: GetTouchState が "" を返すため未適用
3. **アイテムサイクル末尾の舌モード**: コメントアウト済み。再開時に手/舌の切り替え UI を再設計する

### 診断コードの除去（優先度: 低）

全機能の動作確認完了後に整理:
- `DiscoverVoiceMembers` 内の過剰なログ
- `HasState` 診断ブロック（確認済みのため不要）
- `DIAG siri ENTER` ブロック
- `LogNipBones("DIAG")` の定期出力

---

## 9. デバッグ Tips

ログファイル: `E:\Koikatsu_HF3.36\output_log.txt`

| ログキーワード | 意味 |
|---|---|
| `TongueCaress disabled — nipple management only.` | 舌カレス無効化・乳首管理のみで初期化完了 |
| `LoadItem (L/R) id=X kindTouch=Y` | アイテム登録確認 |
| `SetItem_Pre (L/R): zone=Z idObject A → B` | アイテム切り替え発動確認 |
| `Cycle triggered (L=True R=False)` | コントローラーボタン検出 |
| `id=X invalid → skipped to index=Y id=Z` | 無効アイテムスキップ確認 |
| `NipState ON: nipStandRate=X caressedL=Y caressedR=Z` | 乳首管理開始確認 |
| `NipState OFF` | 乳首管理終了確認 |
| `HandDiag (L/R): nowAnim=X zone=Y useItem=Z` | 3秒ごとの手モード診断 |

パッチ失敗の典型症状: ログが全く出ない・エラーなしで機能が動かない → named method param 混入を疑う

---

## 10. 次バージョン計画

### 10.1 舌カレス（設計見直し・再実装）

コード自体は `TongueCaress.cs` に保存済み（`TongueCaressEnabled = false` で無効化中）。
再開前に以下3点の方針を決める必要がある。

**① 舌の形状問題**
p_tang2 の bind pose がアナル形状のまま。原因候補:
- bind pose 自体がアナル向けにベイクされている
- ゾーン別形状は男性アニメーターの Sita_ ステートによって決まり、コピーには Animator がないため再現不可

調査手順: Runtime Unity Editor で通常プレイ中に男性アニメーターの各 Sita_ ステートを確認し、
胸/股間/尻向けのボーン姿勢を取得する。
代替案: ゾーン進入時に男性アニメーターへ CrossFade を発行し、数フレーム後にボーン姿勢をスナップショット。

**② 手/舌モード切り替え UI**
旧実装ではアイテムサイクル末尾に舌モードを挿入していたが、現在はコメントアウト済み。
再設計の選択肢:
- 専用 VR ボタン（Menu ボタン長押し等）でトグル
- 視覚 HUD と合わせて専用ジェスチャーで切り替え
- アイテムサイクル末尾に復活させる（ただし UI で現状が分かることが前提）

**③ 胸ゾーンのポーズ変化（M_Touch）**
`GetTouchState("muneL/R")` が `""` を返すため未適用。
舌モード中はハンド干渉がないため `"M_Touch"` を安全に返すよう変更可能。
確認事項: M_Touch 適用後に VRHandCtrl の胸コライダー位置がズレて次回のタッチ検出に影響しないか。

---

### 10.2 現在ゾーンの視覚表示

**目的:** 今どのゾーン（胸 L/R・股間・尻 L/R）を愛撫しているかを VR 内でひと目で確認できるようにする。

**実装方針（案）:**

VR では Screen Space UI（`Camera.main` に追従する Canvas）は正常に描画されない場合がある。
World Space Canvas を HMD 直下に配置するのが最も確実。

```csharp
// HMD 前方 30〜40cm に World Space Canvas を配置する例
var canvasGo = new GameObject("KK_AibuVR_HUD");
canvasGo.transform.SetParent(vrCamera.transform, false);
canvasGo.transform.localPosition = new Vector3(0f, -0.05f, 0.35f);
canvasGo.transform.localRotation = Quaternion.identity;

var canvas = canvasGo.AddComponent<Canvas>();
canvas.renderMode = RenderMode.WorldSpace;
canvasGo.AddComponent<CanvasScaler>();   // 省略可
canvasGo.layer = 0;                      // VR カメラがカリングしないレイヤー

var textGo = new GameObject("ZoneText");
textGo.transform.SetParent(canvasGo.transform, false);
var text = textGo.AddComponent<UnityEngine.UI.Text>();
text.text = "muneL";
text.fontSize = 14;
text.color = Color.white;
// RectTransform のサイズを適切に設定すること
```

**表示内容の候補:**
- 現在アクティブなゾーン名（日本語表記でもよい）
- 各手の useItem 名（モミ / 電マ / バイブ）
- NipState ON/OFF インジケーター

**注意点:**
- `UnityEngine.UI.Text` は Unity 2018 に標準搭載されており TextMeshPro 不要
- Canvas の `localScale` を小さく設定しないと巨大に見える（`0.001f` 程度が目安）
- `vrCamera` は `Resources.FindObjectsOfTypeAll<SteamVR_Camera>().FirstOrDefault()` で取得済み

---

### 10.3 現在の切り替えアイテムの視覚表示

**目的:** 左右それぞれの手が今どのアイテム（モミ / 電マ / バイブ / 舌モード）を使っているか VR 内で確認できるようにする。

**表示内容の候補:**
- 左手: `id=0 (モミ)` / 右手: `id=2 (電マ)` のような固定位置テキスト
- ゾーン別に使えないアイテムをグレーアウト表示（選択肢の全体像を把握しやすくする）
- アイテム切り替え時に短時間（2〜3秒）だけ表示して自動消灯（常時表示は邪魔になる可能性）

**アイテム名マッピング（暫定）:**

| id | 表示名 | 備考 |
|---|---|---|
| 0 | モミ | 全ゾーン有効 |
| 1 | 指 | siri 除外 |
| 2 | 電マ | 全ゾーン有効 |
| 3 | バイブ | 胸のみ有効 |
| — | 舌 | 舌モード有効時 |

実際のアセット名は `dicItem` のキーと `AibuItem.obj.name` をログで確認して確定させること。

**実装方針:**
10.2 の World Space Canvas を共用し、ゾーン表示と同じ HUD に統合するのが望ましい。
`AibuItemTracker.ItemState.CurrentId` と `VRHandCtrlPatches.F_handLR` で左右の現在アイテムを取得できる。

---

## 12. 参照

- ゲーム内部 API 解析: `E:\Koikatsu_HF3.36\_MyPlugins\GameAPI_Analysis.md`
- Harmony v1: https://harmony.pardeike.net/
- BepInEx: https://docs.bepinex.dev/
