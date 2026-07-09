# KK_AibuVR

**English** | [日本語](README.ja.md)

> A BepInEx plugin for **Koikatsu VR** that lets you switch hand-caress items on the fly and keeps nipples naturally raised during breast play.

---

## Overview

In the VR H-scene, the hand-caress item is normally fixed per zone. KK_AibuVR adds two things:

- **Hand item switching** — cycle the caress item on each hand in real time, using a VR controller button or the keyboard.
- **Nipple-stand correction** — keeps nipples from sinking into the breast during finger play.

> **Beta** — verified on HF Patch 3.36 with Meta Quest 2 (Oculus Link / Air Link).

---

## Requirements

| Item | Requirement |
|---|---|
| Game | Koikatsu (HF Patch 3.36) — **`KoikatuVR.exe` only** (VR build) |
| Framework | BepInEx 5.4.x |
| VR headset | Meta Quest 2 (other headsets untested) |

This plugin has no dependency other than BepInEx. It does nothing in the non-VR build.

---

## Installation

1. Download the latest `KK_AibuVR.dll` from [Releases](../../releases).
2. Place it in your `BepInEx/plugins/` folder.
3. Start `KoikatuVR.exe`.

---

## How to Use

Works in the VR H-scene (caress / insertion / hand / mouth modes).

### Switching items

- Press the configured **controller button** or **keyboard key** to cycle the caress item.
- **Left and right hands switch independently** (each controller has its own button).
- Items that can't be used in the current caress zone are **skipped automatically**.

**Item / zone availability:**

| Item | Breasts | Crotch / Anal | Butt |
|---|---|---|---|
| Grope | ✔ | ✔ | ✔ |
| Rub / stroke | ✔ | ✔ | ✖ (clips into the body, skipped) |
| Massager (denma) | ✔ | ✔ | ✔ |
| Vibrator | ✔ (breasts only) | ✖ | ✖ |

> The vibrator only appears on the breasts (game limitation).

### Nipple-stand correction

- Applies automatically when finger caress on the breast is detected, and releases when you move away.
- Strength is adjustable (see Configuration). Default is off.

---

## Configuration

Change these in the in-game **BepInEx Configuration Manager** (default: `F1`).

### Hand Caress

| Setting | Default | Description |
|---|---|---|
| Enabled | `true` | Master on/off. `false` disables both item switching and nipple correction (vanilla behavior). |
| Nipple Protrusion | `1.0` | Nipple forward offset during breast caress. `1.0` = no correction, `3.0` = recommended (~6 mm), `8.0` = max (~20 mm). |

### Controls

| Setting | Default | Description |
|---|---|---|
| Cycle Key (Keyboard) | `Tab` | Cycles the item on **both hands** at once. |
| Cycle Button - Left Controller | `Grip` | Cycles the **left** hand's item. |
| Cycle Button - Right Controller | `Grip` | Cycles the **right** hand's item. |

**Button choices:** `None` (disabled) / `Grip` / `Menu` / `Touchpad` / `Touchpad_Up` / `Touchpad_Down` / `Touchpad_Left` / `Touchpad_Right`

Set a controller to `None` to disable switching on that hand only; the other hand is unaffected.

---

## Notes

- The tongue-caress feature is currently disabled (under review).
- Only HF Patch 3.36 has been verified; other versions are untested.

---

## License

[GNU General Public License v3.0](LICENSE)

---

## Disclaimer

This is an adult (R18) mod intended for the H-scene. Use at your own responsibility.
