using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace KK_AibuVR
{
    // Tongue caress: HMD proximity to a body zone bone activates the corresponding
    // Sita_ (tongue) animator layer and CrossFades to its entry state.
    //
    // Layer mapping (confirmed via HasState DIAG):
    //   Layer 31 → Sita_mune_*  (breast)
    //   Layer 37 → Sita_kokan_* (groin)
    //   Layer for siri: detected at runtime via HasState scan
    [DefaultExecutionOrder(30000)]  // run LateUpdate after VR animator and DragAction
    internal class TongueCaressController : MonoBehaviour
    {
        private static readonly FieldInfo F_flags   = AccessTools.Field(typeof(VRHScene), "flags");
        private static readonly FieldInfo F_vrHands = AccessTools.Field(typeof(VRHScene), "vrHands");

        // Feel/gauge reflection — discovered at runtime
        private static FieldInfo  _fRateDragGauge;   // HFlag.rateDragGauge (float)
        private static MethodInfo _miFemaleGaugeUp;  // HFlag.FemaleGaugeUp(float,bool,bool)
        private static bool       _feelDiscovered;

        // voice trigger: HFlag.voice (VoiceFlag) and VoiceFlag.playVoices
        private static readonly FieldInfo F_flagsVoice =
            AccessTools.Field(typeof(HFlag), "voice");
        private static readonly FieldInfo F_playVoices =
            F_flagsVoice != null
            ? AccessTools.Field(F_flagsVoice.FieldType, "playVoices")
            : null;

        // Layers confirmed by diagnostic
        private const int LayerMuneL = 31;   // Sita_mune_sawari_L
        private const int LayerMuneR = 32;   // Sita_mune_sawari_R
        private const int LayerKokan = 37;

        private static readonly FieldInfo F_nowAnimStateName =
            AccessTools.Field(typeof(HFlag), "nowAnimStateName");

        private VRHandCtrl[] _vrHands              = null!;
        private string       _appliedTouchState    = "";
        private static readonly int IdleHash = Animator.StringToHash("Idle");
        private string       _lastApplyPath = "";
        private float        _diagLogTimer  = 0f;

        // Hand-mode nipple state management
        private ChaControl       _femaleCha            = null!;
        private static MethodInfo   MI_ChangeNipState    = null!;
        private static PropertyInfo _piFileStatus        = null!;
        private static PropertyInfo _piNipStandRate      = null!;
        private static FieldInfo    _fiNipStandRate      = null!;
        private static MethodInfo   MI_ChangeSettingNip  = null!;
        private static MethodInfo   MI_DisableShapeNip    = null!; // DisableShapeNip(int, bool)
        private static MethodInfo   MI_DisableShapeBodyID = null!; // DisableShapeBodyID(int, int, bool)
        private static MethodInfo   MI_SetShapeBodyValue  = null!; // SetShapeBodyValue(int, float)
        // ChaFileStatus.disableBustShapeMask — bool[,] accessed directly to clear mask before UpdateShapeBody reads it
        private static PropertyInfo _piDisableBustShapeMask = null!;
        // 舌カレス機能の有効/無効フラグ。false=無効（乳首管理は引き続き動作する）。
        // 再有効化するには true に変更してビルドする。
        private static readonly bool TongueCaressEnabled = false;

        // ChaFileDefine.cf_ShapeMaskNipStand = 8  (index into cf_BustShapeMaskID[])
        // cf_BustShapeMaskID[8] = 13 = body shape index for NipStand (drives cf_j_bnip02 bones)
        private const int NipStandShapeID  = 8;
        private const int NipStandBodyIdx  = 13;
        private bool             _nipStateActive       = false;
        private float            _origNipStandRate     = 0f;
        private Vector3          _origRootScaleR       = Vector3.one;
        private Vector3          _origRootScaleL       = Vector3.one;
        private bool             _origMnpaEnabled      = true;
        private bool             _origMnpbEnabled      = true;
        // Which breast(s) are actively caressed by p_fingerL. Both can be true simultaneously
        // when both hands are on breasts. Sticky: cleared only when NipState goes OFF.
        private bool             _caressedL            = false;
        private bool             _caressedR            = false;
        private float            _handDiagTimer        = 0f;
        private float            _nipBoneDiagTimer     = 0f;
        private float            _nipOffDebounce       = 0f;
        private const float      NipOffDelay           = 0.4f;

        // nipple bone references for diagnostic + direct manipulation
        private Transform _bnip02R  = null!;
        private Transform _bnip02L  = null!;
        private Transform _dnip01R  = null!;
        private Transform _dnip01L  = null!;
        private Transform _knipR00  = null!;
        private Transform _knipL00  = null!;
        private bool         _hasKTouch            = false;
        private string       _handModeTouchState   = "";
        private static readonly int KTouchHash     = Animator.StringToHash("K_Touch");

        private HFlag      _flags    = null!;
        private Animator   _animBody = null!;
        private GameObject  _pTang          = null!;  // instantiated p_tang tongue prop (flat shape)
        private Renderer[]  _pTangRenderers  = null!;
        private Renderer[]           _nipRenderers = new Renderer[0];
        private SkinnedMeshRenderer  _bodySmr      = null!;
        private SkinnedMeshRenderer  _mnpaSmr      = null!;
        private SkinnedMeshRenderer  _mnpbSmr      = null!;
        private Transform   _pTangRoot      = null!;  // cf_j_tang_01 inside _pTang for LookRotation
        private GameObject _myFace    = null!;

        private Transform _bustL = null!;
        private Transform _bustR = null!;
        private Transform _kokan = null!;
        private Transform _siriL = null!;
        private Transform _siriR = null!;

        // Siri layer: determined at runtime (may be -1 if not found)
        private int _layerSiri = -1;

        private string _activeZone      = "";
        private int    _activeLayer     = -1;
        private float  _savedLayerWeight = 0f;

        // Male animator Sita_ layer control: drives original p_tang2 bones → LateUpdate copies them
        private Animator _animMale           = null!;
        private int      _maleLayerMuneL     = -1;
        private int      _maleLayerMuneR     = -1;
        private int      _maleLayerKokan     = -1;
        private int      _maleLayerSiriL     = -1;
        private int      _maleLayerSiriR     = -1;
        private int      _maleActiveLayer    = -1;
        private float    _savedMaleWeight    = 0f;
        private float  _voiceTriggerTimer = 0f;
        private float  _zoneExitCountdown    = 0f;
        private float  _entryBlockTimer      = 0f;
        private string _pendingZone          = "";   // zone change debounce
        private float  _zoneChangePendingTime = 0f;
        private const  float ZoneExitDelay       = 0.80f;
        private const  float EntryBlockAfterExit  = 1.50f;
        private const  float ZoneChangeDelay      = 0.50f;  // prevent siriL↔kokan oscillation
        private float  _postOrgasmCooldown    = 0f;
        private const  float PostOrgasmCooldown = 3.0f;     // don't re-apply poses right after orgasm

        // Male tongue bones driven procedurally in LateUpdate (p_tang2 chain)
        private Transform[] _maleTangBones     = new Transform[0];
        private Transform   _maleTangRoot      = null!;
        private Renderer[]  _p_tang2_renderers = new Renderer[0];
        // Original male character's p_tang2 bones: copied each frame in LateUpdate
        // so the tongue shape tracks the male animator's actual animation state.
        private Transform[] _origMaleTangBones = new Transform[0];

        internal static TongueCaressController Instance { get; private set; } = null!;
        internal bool IsActive => _activeZone != "";
        internal static bool TongueModeActive { get; private set; } = false;
        // Exposed for ChaControl_UpdateShapeBody_Patch
        internal static bool NipStateActive { get; private set; } = false;
        internal static ChaControl FemaleCha { get; private set; } = null!;

        internal static void SetTongueMode(bool active)
        {
            if (TongueModeActive == active) return;
            TongueModeActive = active;
            if (!active) Instance?.ExitTongueMode();
            else Instance?.ResetHandMode();  // 舌モード開始時は手モードの状態をリセット
            Plugin.Logger.LogInfo($"[KK_AibuVR] TongueModeActive = {active}");
        }

        // Hand-mode update: diagnostic log + nipple state management for p_fingerL breast caress.
        // Called from HAibu_LateProc_Patch when tongue mode is OFF.
        internal static void UpdateHandMode()
        {
            if (!Plugin.HandCaressEnabled.Value) return;
            if (TongueModeActive || Instance == null) return;
            if (Instance._flags == null || Instance._flags.mode != HFlag.EMode.aibu) return;
            Instance.DoUpdateHandMode();
        }

        private void ResetHandMode()
        {
            _handModeTouchState = "";
            _caressedL = false;
            _caressedR = false;
            SetNipState(false);
        }

        // Called from item-cycling code before SetItem is invoked.
        // SetItem calls DisableShapeNip internally; NipStateActive must be false at that point
        // or our DisableShapeNip patch will block the call and break the switch.
        internal static void NotifyItemSwitch()
        {
            Instance?.ResetHandMode();
        }

        private object GetFileStatus()
        {
            if (_piFileStatus == null || _femaleCha == null) return null;
            return _piFileStatus.GetValue(_femaleCha, null);
        }

        private float GetNipStandRate(object fs)
        {
            if (fs == null) return 0f;
            if (_piNipStandRate != null) return (float)_piNipStandRate.GetValue(fs, null);
            if (_fiNipStandRate  != null) return (float)_fiNipStandRate.GetValue(fs);
            return 0f;
        }

        private void SetNipStandRate(object fs, float rate)
        {
            if (fs == null) return;
            if (_piNipStandRate != null) _piNipStandRate.SetValue(fs, rate, null);
            else if (_fiNipStandRate != null) _fiNipStandRate.SetValue(fs, rate);
        }

        // Bone position is set directly in LateUpdate — no API calls needed.
        internal void MaintainNipState() { }

        // Called from UpdateShapeBody_Patch.Prefix: clears disableBustShapeMask[LR, 8] (cf_ShapeMaskNipStand)
        // BEFORE UpdateShapeBody reads it. DisableShapeBust (not blocked) sets changeShapeBodyMask=true and
        // updateShapeBody=true, causing UpdateShapeBody to run. At that point our DisableShapeBodyID block
        // may not have fired yet (first frame, NipStateActive was false). Clearing the mask here in the
        // Prefix ensures UpdateShapeBody always computes Lerp(shapeValueBody[13], 1f, nipStandRate)
        // instead of 0.5f (flat), regardless of what happened in previous frames.
        internal void ClearNipStandMask()
        {
            if (_femaleCha == null || _piFileStatus == null || _piDisableBustShapeMask == null) return;
            try
            {
                var fs = _piFileStatus.GetValue(_femaleCha, null);
                if (fs == null) return;
                var mask = (bool[,])_piDisableBustShapeMask.GetValue(fs, null);
                if (mask == null || mask.GetLength(1) <= 8) return;
                mask[0, 8] = false;
                mask[1, 8] = false;
            }
            catch { }
        }

        private void SetNipState(bool active)
        {
            if (_nipStateActive == active) return;
            _nipStateActive = active;
            NipStateActive  = active;
            if (active)
            {
                // Log only — no API calls. Bone position is set every frame in LateUpdate.
                // MI_SetShapeBodyValue/DisableShapeNip were removed: they drove both nipples
                // via ShapeBodyInfoFemale and flattened the areola as undesired side effects.
                var fs = GetFileStatus();
                _origNipStandRate = GetNipStandRate(fs);
                Plugin.Logger.LogInfo(
                    $"[KK_AibuVR] NipState ON: nipStandRate={_origNipStandRate:F4} caressedL={_caressedL} caressedR={_caressedR}");
            }
            else
            {
                // Reset nipple bone positions when caress ends so they don't stay at protrusion offset.
                if (_bnip02R != null) _bnip02R.localPosition = Vector3.zero;
                if (_bnip02L != null) _bnip02L.localPosition = Vector3.zero;
                _caressedL = false;
                _caressedR = false;
                Plugin.Logger.LogInfo($"[KK_AibuVR] NipState OFF");
            }
        }

        private void DoUpdateHandMode()
        {
            if (_animBody == null) return;

            // Periodic diagnostic
            _handDiagTimer -= Time.deltaTime;
            string nowAnim = F_nowAnimStateName != null
                ? ((string)F_nowAnimStateName.GetValue(_flags) ?? "") : "";
            if (_handDiagTimer <= 0f)
            {
                _handDiagTimer = 3f;
                int bHash = _animBody.GetCurrentAnimatorStateInfo(0).shortNameHash;
                foreach (var h in _vrHands)
                {
                    if (h == null) continue;
                    var ui   = (VRHandCtrl.AibuItem)VRHandCtrlPatches.F_useItem.GetValue(h);
                    var zone = VRHandCtrlPatches.F_selectKindTouch.GetValue(h);
                    var lr   = VRHandCtrlPatches.F_handLR.GetValue(h);
                    Plugin.Logger.LogInfo(
                        $"[KK_AibuVR] HandDiag ({lr}): nowAnim={nowAnim} baseHash={bHash} zone={zone} useItem={ui?.obj?.name ?? "null"}");
                }
            }

            // Detect p_fingerL breast caress.
            // M_Touch = drag motion, M_Idle = static pinch (sustained nipple caress).
            // Both are breast-zone states set by DragAction.
            // Detect p_fingerL breast caress and which side(s) are being touched.
            bool inBreastAnim    = nowAnim == "M_Touch" || nowAnim == "M_Idle";
            bool fingerBreastActive = false;
            if (inBreastAnim)
            {
                // Reset sticky flags each detection pass so a hand that left the breast clears its side.
                bool detectedL = false;
                bool detectedR = false;
                foreach (var h in _vrHands)
                {
                    if (h == null) continue;
                    var ui = (VRHandCtrl.AibuItem)VRHandCtrlPatches.F_useItem.GetValue(h);
                    if (ui?.obj == null) continue;
                    if (ui.obj.name.IndexOf("finger", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        fingerBreastActive = true;
                        // Sticky zone: update only when a confirmed mune zone is detected.
                        // zone="none" keeps the previous value so protrusion doesn't drop mid-caress.
                        string zoneStr = VRHandCtrlPatches.F_selectKindTouch.GetValue(h)?.ToString() ?? "";
                        if (zoneStr.IndexOf("mune", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if      (zoneStr.IndexOf("R", StringComparison.OrdinalIgnoreCase) >= 0) detectedR = true;
                            else if (zoneStr.IndexOf("L", StringComparison.OrdinalIgnoreCase) >= 0) detectedL = true;
                        }
                    }
                }
                // Sticky: once a side is set, keep it until NipState goes OFF.
                if (detectedL) _caressedL = true;
                if (detectedR) _caressedR = true;
            }

            if (fingerBreastActive)
            {
                _nipOffDebounce = 0f;
                if (!_nipStateActive) SetNipState(true);
            }
            else if (!inBreastAnim)
            {
                // Animation left M_Touch/M_Idle: intentional release — stop immediately, no debounce.
                _nipOffDebounce = 0f;
                if (_nipStateActive) SetNipState(false);
            }
            else
            {
                // Still in M_Touch/M_Idle but finger item momentarily lost: brief glitch → debounce.
                _nipOffDebounce += Time.deltaTime;
                if (_nipOffDebounce >= NipOffDelay) SetNipState(false);
            }

            // Log nipple bone transforms every 3s while caress is active
            if (fingerBreastActive)
            {
                _nipBoneDiagTimer -= Time.deltaTime;
                if (_nipBoneDiagTimer <= 0f)
                {
                    _nipBoneDiagTimer = 3f;
                    LogNipBones("DIAG");
                }
            }
        }

        private void LogNipBones(string tag)
        {
            void Log(string name, Transform t) {
                if (t == null) return;
                Plugin.Logger.LogInfo(
                    $"[KK_AibuVR] {tag} {name}: lpos={t.localPosition:F4}" +
                    $" lrot={t.localEulerAngles:F2} lscale={t.localScale:F4}");
            }
            Log("bnip02R", _bnip02R);
            Log("bnip02L", _bnip02L);
            // World-space axes and parent (bnip02root) position to understand ShapeBodyInfoFemale's bone target
            if (_bnip02R != null)
            {
                Plugin.Logger.LogInfo(
                    $"[KK_AibuVR] {tag} bnip02R-world: right={_bnip02R.right:F3}" +
                    $" up={_bnip02R.up:F3} fwd={_bnip02R.forward:F3}");
                var root = _bnip02R.parent;
                if (root != null)
                    Plugin.Logger.LogInfo(
                        $"[KK_AibuVR] {tag} bnip02rootR: lpos={root.localPosition:F4} lscale={root.localScale:F4}");
            }
        }

        private bool IsOrgasmPlaying()
        {
            if (F_nowAnimStateName == null || _flags == null) return false;
            string nowAnim = (string)F_nowAnimStateName.GetValue(_flags);
            return !string.IsNullOrEmpty(nowAnim) && nowAnim.Contains("Orgasm");
        }

        private void ExitTongueMode()
        {
            if (_activeLayer >= 0 && _animBody != null)
                _animBody.SetLayerWeight(_activeLayer, _savedLayerWeight);
            _activeLayer = -1;
            if (_animMale != null && _maleActiveLayer >= 0)
            {
                _animMale.SetLayerWeight(_maleActiveLayer, _savedMaleWeight);
                _maleActiveLayer = -1;
            }
            if (_activeZone != "" && _animBody != null)
            {
                // Don't interrupt orgasm animation — HAibu handles its own cleanup
                if (!IsOrgasmPlaying())
                    _animBody.CrossFadeInFixedTime("Idle", 0.8f, 0);
                _appliedTouchState = "";
            }
            _activeZone = "";
            _zoneExitCountdown = 0f;
            _entryBlockTimer   = 0f;
            _pendingZone       = "";
            _zoneChangePendingTime = 0f;
            SetTongueVisible(false);
        }

        internal static void Init(VRHScene scene)
        {
            var old = scene.gameObject.GetComponent<TongueCaressController>();
            if (old != null) Destroy(old);
            Instance = null!;
            var ctrl = scene.gameObject.AddComponent<TongueCaressController>();
            ctrl.Initialize(scene);
            Instance = ctrl;
        }

        private void Initialize(VRHScene scene)
        {
            _flags = (HFlag)F_flags.GetValue(scene);

            var chaControls = FindObjectsOfType<ChaControl>();
            var female = chaControls.FirstOrDefault(c => c.chaFile.parameter.sex == 1);
            if (female == null)
            {
                Plugin.Logger.LogWarning("[KK_AibuVR] TongueCaress: female not found");
                return;
            }

            _femaleCha = female;
            FemaleCha  = female;
            _animBody  = female.animBody;

            var allSmrs = female.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            _bodySmr = allSmrs.FirstOrDefault(s => s.name == "o_body_a");
            _mnpaSmr = allSmrs.FirstOrDefault(s => s.name == "o_mnpa");
            _mnpbSmr = allSmrs.FirstOrDefault(s => s.name == "o_mnpb");
            _nipRenderers = new Renderer[0];
            _origMnpaEnabled = _mnpaSmr != null && _mnpaSmr.enabled;
            _origMnpbEnabled = _mnpbSmr != null && _mnpbSmr.enabled;
            Plugin.Logger.LogInfo(
                $"[KK_AibuVR] SMR cache: body={_bodySmr != null} mnpa={_mnpaSmr != null} mnpb={_mnpbSmr != null}" +
                $" | mnpa.enabled={_origMnpaEnabled} mnpb.enabled={_origMnpbEnabled}");

            // Log all female renderer names that contain "nip" (includes disabled renderers)
            var allRenderers = female.GetComponentsInChildren<Renderer>(true);
            Plugin.Logger.LogInfo($"[KK_AibuVR] TotalRenderers: {allRenderers.Length}");
            foreach (var r in allRenderers)
            {
                if (r.name.IndexOf("nip", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    r.name.IndexOf("mnip", StringComparison.OrdinalIgnoreCase) >= 0)
                    Plugin.Logger.LogInfo(
                        $"[KK_AibuVR] NipRenderer: {r.name} enabled={r.enabled} type={r.GetType().Name}");
            }

            // Log blend shape counts for all SMRs to find which ones have shapes
            foreach (var smr in female.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                int cnt = smr.sharedMesh != null ? smr.sharedMesh.blendShapeCount : 0;
                if (cnt > 0)
                    Plugin.Logger.LogInfo($"[KK_AibuVR] SMR_BS: {smr.name} shapes={cnt}");
            }

            var allT = female.GetComponentsInChildren<Transform>(true);

            // Cache nipple bones for diagnostic logging and future direct manipulation
            _bnip02R = allT.FirstOrDefault(t => t.name == "cf_j_bnip02_R");
            _bnip02L = allT.FirstOrDefault(t => t.name == "cf_j_bnip02_L");
            _dnip01R = allT.FirstOrDefault(t => t.name == "cf_d_bnip01_R");
            _dnip01L = allT.FirstOrDefault(t => t.name == "cf_d_bnip01_L");
            _knipR00 = allT.FirstOrDefault(t => t.name == "k_f_munenipR_00");
            _knipL00 = allT.FirstOrDefault(t => t.name == "k_f_munenipL_00");
            Plugin.Logger.LogInfo(
                $"[KK_AibuVR] NipBones: bnip02R={_bnip02R != null} bnip02L={_bnip02L != null}" +
                $" dnip01R={_dnip01R != null} dnip01L={_dnip01L != null}" +
                $" knipR00={_knipR00 != null} knipL00={_knipL00 != null}");

            // 乳首管理に必要な初期化はここまで。舌カレスが無効なら早期リターン。
            _vrHands = (VRHandCtrl[])F_vrHands.GetValue(scene);
            DiscoverFeelMembers();
            if (!TongueCaressEnabled)
            {
                Plugin.Logger.LogInfo("[KK_AibuVR] Tongue caress disabled — nipple management only.");
                return;
            }

            // Use cf_j_bust01_L/R (breast root joint, close to chest wall) for zone detection.
            // cf_hit_bust02_L/R moves with M_Touch animation → causes false zone exits.
            _bustL   = allT.FirstOrDefault(t => t.name == "cf_j_bust01_L")
                    ?? allT.FirstOrDefault(t => t.name == "cf_hit_bust02_L");
            _bustR   = allT.FirstOrDefault(t => t.name == "cf_j_bust01_R")
                    ?? allT.FirstOrDefault(t => t.name == "cf_hit_bust02_R");
            _kokan   = allT.FirstOrDefault(t => t.name == "cf_hit_kokan")
                    ?? allT.FirstOrDefault(t => t.name == "cf_j_kokan");
            _siriL   = allT.FirstOrDefault(t => t.name == "cf_hit_siri_L");
            _siriR   = allT.FirstOrDefault(t => t.name == "cf_hit_siri_R");

            // Find siri layer at runtime
            _layerSiri = FindLayer("Sita_siri_sawari_L");
            if (_layerSiri < 0) _layerSiri = FindLayer("Sita_siri_sawari_R");

            int layerSiriR = FindLayer("Sita_siri_sawari_R");

            // Scan base layer (0) for candidate states (including K_Touch for nipple pinch)
            var siriCandidates = new[] { "S_Touch", "SA_Touch", "AS_Touch", "Siri_Touch", "B_Touch",
                                         "A_Touch", "M_Touch", "K_Touch", "K_Loop",
                                         "Idle", "M_Idle", "A_Idle" };
            foreach (var cand in siriCandidates)
            {
                bool has = _animBody.HasState(0, Animator.StringToHash(cand));
                Plugin.Logger.LogInfo($"[KK_AibuVR] HasState(0, \"{cand}\") = {has}");
                if (cand == "K_Touch") _hasKTouch = has;
            }
            Plugin.Logger.LogInfo(
                $"[KK_AibuVR] TongueCaress: bustL={(_bustL != null ? _bustL.name : "null")}" +
                $" bustR={(_bustR != null ? _bustR.name : "null")}" +
                $" kokan={_kokan != null} siriL={_siriL != null} siriR={_siriR != null}");
            Plugin.Logger.LogInfo(
                $"[KK_AibuVR] TongueCaress layers: muneL={LayerMuneL} muneR={LayerMuneR}" +
                $" kokan={LayerKokan} siriL={_layerSiri} siriR={layerSiriR}" +
                $" defaultWeights: muneL={_animBody.GetLayerWeight(LayerMuneL):F2}" +
                $" muneR={_animBody.GetLayerWeight(LayerMuneR):F2}");

            var vrCam = Resources.FindObjectsOfTypeAll<SteamVR_Camera>().FirstOrDefault();
            if (vrCam == null)
            {
                Plugin.Logger.LogWarning("[KK_AibuVR] TongueCaress: SteamVR_Camera not found");
                return;
            }
            _myFace = new GameObject("KK_AibuVR_Face");
            _myFace.transform.SetParent(vrCam.transform, false);
            _myFace.transform.localPosition = new Vector3(0f, -0.12f, 0f);

            // p_tang2 is the male character's existing tongue prop with bones + skinned mesh.
            // Instantiate a copy at HMD position (_myFace) so the bones can be driven
            // toward the target zone. The original p_tang2 stays untouched (~40cm from HMD in VR).
            var male = chaControls.FirstOrDefault(c => c.chaFile.parameter.sex == 0);
            if (male != null)
            {
                _animMale = male.animBody;
                if (_animMale != null)
                {
                    _maleLayerMuneL = FindLayer(_animMale, "Sita_mune_sawari_L");
                    _maleLayerMuneR = FindLayer(_animMale, "Sita_mune_sawari_R");
                    _maleLayerKokan = FindLayer(_animMale, "Sita_kokan_sawari_F");
                    _maleLayerSiriL = FindLayer(_animMale, "Sita_siri_sawari_L");
                    _maleLayerSiriR = FindLayer(_animMale, "Sita_siri_sawari_R");
                    Plugin.Logger.LogInfo(
                        $"[KK_AibuVR] Male Sita layers: muneL={_maleLayerMuneL} muneR={_maleLayerMuneR}" +
                        $" kokan={_maleLayerKokan} siriL={_maleLayerSiriL} siriR={_maleLayerSiriR}");
                }

                var p2template = male.GetComponentsInChildren<Transform>(true)
                    .FirstOrDefault(t => t.name == "p_tang2");
                if (p2template != null)
                {
                    // Store references to the ORIGINAL male bones (before instantiating copy).
                    // LateUpdate copies their localRotations each frame so our copy tracks
                    // whatever animation the male animator is currently playing.
                    var origRoot = p2template.GetComponentsInChildren<Transform>(true)
                        .FirstOrDefault(t => t.name == "cf_j_tang_01");
                    var origChain = new List<Transform>();
                    var origCur = origRoot;
                    while (origCur != null && origChain.Count < 5)
                    {
                        origChain.Add(origCur);
                        int next = origChain.Count + 1;
                        origCur = Enumerable.Range(0, origCur.childCount)
                            .Select(i => origCur.GetChild(i))
                            .FirstOrDefault(c => c.name == $"cf_j_tang_0{next}");
                    }
                    _origMaleTangBones = origChain.ToArray();
                    Plugin.Logger.LogInfo($"[KK_AibuVR] p_tang2 orig chain: {_origMaleTangBones.Length} bones");

                    var p2copy = Instantiate(p2template.gameObject);
                    p2copy.name = "KK_AibuVR_pTang2";
                    p2copy.transform.SetParent(_myFace.transform, false);
                    p2copy.transform.localPosition = new Vector3(0f, 0f, 0.08f);
                    p2copy.transform.localRotation = Quaternion.identity;

                    // Original p_tang2 may be disabled (hidden in VR first-person).
                    // Activate the whole copy so renderers can be toggled via Renderer.enabled.
                    // Also reset to layer 0: p_tang2 uses a character layer that the VR first-person
                    // camera culls (shadow still casts from all layers, so shadow appeared but mesh didn't).
                    foreach (var t in p2copy.GetComponentsInChildren<Transform>(true))
                    {
                        t.gameObject.SetActive(true);
                        t.gameObject.layer = 0;
                    }

                    var p2Root = p2copy.GetComponentsInChildren<Transform>(true)
                        .FirstOrDefault(t => t.name == "cf_j_tang_01");
                    var chain = new List<Transform>();
                    var cur = p2Root;
                    while (cur != null && chain.Count < 5)
                    {
                        chain.Add(cur);
                        int next = chain.Count + 1;
                        cur = Enumerable.Range(0, cur.childCount)
                            .Select(i => cur.GetChild(i))
                            .FirstOrDefault(c => c.name == $"cf_j_tang_0{next}");
                    }
                    _maleTangBones = chain.ToArray();
                    _maleTangRoot  = chain.Count > 0 ? chain[0] : null;

                    // Reset bone local rotations: the copy inherits whatever pose the male
                    // animator was in (e.g. anal caress curl). Start from a neutral straight pose.
                    foreach (var b in _maleTangBones)
                        b.localRotation = Quaternion.identity;
                    Plugin.Logger.LogInfo($"[KK_AibuVR] p_tang2 bone reset: {_maleTangBones.Length} bones to identity");

                    var toggleList2 = new List<Renderer>();
                    foreach (var r in p2copy.GetComponentsInChildren<Renderer>(true))
                    {
                        bool isSil = r.name.IndexOf("silhouette", StringComparison.OrdinalIgnoreCase) >= 0;
                        r.enabled = false;
                        if (!isSil) toggleList2.Add(r);
                    }
                    _p_tang2_renderers = toggleList2.ToArray();

                    var smrs   = p2copy.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                    Plugin.Logger.LogInfo(
                        $"[KK_AibuVR] p_tang2 copy: chain={_maleTangBones.Length}" +
                        $" rend={_p_tang2_renderers.Length}" +
                        $" skinnedMesh={smrs.Length}" +
                        $" bones={smrs.FirstOrDefault()?.bones?.Length ?? 0}");

                }
                else
                {
                    Plugin.Logger.LogWarning("[KK_AibuVR] p_tang2 not found");
                }
            }
            else
            {
                Plugin.Logger.LogWarning("[KK_AibuVR] Male ChaControl not found");
            }

            // Load p_tang tongue prop from the same bundle as VR hand items
            try
            {
                var prefab = CommonLib.LoadAsset<GameObject>("h/common/00_00.unity3d", "p_tang", false);
                if (prefab != null)
                {
                    _pTang = Instantiate(prefab);
                    _pTang.name = "KK_AibuVR_pTang";
                    _pTang.transform.SetParent(_myFace.transform, false);
                    // Push forward (Z+) from mouth so the tongue is visible in front of the HMD.
                    // localPosition is in VR camera space: Z+ = looking direction.
                    _pTang.transform.localPosition = new Vector3(0f, 0f, 0.08f);

                    // Cache main renderers only; permanently disable silhouette renderer
                    // (Custom/Silhouette shader causes solid-color overlay on the prop).
                    // Keep root active — toggling SetActive causes shadow-only re-display
                    // on second approach. We toggle Renderer.enabled instead.
                    var toggleList = new System.Collections.Generic.List<Renderer>();
                    foreach (var r in _pTang.GetComponentsInChildren<Renderer>(true))
                    {
                        bool isSilhouette = r.name.IndexOf("silhouette",
                            System.StringComparison.OrdinalIgnoreCase) >= 0;
                        r.enabled = false;
                        if (isSilhouette)
                            Plugin.Logger.LogInfo($"[KK_AibuVR] p_tang: silhouette renderer disabled ({r.name})");
                        else
                            toggleList.Add(r);
                    }
                    _pTangRenderers = toggleList.ToArray();

                    // Find root bone for LookRotation (same chain as p_tang2)
                    _pTangRoot = _pTang.GetComponentsInChildren<Transform>(true)
                        .FirstOrDefault(t => t.name == "cf_j_tang_01");
                    Plugin.Logger.LogInfo(
                        $"[KK_AibuVR] TongueCaress: p_tang loaded, {_pTangRenderers.Length} main renderer(s)" +
                        $" boneRoot={(_pTangRoot != null ? _pTangRoot.name : "null")}");
                }
                else
                {
                    Plugin.Logger.LogWarning("[KK_AibuVR] TongueCaress: p_tang prefab is null");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[KK_AibuVR] TongueCaress: p_tang load failed: {ex.Message}");
            }

            DiscoverVoiceMembers(scene);
            DiagnoseNipRenderers(female);
            Plugin.Logger.LogInfo("[KK_AibuVR] TongueCaress initialized");
        }

        private static void LogAllBlendShapes(string label, SkinnedMeshRenderer smr)
        {
            if (smr == null) { Plugin.Logger.LogInfo($"[KK_AibuVR] BS[{label}]: null"); return; }
            var mesh = smr.sharedMesh;
            int count = mesh != null ? mesh.blendShapeCount : 0;
            Plugin.Logger.LogInfo($"[KK_AibuVR] BS[{label}]: {count} shapes");
            for (int i = 0; i < count; i++)
                Plugin.Logger.LogInfo($"[KK_AibuVR]   {i}:{mesh.GetBlendShapeName(i)} w={smr.GetBlendShapeWeight(i):F1}");
        }

        private static Dictionary<string, float> SnapshotBlendShapes(params SkinnedMeshRenderer[] smrs)
        {
            var d = new Dictionary<string, float>();
            foreach (var smr in smrs)
            {
                if (smr?.sharedMesh == null) continue;
                for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++)
                    d[$"{smr.name}/{i}"] = smr.GetBlendShapeWeight(i);
            }
            return d;
        }

        private static void LogBlendShapeChanges(
            Dictionary<string, float> before, params SkinnedMeshRenderer[] smrs)
        {
            foreach (var smr in smrs)
            {
                if (smr?.sharedMesh == null) continue;
                for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++)
                {
                    string key = $"{smr.name}/{i}";
                    float after = smr.GetBlendShapeWeight(i);
                    if (!before.TryGetValue(key, out float bef) || Math.Abs(after - bef) > 0.01f)
                        Plugin.Logger.LogInfo(
                            $"[KK_AibuVR] BSChange [{smr.name}] {i}:{smr.sharedMesh.GetBlendShapeName(i)}" +
                            $" {bef:F1}→{after:F1}");
                }
            }
        }

        private static void DiagnoseNipRenderers(ChaControl female)
        {
            // Log all blend shapes containing "nip" across all SMRs
            foreach (var smr in female.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = smr.sharedMesh;
                if (mesh == null) continue;
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    string bsn = mesh.GetBlendShapeName(i);
                    if (bsn.IndexOf("nip", StringComparison.OrdinalIgnoreCase) >= 0)
                        Plugin.Logger.LogInfo(
                            $"[KK_AibuVR] NipBS [{smr.name}] {i}:{bsn} w={smr.GetBlendShapeWeight(i):F1}");
                }
            }

            // Probe material properties on key renderers (o_mnpa, o_mnpb, o_body_a)
            var targets = new[] { "o_mnpa", "o_mnpb", "o_body_a" };
            var probeProps = new[] {
                "_Color", "_alpha", "_alpha_a", "_alpha_b", "_AlphaA", "_AlphaB",
                "_overCol", "_overCol_Alpha", "_overBodyCol", "_overBodyCol_Alpha",
                "_nipAlpha", "_NipAlpha", "_nipTex", "_OverTex",
                "_overBodyMask", "_DetailMask", "_ColorMask",
                "_shadowColor", "_EmissionColor", "_rimColor"
            };
            foreach (var smr in female.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                bool isTarget = false;
                foreach (var t in targets) if (smr.name == t) { isTarget = true; break; }
                if (!isTarget) continue;

                var mat = smr.sharedMaterial;
                if (mat == null) continue;
                Plugin.Logger.LogInfo(
                    $"[KK_AibuVR] MatProbe [{smr.name}] shader={mat.shader.name}" +
                    $" color={mat.color} renderQ={mat.renderQueue}");
                foreach (var p in probeProps)
                {
                    if (!mat.HasProperty(p)) continue;
                    try   { Plugin.Logger.LogInfo($"[KK_AibuVR]   float {p}={mat.GetFloat(p):F4}"); }
                    catch { }
                    try   { var c = mat.GetColor(p); Plugin.Logger.LogInfo($"[KK_AibuVR]   color {p}={c}"); }
                    catch { }
                }
            }
        }

        private static void DiscoverFeelMembers()
        {
            if (_feelDiscovered) return;
            _feelDiscovered = true;

            const BindingFlags BF =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            const BindingFlags BF_ALL =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

            _fRateDragGauge  = typeof(HFlag).GetField("rateDragGauge", BF);
            _miFemaleGaugeUp = AccessTools.Method(typeof(HFlag), "FemaleGaugeUp",
                new[] { typeof(float), typeof(bool), typeof(bool) });

            // Enumerate ALL nip-related methods and fields on ChaControl and fileStatus
            foreach (var m in typeof(ChaControl).GetMethods(BF_ALL))
                if (m.Name.IndexOf("nip", StringComparison.OrdinalIgnoreCase) >= 0)
                    Plugin.Logger.LogInfo(
                        $"[KK_AibuVR] ChaControl M: {m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name).ToArray())})");
            foreach (var f in typeof(ChaControl).GetFields(BF_ALL))
                if (f.Name.IndexOf("nip", StringComparison.OrdinalIgnoreCase) >= 0)
                    Plugin.Logger.LogInfo($"[KK_AibuVR] ChaControl F: {f.FieldType.Name} {f.Name}");

            // fileStatus is a property on ChaControl (returns ChaFileStatus)
            var fsPropInfo = AccessTools.Property(typeof(ChaControl), "fileStatus");
            _piFileStatus = fsPropInfo;
            Type fsType = fsPropInfo?.PropertyType;
            if (fsType != null)
            {
                // nipStandRate controls the nipple erection stored in fileStatus
                _piNipStandRate = AccessTools.Property(fsType, "nipStandRate");
                if (_piNipStandRate == null)
                    _fiNipStandRate = fsType.GetField("<nipStandRate>k__BackingField", BF_ALL);

                // disableBustShapeMask: bool[,] — direct access so ClearNipStandMask can clear
                // index 8 (cf_ShapeMaskNipStand) in UpdateShapeBody_Patch.Prefix without side effects.
                _piDisableBustShapeMask = AccessTools.Property(fsType, "disableBustShapeMask");

                Plugin.Logger.LogInfo(
                    $"[KK_AibuVR] fileStatus OK type={fsType.Name}" +
                    $" nipStandRate: prop={_piNipStandRate != null} field={_fiNipStandRate != null}" +
                    $" disableBustShapeMask: {(_piDisableBustShapeMask != null ? "OK" : "null")}");
            }
            else
            {
                Plugin.Logger.LogWarning("[KK_AibuVR] fileStatus not found");
            }

            // ChangeNipRate(float) — blend shape (erection level)
            MI_ChangeNipState = typeof(ChaControl).GetMethods(BF_ALL)
                .FirstOrDefault(m => m.Name == "ChangeNipRate"
                                  && m.GetParameters().Length == 1
                                  && m.GetParameters()[0].ParameterType == typeof(float));
            if (MI_ChangeNipState == null)
                MI_ChangeNipState = typeof(ChaControl).GetMethods(BF_ALL)
                    .FirstOrDefault(m => m.Name.IndexOf("nip", StringComparison.OrdinalIgnoreCase) >= 0
                                      && m.GetParameters().Length >= 1);

            // ChangeSettingNip() — applies fileStatus settings to renderer
            MI_ChangeSettingNip = typeof(ChaControl).GetMethod("ChangeSettingNip",
                BF_ALL, null, Type.EmptyTypes, null);

            // DisableShapeNip(int LR, bool disable) — controls nipple shape masks 6/7
            MI_DisableShapeNip = typeof(ChaControl).GetMethod("DisableShapeNip",
                BF_ALL, null, new[] { typeof(int), typeof(bool) }, null);

            // DisableShapeBodyID(int LR, int id, bool disable)
            // id=8 (cf_ShapeMaskNipStand) → controls the NipStand blend shape.
            // Called with disable=true by HandCtrl.EnableShape when a hand item touches the breast.
            MI_DisableShapeBodyID = typeof(ChaControl).GetMethod("DisableShapeBodyID",
                BF_ALL, null, new[] { typeof(int), typeof(int), typeof(bool) }, null);

            // SetShapeBodyValue(int index, float value) — directly sets body shape value.
            // index=13 (NipStand) with value=1f forces maximum nipple stand (bypasses mask system).
            MI_SetShapeBodyValue = typeof(ChaControl).GetMethod("SetShapeBodyValue",
                BF_ALL, null, new[] { typeof(int), typeof(float) }, null);

            Plugin.Logger.LogInfo(
                $"[KK_AibuVR] feel setup: rateDragGauge={(_fRateDragGauge != null ? "OK" : "null")}" +
                $" FemaleGaugeUp={(_miFemaleGaugeUp != null ? "OK" : "null")}" +
                $" NipMethod={MI_ChangeNipState?.Name ?? "null"}" +
                $" nipStandRate={(_piNipStandRate != null || _fiNipStandRate != null ? "OK" : "null")}" +
                $" ChangeSettingNip={( MI_ChangeSettingNip != null ? "OK" : "null")}" +
                $" DisableShapeNip={( MI_DisableShapeNip != null ? "OK" : "null")}" +
                $" DisableShapeBodyID={( MI_DisableShapeBodyID != null ? "OK" : "null")}" +
                $" SetShapeBodyValue={( MI_SetShapeBodyValue != null ? "OK" : "NULL")}");
        }

        private void DiscoverVoiceMembers(VRHScene scene)
        {
            const BindingFlags BF =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            // HVoiceCtrl メソッド診断（参考ログ、実運用では不要）
            var fVoice = typeof(VRHScene).GetField("voice", BF);
            var voiceCtrl = fVoice?.GetValue(scene);
            Plugin.Logger.LogInfo($"[KK_AibuVR] voice ctrl={( voiceCtrl != null ? "OK" : "null")}");

            // HFlag "now/isXxx/kindTouch" 状態フィールド
            foreach (var f in typeof(HFlag).GetFields(BF))
            {
                string n = f.Name.ToLower();
                if (n.StartsWith("now") || n.StartsWith("is") && (n.Contains("touch") || n.Contains("aibu")) ||
                    n.Contains("kindtouch") || n.Contains("touchparts") || n.Contains("aibukind"))
                    Plugin.Logger.LogInfo($"[KK_AibuVR] HFlag state: {f.FieldType.Name} {f.Name}");
            }

            // Sita_ ハッシュ確認
            Plugin.Logger.LogInfo(
                $"[KK_AibuVR] Sita hash check:" +
                $" muneL={Animator.StringToHash("Sita_mune_sawari_L")}" +
                $" muneR={Animator.StringToHash("Sita_mune_sawari_R")}" +
                $" kokan={Animator.StringToHash("Sita_kokan_sawari_F")}" +
                $" siriL={Animator.StringToHash("Sita_siri_sawari_L")}");
        }

        // Scan all layers for the given state name; return layer index or -1
        private int FindLayer(string stateName)
        {
            if (_animBody == null) return -1;
            int hash = Animator.StringToHash(stateName);
            for (int l = 0; l < _animBody.layerCount; l++)
                if (_animBody.HasState(l, hash)) return l;
            return -1;
        }

        private static int FindLayer(Animator anim, string stateName)
        {
            if (anim == null) return -1;
            int hash = Animator.StringToHash(stateName);
            for (int l = 0; l < anim.layerCount; l++)
                if (anim.HasState(l, hash)) return l;
            return -1;
        }

        private int GetMaleLayerForZone(string zone)
        {
            switch (zone)
            {
                case "muneL": return _maleLayerMuneL;
                case "muneR": return _maleLayerMuneR;
                case "kokan": return _maleLayerKokan;
                case "siriL": return _maleLayerSiriL;
                case "siriR": return _maleLayerSiriR;
                default:      return -1;
            }
        }

        private void Update()
        {
            if (!TongueCaressEnabled) return;
            if (_myFace == null || _flags == null || _animBody == null) return;
            if (_flags.mode != HFlag.EMode.aibu) return;
            if (!TongueModeActive) return;

            if (_entryBlockTimer > 0f)    _entryBlockTimer    -= Time.deltaTime;
            if (_postOrgasmCooldown > 0f) _postOrgasmCooldown -= Time.deltaTime;

            var   facePos = _myFace.transform.position;
            float enterD  = 0.28f;
            float exitD   = enterD + 0.12f;
            float checkD  = IsActive ? exitD : enterD;

            string zone = FindNearestZone(facePos, checkD);

            // zone exit debounce: prevents transient exit detection when bones shift
            if (zone == "" && _activeZone != "")
            {
                _zoneExitCountdown += Time.deltaTime;
                if (_zoneExitCountdown < ZoneExitDelay)
                    zone = _activeZone;
            }
            else
            {
                _zoneExitCountdown = 0f;
            }

            // block re-entry for EntryBlockAfterExit seconds after zone exit
            // prevents the EXIT→ENTER cycle that causes repeated ビクッ/CrossFade
            if (zone != "" && _activeZone == "" && _entryBlockTimer > 0f)
                zone = "";

            // zone change debounce: prevent rapid oscillation between adjacent zones (e.g. siriL↔kokan)
            // Only fires when switching from one active zone to a different one.
            if (zone != "" && zone != _activeZone && _activeZone != "")
            {
                if (zone != _pendingZone)
                {
                    _pendingZone = zone;
                    _zoneChangePendingTime = 0f;
                }
                _zoneChangePendingTime += Time.deltaTime;
                if (_zoneChangePendingTime < ZoneChangeDelay)
                    zone = _activeZone;  // hold current zone until change stabilizes
            }
            else
            {
                _pendingZone = "";
                _zoneChangePendingTime = 0f;
            }

            // HAibu sometimes resets layer weights; keep our Sita_ layer at full weight
            if (_activeLayer >= 0)
            {
                float w = _animBody.GetLayerWeight(_activeLayer);
                if (w < 0.9f)
                    _animBody.SetLayerWeight(_activeLayer, 1f);
            }

            // 1-second periodic status log while zone is active
            if (_activeZone != "")
            {
                _diagLogTimer -= Time.deltaTime;
                if (_diagLogTimer <= 0f)
                {
                    _diagLogTimer = 1.0f;
                    string nowAnim = F_nowAnimStateName != null && _flags != null
                        ? (string)F_nowAnimStateName.GetValue(_flags) : "?";
                    int bh = _animBody.GetCurrentAnimatorStateInfo(0).shortNameHash;
                    Plugin.Logger.LogInfo(
                        $"[KK_AibuVR] STATUS zone={_activeZone}" +
                        $" applied={_appliedTouchState}" +
                        $" nowAnim={nowAnim} baseHash={bh}");
                }
            }

            // ゾーンアクティブ中は毎フレームゲージ更新 + ポーズ維持 + 定期ボイストリガー
            if (_activeZone != "")
            {
                UpdateFeelGauge();
                ApplyTouchPose();
                _voiceTriggerTimer -= UnityEngine.Time.deltaTime;
                if (_voiceTriggerTimer <= 0f)
                {
                    TryTriggerVoice();
                    _voiceTriggerTimer = UnityEngine.Random.Range(2.5f, 5.0f);
                }
            }

            if (zone == _activeZone) return;    // no change

            // Deactivate old layer (zone change or exit)
            if (_activeLayer >= 0)
            {
                _animBody.SetLayerWeight(_activeLayer, _savedLayerWeight);
                _activeLayer = -1;
            }

            if (zone != "")
            {
                bool wasActive = _activeZone != "";
                _activeZone = zone;
                _lastApplyPath = "";  // reset so next ApplyTouchPose logs its path
                _diagLogTimer  = 0f;  // log immediately on entry

                int layer = GetLayerForZone(zone);
                if (layer >= 0)
                {
                    _savedLayerWeight = _animBody.GetLayerWeight(layer);
                    _animBody.SetLayerWeight(layer, 1f);
                    string state = GetSitaState(zone);
                    if (!string.IsNullOrEmpty(state))
                        _animBody.CrossFade(state, 0.3f, layer);
                    _activeLayer = layer;

                    Plugin.Logger.LogInfo(
                        $"[KK_AibuVR] TongueCaress {(wasActive ? "ZONE" : "ENTER")} " +
                        $"zone={zone} layer={layer} savedW={_savedLayerWeight:F2} state={state}");
                }
                else
                {
                    Plugin.Logger.LogInfo(
                        $"[KK_AibuVR] TongueCaress {(wasActive ? "ZONE" : "ENTER")} " +
                        $"zone={zone} (no layer)");
                }

                // Activate male's Sita_ layer to drive p_tang2 bone shapes correctly
                if (_animMale != null)
                {
                    int maleLayer = GetMaleLayerForZone(zone);
                    if (maleLayer >= 0)
                    {
                        if (_maleActiveLayer >= 0 && _maleActiveLayer != maleLayer)
                            _animMale.SetLayerWeight(_maleActiveLayer, _savedMaleWeight);
                        _savedMaleWeight = _animMale.GetLayerWeight(maleLayer);
                        _animMale.SetLayerWeight(maleLayer, 1f);
                        string sitaState = GetSitaState(zone);
                        if (!string.IsNullOrEmpty(sitaState))
                            _animMale.CrossFade(sitaState, 0.3f, maleLayer);
                        _maleActiveLayer = maleLayer;
                        Plugin.Logger.LogInfo($"[KK_AibuVR] Male Sita layer={maleLayer} zone={zone}");
                    }
                }

                if (!wasActive)
                {
                    SetTongueVisible(true);
                    _voiceTriggerTimer = 0f;
                    if (_maleTangRoot != null)
                        Plugin.Logger.LogInfo(
                            $"[KK_AibuVR] Tongue ENTER: maleTangRoot={_maleTangRoot.position:F3}" +
                            $" pTang={(_pTang != null ? _pTang.transform.position.ToString("F3") : "null")}");
                }

                // Log nowAnimStateName on siri zone entry so we can identify the correct pose state
                if ((zone == "siriL" || zone == "siriR") && _flags != null && F_nowAnimStateName != null)
                {
                    string cur = (string)F_nowAnimStateName.GetValue(_flags);
                    int bh = _animBody != null ? _animBody.GetCurrentAnimatorStateInfo(0).shortNameHash : 0;
                    Plugin.Logger.LogInfo(
                        $"[KK_AibuVR] DIAG siri ENTER: nowAnimStateName={cur} baseHash={bh}");
                }
            }
            else
            {
                string prev = _activeZone;
                _activeZone = "";
                _entryBlockTimer = EntryBlockAfterExit;
                _pendingZone = "";
                _zoneChangePendingTime = 0f;
                if (_appliedTouchState != "" && _animBody != null)
                {
                    _animBody.CrossFadeInFixedTime("Idle", 0.8f, 0);
                    _appliedTouchState = "";
                }
                if (_animMale != null && _maleActiveLayer >= 0)
                {
                    _animMale.SetLayerWeight(_maleActiveLayer, _savedMaleWeight);
                    _maleActiveLayer = -1;
                }
                SetTongueVisible(false);
                Plugin.Logger.LogInfo($"[KK_AibuVR] TongueCaress EXIT was={prev}");
            }
        }

        private void UpdateFeelGauge()
        {
            if (_flags == null) return;
            if (_miFemaleGaugeUp != null)
            {
                float rate = _fRateDragGauge != null
                    ? (float)_fRateDragGauge.GetValue(_flags)
                    : 5f;
                _miFemaleGaugeUp.Invoke(_flags,
                    new object[] { rate * UnityEngine.Time.deltaTime, false, false });
            }
        }

        // Called each Update and from HAibu_LateProc_Patch (after HAibu runs).
        // Uses shortNameHash to detect if HAibu has overridden our CrossFade so we can
        // re-apply without calling CrossFadeInFixedTime every frame.
        // Note: unlike DragAction (which keeps layer 0 in Idle hash while manipulating bones),
        // our own CrossFadeInFixedTime DOES change the state machine hash reliably.
        internal void ApplyTouchPose()
        {
            if (_activeZone == "" || _animBody == null) return;

            // Never interfere with orgasm or other special sequences.
            // CrossFading during Orgasm corrupts the animator and disables hand caress afterward.
            if (F_nowAnimStateName != null && _flags != null)
            {
                string nowAnim = (string)F_nowAnimStateName.GetValue(_flags);
                if (!string.IsNullOrEmpty(nowAnim) && nowAnim.Contains("Orgasm"))
                {
                    _postOrgasmCooldown = PostOrgasmCooldown;  // block re-apply after orgasm exits
                    string orgPath = "ORGASM_SKIP";
                    if (orgPath != _lastApplyPath)
                    {
                        Plugin.Logger.LogInfo($"[KK_AibuVR] ApplyTouchPose → {orgPath} nowAnim={nowAnim}");
                        _lastApplyPath = orgPath;
                        _appliedTouchState = "";
                    }
                    return;
                }
            }

            // Don't re-apply poses immediately after orgasm — let HAibu finish its cleanup first
            if (_postOrgasmCooldown > 0f)
            {
                string coolPath = "POST_ORGASM_SKIP";
                if (coolPath != _lastApplyPath)
                {
                    Plugin.Logger.LogInfo($"[KK_AibuVR] ApplyTouchPose → {coolPath} t={_postOrgasmCooldown:F1}s");
                    _lastApplyPath = coolPath;
                }
                return;
            }

            string touchState = GetTouchState(_activeZone);

            string path;

            // No base-layer pose change for this zone (e.g. breast zones)
            if (string.IsNullOrEmpty(touchState))
            {
                path = "EMPTY_STATE";
                if (path != _lastApplyPath)
                {
                    Plugin.Logger.LogInfo($"[KK_AibuVR] ApplyTouchPose → {path} zone={_activeZone} applied={_appliedTouchState}");
                    _lastApplyPath = path;
                }
                if (_appliedTouchState != "")
                {
                    _animBody.CrossFadeInFixedTime("Idle", 0.8f, 0);
                    _appliedTouchState = "";
                }
                return;
            }

            int touchHash = Animator.StringToHash(touchState);
            int curHash  = _animBody.GetCurrentAnimatorStateInfo(0).shortNameHash;
            int nextHash = _animBody.IsInTransition(0)
                ? _animBody.GetNextAnimatorStateInfo(0).shortNameHash : 0;
            bool isOrTransitioningTo = curHash == touchHash || nextHash == touchHash;

            if (isOrTransitioningTo)
            {
                path = $"HASH_OK({touchState})";
                if (path != _lastApplyPath)
                {
                    Plugin.Logger.LogInfo($"[KK_AibuVR] ApplyTouchPose → {path} curHash={curHash} nextHash={nextHash}");
                    _lastApplyPath = path;
                }
                _appliedTouchState = touchState;
                return;
            }

            bool reapply = _appliedTouchState == touchState;
            _animBody.CrossFadeInFixedTime(touchState, 0.8f, 0);
            _appliedTouchState = touchState;
            path = reapply ? $"REAPPLY({touchState})" : $"APPLY({touchState})";
            _lastApplyPath = path;
            Plugin.Logger.LogInfo(
                $"[KK_AibuVR] ApplyTouchPose → {path}: zone={_activeZone} curHash={curHash} nextHash={nextHash}");
        }

        private static string GetTouchState(string zone)
        {
            switch (zone)
            {
                // M_Touch displaces the female's body → arm/body reaction zones shift toward
                // the hand, causing VRHandCtrl to detect reac_armL/reac_bodydown instead
                // of muneL/muneR after tongue mode exits → hand caress becomes impossible.
                // Sita_ layers (31/32) already provide the subtle breast reaction (nipple movement).
                case "muneL":
                case "muneR": return "";
                case "kokan":  return "A_Touch";
                // S_Touch confirmed present via HasState(0, "S_Touch") = True
                case "siriL":
                case "siriR":  return "S_Touch";
                default:       return "";
            }
        }

        // DragAction voice table: voiceTable[idObj=0, col]
        // col: mouth=0(-1), muneL/R=1(112), kokan=2(114), anal=3(116), siriL/R=4(118)
        private static int GetVoiceValue(string zone)
        {
            switch (zone)
            {
                case "muneL":
                case "muneR": return 112;
                case "kokan":  return 114;
                case "siriL":
                case "siriR":  return 118;
                default:       return 100;
            }
        }

        private void TryTriggerVoice()
        {
            if (F_playVoices == null || F_flagsVoice == null || _flags == null) return;
            var voiceFlag = F_flagsVoice.GetValue(_flags);
            if (voiceFlag == null) return;
            var playVoices = (int[])F_playVoices.GetValue(voiceFlag);
            if (playVoices == null || playVoices.Length == 0) return;
            if (playVoices[0] != -1) return; // already pending
            int val = GetVoiceValue(_activeZone);
            playVoices[0] = val;
            Plugin.Logger.LogInfo($"[KK_AibuVR] TriggerVoice: zone={_activeZone} playVoices[0]={val}");
        }

        private void LateUpdate()
        {
            // Restore areola shape masks each frame during breast caress.
            // DisableShapeNip(lr, true) sets masks 6/7 (areola). Our DisableShapeNip_Patch blocks
            // new calls, but calling disable=false here also clears any mask that slipped through
            // on the first contact frame (before NipStateActive was set).
            // Note: NipStand mask (index 8) is handled in UpdateShapeBody_Patch.Prefix (ClearNipStandMask)
            // which clears it without triggering the reSetupDynamicBoneBust side effect.
            if (_nipStateActive && (_caressedL || _caressedR) && _femaleCha != null)
            {
                try
                {
                    if (_caressedL) MI_DisableShapeNip?.Invoke(_femaleCha, new object[] { 0, false });
                    if (_caressedR) MI_DisableShapeNip?.Invoke(_femaleCha, new object[] { 1, false });
                }
                catch { }
            }

            // Push caressed nipple forward via cf_j_bnip02 localPosition Z offset.
            // DragAction (via DynamicBone forces) drives the parent bone (cf_d_bnip01) inward,
            // causing the nipple tip to sink into the breast. Setting a positive local Z on bnip02
            // counteracts this regardless of the mask/sibBody state.
            // At NipStandScale=1.0: protrusion=0 → resets any stale offset, no visual change.
            // At NipStandScale=3.0: ~6mm forward → nipple visually natural.
            if (_nipStateActive && (_caressedL || _caressedR))
            {
                float protrusion = (Plugin.NipStandScale.Value - 1f) / 7f * 0.02f;
                var pos = new Vector3(0f, 0f, protrusion);
                if (_caressedR && _bnip02R != null) _bnip02R.localPosition = pos;
                if (_caressedL && _bnip02L != null) _bnip02L.localPosition = pos;
            }

            if (_activeZone == "") return;

            Transform zoneT = GetZoneTransform(_activeZone);
            if (zoneT == null) return;

            // Copy child bone rotations from the original male p_tang2 (after its animator has run).
            // This gives the correct per-zone tongue shape (mune=flat, anal=curled etc.)
            // driven by the actual male animator rather than hardcoded identity rotations.
            // Skip index 0 (root) — its world rotation is overridden below by LookRotation.
            if (_origMaleTangBones.Length == _maleTangBones.Length)
            {
                for (int i = 1; i < _maleTangBones.Length; i++)
                    _maleTangBones[i].localRotation = _origMaleTangBones[i].localRotation;
            }

            // Point root bones toward target zone
            if (_maleTangRoot != null)
            {
                Vector3 toTarget = zoneT.position - _maleTangRoot.position;
                if (toTarget.sqrMagnitude > 0.0001f)
                    _maleTangRoot.rotation = Quaternion.LookRotation(toTarget.normalized);
            }
            if (_pTangRoot != null)
            {
                Vector3 toTarget = zoneT.position - _pTangRoot.position;
                if (toTarget.sqrMagnitude > 0.0001f)
                    _pTangRoot.rotation = Quaternion.LookRotation(toTarget.normalized);
            }
        }

        private Transform GetZoneTransform(string zone)
        {
            switch (zone)
            {
                case "muneL": return _bustL;
                case "muneR": return _bustR;
                case "kokan": return _kokan;
                case "siriL": return _siriL;
                case "siriR": return _siriR;
                default:      return null;
            }
        }

        private void SetTongueVisible(bool visible)
        {
            // p_tang has the correct flat shape for caress zones; p_tang2 bind pose is anal-shaped
            if (_pTangRenderers != null && _pTangRenderers.Length > 0)
            {
                foreach (var r in _pTangRenderers)
                    if (r != null) r.enabled = visible;
                Plugin.Logger.LogInfo($"[KK_AibuVR] SetTongueVisible({visible}): p_tang");
            }
            else if (_p_tang2_renderers.Length > 0)
            {
                // fallback: p_tang2 copy (anal-shaped but better than nothing)
                foreach (var r in _p_tang2_renderers)
                    if (r != null) r.enabled = visible;
                Plugin.Logger.LogInfo($"[KK_AibuVR] SetTongueVisible({visible}): p_tang2 copy (fallback)");
            }
            else
            {
                Plugin.Logger.LogWarning($"[KK_AibuVR] SetTongueVisible({visible}): no renderers available");
            }
        }

        private int GetLayerForZone(string zone)
        {
            switch (zone)
            {
                case "muneL": return LayerMuneL;
                case "muneR": return LayerMuneR;
                case "kokan": return LayerKokan;
                case "siriL":
                case "siriR": return _layerSiri;
                default:      return -1;
            }
        }

        private string GetSitaState(string zone)
        {
            switch (zone)
            {
                case "muneL": return "Sita_mune_sawari_L";
                case "muneR": return "Sita_mune_sawari_R";
                case "kokan": return "Sita_kokan_sawari_F";
                case "siriL": return "Sita_siri_sawari_L";
                case "siriR": return "Sita_siri_sawari_R";
                default:      return "";
            }
        }

        private string FindNearestZone(Vector3 pos, float maxDist)
        {
            float best = maxDist;
            string result = "";
            TryZone(ref best, ref result, _bustL, "muneL", pos);
            TryZone(ref best, ref result, _bustR, "muneR", pos);
            TryZone(ref best, ref result, _kokan, "kokan", pos);
            TryZone(ref best, ref result, _siriL, "siriL", pos);
            TryZone(ref best, ref result, _siriR, "siriR", pos);
            return result;
        }

        private static void TryZone(ref float best, ref string result, Transform t, string zone, Vector3 pos)
        {
            if (t == null) return;
            float d = Vector3.Distance(pos, t.position);
            if (d < best) { best = d; result = zone; }
        }

        private void OnDestroy()
        {
            if (_activeLayer >= 0 && _animBody != null)
                _animBody.SetLayerWeight(_activeLayer, _savedLayerWeight);
            if (_animMale != null && _maleActiveLayer >= 0)
                _animMale.SetLayerWeight(_maleActiveLayer, _savedMaleWeight);
            if (_appliedTouchState != "" && _animBody != null)
            {
                _animBody.CrossFadeInFixedTime("Idle", 0.8f, 0);
                _appliedTouchState = "";
            }
            ResetHandMode();
            SetTongueVisible(false);
            if (_pTang != null) Destroy(_pTang);
            if (_myFace != null) Destroy(_myFace);
            if (Instance == this) Instance = null!;
        }
    }

    [HarmonyPatch(typeof(VRHScene), "MapSameObjectDisable")]
    internal static class VRHScene_MapSameObjectDisable_Patch
    {
        [HarmonyPostfix]
        private static void Postfix(VRHScene __instance)
            => TongueCaressController.Init(__instance);
    }

    // ChaControl.UpdateShapeBody reads disableBustShapeMask[lr, 8] (cf_ShapeMaskNipStand) to decide whether
    // to write 0.5f (flat) or Lerp(shapeValueBody[13], 1f, nipStandRate) (natural) into sibBody.
    // DisableShapeBust (not blocked) sets changeShapeBodyMask=true → triggers UpdateShapeBody.
    // On the first contact frame NipStateActive may be false, so DisableShapeBodyID(lr,8,true) slips through
    // and the mask is set. The Prefix clears it directly (no side effects) before UpdateShapeBody reads it,
    // ensuring the natural value is always computed while p_fingerL breast caress is active.
    [HarmonyPatch(typeof(ChaControl), "UpdateShapeBody")]
    internal static class ChaControl_UpdateShapeBody_Patch
    {
        [HarmonyPrefix]
        private static void Prefix(ChaControl __instance)
        {
            if (!TongueCaressController.NipStateActive) return;
            if (!ReferenceEquals(__instance, TongueCaressController.FemaleCha)) return;
            TongueCaressController.Instance?.ClearNipStandMask();
        }

        [HarmonyPostfix]
        private static void Postfix(ChaControl __instance)
        {
            if (!TongueCaressController.NipStateActive) return;
            if (!ReferenceEquals(__instance, TongueCaressController.FemaleCha)) return;
            TongueCaressController.Instance?.MaintainNipState();
        }
    }

    // VRHandCtrl.EnableShape calls DisableShapeNip(lr, true) and DisableShapeBodyID(lr, 8, true)
    // when a hand item contacts the breast (via OnCollision→EnableShapeCoroutine or SetAnimation).
    // Both calls flatten the nipple/areola:
    //   DisableShapeNip         → disables blend shape masks 6/7 (nipple+areola mesh shape)
    //   DisableShapeBodyID(,8,) → forces NipStand value to 0.5f (flat) in UpdateShapeBody
    // Block disable=true during p_fingerL breast caress to preserve the neutral raised state.
    // Allow disable=false so the game can restore shapes normally after caress ends.
    // NotifyItemSwitch() resets NipStateActive before item cycling so SetItem's own
    // EnableShape calls are not blocked (prevents mode-switch breakage).
    [HarmonyPatch(typeof(ChaControl), "DisableShapeNip")]
    internal static class ChaControl_DisableShapeNip_Patch
    {
        [HarmonyPrefix]
        private static bool Prefix(ChaControl __instance, object[] __args)
        {
            if (!TongueCaressController.NipStateActive) return true;
            if (!ReferenceEquals(__instance, TongueCaressController.FemaleCha)) return true;
            bool disabling = __args != null && __args.Length >= 2 && (bool)__args[1];
            return !disabling;
        }
    }

    // DisableShapeBodyID(lr, 8, true) forces NipStand to flat (0.5f) via UpdateShapeBody.
    // cf_ShapeMaskNipStand=8; only block this specific index to avoid side effects on others.
    [HarmonyPatch(typeof(ChaControl), "DisableShapeBodyID")]
    internal static class ChaControl_DisableShapeBodyID_Patch
    {
        [HarmonyPrefix]
        private static bool Prefix(ChaControl __instance, object[] __args)
        {
            if (!TongueCaressController.NipStateActive) return true;
            if (!ReferenceEquals(__instance, TongueCaressController.FemaleCha)) return true;
            if (__args == null || __args.Length < 3) return true;
            if ((int)__args[1] != 8) return true;  // only intercept cf_ShapeMaskNipStand=8
            bool disabling = (bool)__args[2];
            return !disabling;
        }
    }
}
