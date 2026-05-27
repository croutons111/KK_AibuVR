using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace KK_AibuVR
{
    [HarmonyPatch]
    internal static class VRHandCtrlPatches
    {
        internal static readonly FieldInfo F_dicItem          = AccessTools.Field(typeof(VRHandCtrl), "dicItem");
        internal static readonly FieldInfo F_useItem          = AccessTools.Field(typeof(VRHandCtrl), "useItem");
        internal static readonly FieldInfo F_handLR           = AccessTools.Field(typeof(VRHandCtrl), "handLR");
        internal static readonly FieldInfo F_idObject         = AccessTools.Field(typeof(VRHandCtrl.LayerInfo), "idObject");
        internal static readonly FieldInfo F_kindTouch        = AccessTools.Field(typeof(VRHandCtrl.AibuItem), "kindTouch");
        internal static readonly FieldInfo F_layer            = AccessTools.Field(typeof(VRHandCtrl.AibuItem), "layer");
        internal static readonly FieldInfo F_selectKindTouch  = AccessTools.Field(typeof(VRHandCtrl), "selectKindTouch");
        internal static readonly MethodInfo MI_SetItem        = AccessTools.Method(typeof(VRHandCtrl), "SetItem");

        // AibuColliderKind: none=0,mouth=1, muneL=2,muneR=3(breast), kokan=4,anal=5,siriL=6,siriR=7
        // kindTouch - 2 → areaIndex: muneL/R→0, kokan→1, anal→2, siriL/R→3
        internal static readonly int[] s_kindToArea = { 0, 0, 1, 2, 3, 3 };

        // バイブ(id=3)は kokan/anal/siri では Animator Layer 6 が存在せず不可視になるため除外
        // breast ゾーン (muneL=2, muneR=3) のみ全アイテム有効
        // siriL(6)/siriR(7) では id=1 がめり込むため除外（id=0 モミ・id=2 電マは有効）
        internal static bool IsIdValidForZone(int itemId, int kindInt)
        {
            bool isBreastOrUnknown = kindInt <= 3; // none,mouth,muneL,muneR
            if (!isBreastOrUnknown && itemId >= 3) return false;          // バイブはsiri/kokan/anal不可
            bool isSiri = kindInt == 6 || kindInt == 7;                   // siriL=6, siriR=7
            if (isSiri && itemId == 1) return false;                      // id=1 はsiriでめり込む
            return true;
        }

        [HarmonyPatch(typeof(VRHandCtrl), "LoadItemObject")]
        [HarmonyPostfix]
        private static void LoadItemObject_Post(VRHandCtrl __instance, bool __result)
        {
            if (!__result) return;

            var dic = (Dictionary<int, VRHandCtrl.AibuItem>)F_dicItem.GetValue(__instance);
            if (dic == null || dic.Count == 0) return;

            var lr    = F_handLR.GetValue(__instance);
            var state = AibuItemTracker.GetOrCreate(__instance);
            state.ItemIds.Clear();
            foreach (var key in dic.Keys)
                state.ItemIds.Add(key);
            state.ItemIds.Sort();

            foreach (var kv in dic)
                Plugin.Logger.LogInfo($"[KK_AibuVR] LoadItem ({lr}) id={kv.Key} kindTouch={F_kindTouch.GetValue(kv.Value)}");
            Plugin.Logger.LogInfo(
                $"[KK_AibuVR] VRHandCtrl({lr}) – {state.ItemIds.Count} item(s): [{string.Join(", ", state.ItemIds.Select(x => x.ToString()).ToArray())}]");
        }

        [HarmonyPatch(typeof(VRHandCtrl), "SetItem")]
        [HarmonyPrefix]
        private static void SetItem_Pre(VRHandCtrl __instance, VRHandCtrl.LayerInfo _infoLayer, ref int __state)
        {
            __state = -1;
            if (_infoLayer == null) return;
            if (!AibuItemTracker.TryGet(__instance, out var tracker)) return;
            if (tracker.ItemIds.Count == 0) return;

            var dic = (Dictionary<int, VRHandCtrl.AibuItem>)F_dicItem.GetValue(__instance);
            if (dic == null) return;

            int zoneKind = Convert.ToInt32(F_selectKindTouch.GetValue(__instance));
            var lr       = F_handLR.GetValue(__instance);
            int origId   = (int)F_idObject.GetValue(_infoLayer);

            int targetId = tracker.CurrentId;

            // non-breast ゾーンでバイブ(id≥3)は対応外 → id=0 にフォールバック
            if (!IsIdValidForZone(targetId, zoneKind))
                targetId = tracker.ItemIds[0];

            // dic に targetId がなければ id=0 にフォールバック
            if (!dic.ContainsKey(targetId))
                targetId = tracker.ItemIds[0];

            // dic に id=0 もなければ何もできない
            if (!dic.ContainsKey(targetId)) return;

            // idObject を常に正しいターゲットに維持する。
            // Post での復元を廃止したため、DragAction は SetItem 実行後も
            // idObject=targetId を読み取り、正しいアニメーション状態（例:K_Touch）を選択できる。
            if (origId != targetId)
            {
                F_idObject.SetValue(_infoLayer, targetId);
                Plugin.Logger.LogInfo(
                    $"[KK_AibuVR] SetItem_Pre ({lr}): zone={zoneKind} idObject {origId} → {targetId} (index={tracker.CurrentIndex})");
            }
        }

        // CRITICAL: Do NOT add named method params (e.g. _isFront, _kindTouch) here.
        // Harmony v1 (0Harmony.dll) will silently fail BOTH SetItem_Pre AND SetItem_Post.
        // Only Harmony special vars (__instance, __result, __state) are safe.
        [HarmonyPatch(typeof(VRHandCtrl), "SetItem")]
        [HarmonyPostfix]
        private static void SetItem_Post(VRHandCtrl __instance, VRHandCtrl.LayerInfo _infoLayer, bool __result, int __state)
        {
            if (_infoLayer == null) return;

            var lr = F_handLR.GetValue(__instance);
            var ui = (VRHandCtrl.AibuItem)F_useItem.GetValue(__instance);
            Plugin.Logger.LogInfo(
                $"[KK_AibuVR] SetItem_Post ({lr}): result={__result}" +
                $" useItem={(ui == null ? "null" : (ui.obj == null ? "obj=null" : ui.obj.name + " active=" + ui.obj.activeSelf))}");
        }
    }

    [HarmonyPatch(typeof(VRHandCtrl), "IsItemTouch")]
    internal static class VRHandCtrl_IsItemTouch_Patch
    {
        [HarmonyPostfix]
        private static void Postfix(ref bool __result)
        {
            if (TongueCaressController.TongueModeActive)
                __result = false;
        }
    }

    [HarmonyPatch(typeof(VRHScene), "Update")]
    internal static class VRHScene_Update_Patch
    {
        // private static bool s_tongueModeActive = false;  // 舌モードサイクルは一時停止中
        private static readonly FieldInfo  F_vrHands              = AccessTools.Field(typeof(VRHScene), "vrHands");
        private static readonly FieldInfo  F_viveCtrlMgr          = AccessTools.Field(typeof(VRHandCtrl), "viveCtrlManager");
        // IsPressDownSelectHand(EViveButtonKind, int num, int mode) … 手番号指定 (0=左, 1=右)
        private static readonly MethodInfo MI_IsPressDownSelectHand = AccessTools.Method(
            typeof(VRViveControllerManager), "IsPressDownSelectHand",
            new[] { typeof(VRViveController.EViveButtonKind), typeof(int), typeof(int) });

        // 左右どちらの手をサイクルするか判定。keyboard=両手、VRボタン=押した側のみ。
        // out 引数で返す（ValueTuple 非対応環境のため）
        private static void GetCycleHands(VRHandCtrl[] vrHands, out bool left, out bool right)
        {
            if (Plugin.CycleItemKey.Value.IsDown()) { left = true; right = true; return; }

            left = false; right = false;
            if (vrHands == null || vrHands.Length == 0) return;

            object mgr = null!;
            foreach (var h in vrHands)
            {
                if (h == null) continue;
                mgr = F_viveCtrlMgr.GetValue(h);
                if (mgr != null) break;
            }
            if (mgr == null) return;

            var btnL = Plugin.CycleButtonVR_L.Value;
            var btnR = Plugin.CycleButtonVR_R.Value;
            // handNum: 0=左コントローラー, 1=右コントローラー
            if (btnL != Plugin.ViveButton.None)
                left  = (bool)(MI_IsPressDownSelectHand.Invoke(mgr,
                    new object[] { (VRViveController.EViveButtonKind)(int)btnL, 0, 0 }) ?? false);
            if (btnR != Plugin.ViveButton.None)
                right = (bool)(MI_IsPressDownSelectHand.Invoke(mgr,
                    new object[] { (VRViveController.EViveButtonKind)(int)btnR, 1, 0 }) ?? false);
        }

        [HarmonyPostfix]
        private static void Postfix(VRHScene __instance)
        {
            if (!Plugin.HandCaressEnabled.Value) return;

            var vrHands = (VRHandCtrl[])F_vrHands.GetValue(__instance);
            bool cycleLeft, cycleRight;
            GetCycleHands(vrHands, out cycleLeft, out cycleRight);
            if (!cycleLeft && !cycleRight) return;

            if (vrHands == null)
            {
                Plugin.Logger.LogWarning("[KK_AibuVR] vrHands is null");
                return;
            }

            Plugin.Logger.LogInfo($"[KK_AibuVR] ===== Cycle triggered (L={cycleLeft} R={cycleRight}) =====");

            // ---- 舌モードサイクルは一時停止中 ----
            // // Tongue mode toggle
            // if (s_tongueModeActive) { ... }
            // // Check if the next cycle would exhaust valid items → enter tongue mode instead
            // bool enterTongue = false; ...
            // if (enterTongue) { s_tongueModeActive = true; ... }
            // ---------------------------------------

            foreach (var hand in vrHands)
            {
                if (hand == null) continue;

                // 押されたコントローラー側の手のみサイクル (handLR: 0=Left, 1=Right)
                int handLRInt = Convert.ToInt32(VRHandCtrlPatches.F_handLR.GetValue(hand));
                if (handLRInt == 0 && !cycleLeft)  continue;
                if (handLRInt == 1 && !cycleRight) continue;

                var lr     = VRHandCtrlPatches.F_handLR.GetValue(hand);
                var before = (VRHandCtrl.AibuItem)VRHandCtrlPatches.F_useItem.GetValue(hand);
                var dic    = (Dictionary<int, VRHandCtrl.AibuItem>)VRHandCtrlPatches.F_dicItem.GetValue(hand);
                var zone   = VRHandCtrlPatches.F_selectKindTouch.GetValue(hand);
                int zoneKind = Convert.ToInt32(zone);

                if (!AibuItemTracker.TryGet(hand, out var stateBefore))
                {
                    Plugin.Logger.LogWarning($"[KK_AibuVR] ({lr}) tracker not found → skip");
                    continue;
                }

                Plugin.Logger.LogInfo(
                    $"[KK_AibuVR] ({lr}) BEFORE: index={stateBefore.CurrentIndex} id={stateBefore.CurrentId}" +
                    $" | zone={zone}" +
                    $" | useItem={(before == null ? "null" : (before.obj == null ? "obj=null" : before.obj.name + " active=" + before.obj.activeSelf))}" +
                    $" | dicCount={dic?.Count}");

                AibuItemTracker.CycleNext(hand);
                var state = AibuItemTracker.GetOrCreate(hand);

                // 現在ゾーンで無効なアイテムに進んだ場合、次の有効アイテムへスキップ
                // （単純に index=0 へ戻すと id=1 を挟んだ id=2 に到達できないため前進検索）
                if (!VRHandCtrlPatches.IsIdValidForZone(state.CurrentId, zoneKind))
                {
                    int searchFrom = state.CurrentIndex;
                    bool found = false;
                    for (int i = 1; i < state.ItemIds.Count; i++)
                    {
                        int tryIdx = (searchFrom + i) % state.ItemIds.Count;
                        if (VRHandCtrlPatches.IsIdValidForZone(state.ItemIds[tryIdx], zoneKind))
                        {
                            state.CurrentIndex = tryIdx;
                            found = true;
                            break;
                        }
                    }
                    if (!found) state.CurrentIndex = 0;
                    Plugin.Logger.LogInfo(
                        $"[KK_AibuVR] ({lr}) zone={zone} id={state.ItemIds[searchFrom]} invalid → skipped to index={state.CurrentIndex} id={state.CurrentId}");
                }

                Plugin.Logger.LogInfo($"[KK_AibuVR] ({lr}) AFTER CycleNext: index={state.CurrentIndex} id={state.CurrentId}");

                if (dic == null || !dic.ContainsKey(state.CurrentId))
                {
                    Plugin.Logger.LogWarning($"[KK_AibuVR] ({lr}) dic does not contain id={state.CurrentId} → skip");
                    continue;
                }

                if (before == null)
                {
                    Plugin.Logger.LogInfo($"[KK_AibuVR] ({lr}) useItem=null (neutral) → will apply on next touch");
                    continue;
                }

                TryImmediateSwap(hand, state, before, lr.ToString());
            }
        }

        private static void TryImmediateSwap(
            VRHandCtrl hand, AibuItemTracker.ItemState state,
            VRHandCtrl.AibuItem currentItem, string lr)
        {
            try
            {
                var kindTouchObj = VRHandCtrlPatches.F_selectKindTouch.GetValue(hand);
                int kindInt      = Convert.ToInt32(kindTouchObj);

                if (kindInt < 2 || kindInt > 7)
                {
                    Plugin.Logger.LogInfo($"[KK_AibuVR] ({lr}) selectKindTouch={kindTouchObj} (not in zone 2-7) → skip immediate");
                    return;
                }

                var layer = VRHandCtrlPatches.F_layer.GetValue(currentItem) as VRHandCtrl.LayerInfo;
                if (layer == null)
                {
                    Plugin.Logger.LogWarning($"[KK_AibuVR] ({lr}) currentItem.layer is null → skip immediate");
                    return;
                }

                int areaIndex = VRHandCtrlPatches.s_kindToArea[kindInt - 2];

                Plugin.Logger.LogInfo(
                    $"[KK_AibuVR] ({lr}) IMMEDIATE SWAP: zone={kindTouchObj} area={areaIndex} targetId={state.CurrentId}" +
                    $" hiding {currentItem.obj?.name}");

                // SetItem は旧アイテムを非表示にしないため先に隠す
                if (currentItem.obj != null)
                    currentItem.obj.SetActive(false);

                // NipState をリセットしてから SetItem を呼ぶ。
                // SetItem は内部で DisableShapeNip を呼ぶため、NipStateActive=true のままだと
                // DisableShapeNip パッチがブロックしてしまい切り替えが失敗する。
                TongueCaressController.NotifyItemSwitch();

                // SetItem を直接呼び出す → SetItem_Pre が idObject を書き換えて正しいアイテムを選択させる
                VRHandCtrlPatches.MI_SetItem.Invoke(
                    hand,
                    new object[] { areaIndex, layer, 0, kindTouchObj, true, false });
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[KK_AibuVR] ({lr}) TryImmediateSwap exception: {ex}");
            }
        }
    }

    // Re-apply touch pose after HAibu.LateProc in case HAibu overrode our CrossFade.
    // Also manages hand-mode diagnostic logging and nipple state for p_fingerL breast caress.
    [HarmonyPatch(typeof(HAibu), "LateProc")]
    internal static class HAibu_LateProc_Patch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            TongueCaressController.Instance?.ApplyTouchPose();
            TongueCaressController.UpdateHandMode();
        }
    }
}
