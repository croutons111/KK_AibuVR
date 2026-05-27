# KK_AibuVR

**Koikatsu VR**（`KoikatuVR.exe`）の H シーンで、ハンド愛撫アイテムの切り替えと乳首立ち管理を行う BepInEx プラグインです。

---

## 背景

VR 版の `VRHandCtrl` はすべてのハンドアイテムを `dicItem` に読み込みますが、通常は id=0 しか使用しません。本プラグインはアイテムリストを直接読み取り、コントローラーボタンまたはキーボードでハンドごとにアイテムを切り替えられるようにします。また、胸愛撫中に VR の DragAction が乳首ボーンを押し込んでしまう問題を打ち消します。

---

## 機能

### ハンドアイテム切り替え

- コントローラーボタンまたはキーボードショートカットでハンドアイテムを切り替え
- **左右のハンドは独立** — 各ハンドは対応するコントローラーボタンが押されたときのみ切り替わります。キーボード操作では両手同時に切り替わります
- 現在の愛撫ゾーンと互換性のないアイテムは自動的にスキップ（下表参照）
- 無効なアイテムが次の候補になる場合、id=0 に巻き戻さず前方検索で次の有効アイテムへ進みます

**ゾーン互換性:**

| アイテム | 胸（muneL/R） | 股間・アナル | 尻（L/R） | 除外理由 |
|---|---|---|---|---|
| id=0（もみ） | ✔ | ✔ | ✔ | — |
| id=1 | ✔ | ✔ | ✖ | 尻ゾーンでボディにめり込む |
| id=2（電マ） | ✔ | ✔ | ✔ | — |
| id=3（バイブ） | ✔ | ✖ | ✖ | 胸以外のゾーンにアニメーターレイヤー 6 が存在しないためアイテムが非表示になる |

尻ゾーンでの切り替え例:
```
id=0（もみ） → [id=1 スキップ] → id=2（電マ） → [id=3 スキップ] → id=0 → …
```

### 乳首立ち管理

VR の胸愛撫中、DragAction が親ボーン（`cf_d_bnip01`）を DynamicBone の力で内側に押し込み、乳首が沈んでしまいます。本プラグインは毎 LateUpdate でこれを打ち消します:

- 愛撫中は毎フレーム `cf_j_bnip02_L/R.localPosition = (0, 0, 突出量)` をセット
- アレオラ形状を保持するため `DisableShapeNip(lr, true)` の呼び出しをブロック
- `UpdateShapeBody` Prefix パッチで `disableBustShapeMask[lr, 8]`（cf_ShapeMaskNipStand）をゲームが読む前にクリアし、NipStand ブレンドシェイプを維持
- **両手同時胸愛撫に対応** — `_caressedL` / `_caressedR` フラグで左右を独立管理
- 愛撫終了時はボーン位置を `Vector3.zero` にリセットし、オフセットが残らないようにします

突出量は設定の `Nipple Protrusion`（後述）で調整できます。

---

## 動作環境

| 項目 | 要件 |
|---|---|
| ゲーム | Koikatsu（HF Patch 3.36）— `KoikatuVR.exe` のみ |
| BepInEx | 5.4.x |
| Harmony | 0Harmony.dll v1（BepInEx 同梱） |

---

## インストール

1. [Releases](../../releases) から最新の `KK_AibuVR.dll` をダウンロード
2. `BepInEx/plugins/` フォルダに配置
3. `KoikatuVR.exe` を起動

---

## 設定

ゲーム内 **ConfigurationManager**（デフォルト: `F1`）からリアルタイムに設定を変更できます。

### Hand Caress（ハンド愛撫）

| キー | デフォルト | 説明 |
|---|---|---|
| Enabled | `true` | マスタースイッチ。`false` にするとアイテム切り替えと乳首管理の両方が無効になります |
| Nipple Protrusion | `3.0` | 胸愛撫中の `cf_j_bnip02` Z オフセット（範囲: 1.0〜8.0）。`1.0` = オフセットなし、`3.0` ≈ 6mm 前方、`8.0` ≈ 20mm 前方 |

### Controls（操作）

| キー | デフォルト | 説明 |
|---|---|---|
| Cycle Key (Keyboard) | `Tab` | **両手**同時にアイテムを切り替えます |
| Cycle Button - Left Controller | `Grip` | **左手**のみを切り替える VR コントローラーボタン |
| Cycle Button - Right Controller | `Grip` | **右手**のみを切り替える VR コントローラーボタン |

**VR ボタンの選択肢:** `None`（無効）/ `Grip` / `Menu` / `Touchpad` / `Touchpad_Up` / `Touchpad_Down` / `Touchpad_Left` / `Touchpad_Right`

コントローラーボタンを `None` に設定すると、そのハンドの VR 切り替えが無効になります（もう一方のハンドには影響しません）。

---

## 技術メモ

### アイテム切り替えに `SetItem` への Harmony パッチが必要な理由

`VRHandCtrl.SetItem` は `LayerInfo.idObject` を読み取ってどのアイテムをアクティブにするかを決定します。プラグインの `SetItem_Pre` パッチは `SetItem` 実行直前に `idObject` をトラッカーの `CurrentId` で上書きするため、ゲームの内部状態を変えることなく正しいアイテムが選択されます。

### 乳首管理に `ChangeNipRate` / `DisableShapeNip` だけでは不十分な理由

これらの API はブレンドシェイプマスクを制御するものであり、ボーン位置は操作しません。VR の DragAction は毎フレーム DynamicBone の力で `cf_d_bnip01` を操作するため、ブレンドシェイプの結果を上書きしてしまいます。DragAction に対して有効なのは `cf_j_bnip02` への `localPosition` 直接上書きだけです。

### Harmony v1 の制約

本プラグインは `0Harmony.dll`（v1）を対象としています。v1 ではパッチメソッドに名前付きメソッドパラメーターを使用すると、同クラス内の**すべて**のパッチが無効になります。パラメーターには Harmony 特殊変数（`__instance`、`__result`、`__state`、`__args`）のみ使用してください。

---

## 既知の制限・保留事項

- **舌愛撫**（HMD 近接 → 舌アニメーション）は実装済みですが、仕様再設計のため現在**無効化**しています（`TongueCaress.cs` の `TongueCaressEnabled = false`）。コードは将来の再有効化に備えて保持されています。
- アイテム名（もみ、電マ、バイブ）は動作から推測したものであり、実際のアセット名は HF Patch バージョンによって異なる場合があります。
- HF Patch 3.36 でのみ動作確認済みです。

---

## ライセンス

[GNU General Public License v3.0](LICENSE)
