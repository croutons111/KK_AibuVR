using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace KK_AibuVR
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInProcess("KoikatuVR")]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid    = "com.koikatu.aibu_vr";
        public const string PluginName    = "KK_AibuVR";
        public const string PluginVersion = "1.0.0";

        internal static new ManualLogSource Logger = null!;

        public static ConfigEntry<bool>             HandCaressEnabled = null!;
        public static ConfigEntry<KeyboardShortcut> CycleItemKey    = null!;
        public static ConfigEntry<ViveButton>       CycleButtonVR_L = null!;  // 左コントローラー
        public static ConfigEntry<ViveButton>       CycleButtonVR_R = null!;  // 右コントローラー

        // EViveButtonKind の値に一致させる（VRViveController+EViveButtonKind）
        public enum ViveButton
        {
            None          = -1,
            Grip          = 1,
            Menu          = 2,
            Touchpad      = 11,
            Touchpad_Up   = 3,
            Touchpad_Down = 4,
            Touchpad_Left = 5,
            Touchpad_Right= 6,
        }

        private void Awake()
        {
            Logger = base.Logger;

            HandCaressEnabled = Config.Bind(
                "Hand Caress",
                "Enabled",
                true,
                "ハンド愛撫アイテム切り替え機能の有効/無効\n" +
                "false にするとアイテムサイクルおよび乳首管理が完全に無効になる");

            CycleItemKey = Config.Bind(
                "Controls",
                "Cycle Key (Keyboard)",
                new KeyboardShortcut(UnityEngine.KeyCode.Tab),
                "アイテム切り替えキーボードショートカット");

            CycleButtonVR_L = Config.Bind(
                "Controls",
                "Cycle Button - Left Controller",
                ViveButton.Grip,
                "左コントローラーのアイテム切り替えボタン（左手のみ切り替え）\n" +
                "None=無効 / Grip=グリップ / Menu=メニュー / Touchpad=タッチパッド押し込み\n" +
                "Touchpad_Up/Down/Left/Right=タッチパッド方向押し");

            CycleButtonVR_R = Config.Bind(
                "Controls",
                "Cycle Button - Right Controller",
                ViveButton.Grip,
                "右コントローラーのアイテム切り替えボタン（右手のみ切り替え）\n" +
                "None=無効 / Grip=グリップ / Menu=メニュー / Touchpad=タッチパッド押し込み\n" +
                "Touchpad_Up/Down/Left/Right=タッチパッド方向押し");

            Harmony.CreateAndPatchAll(typeof(Plugin).Assembly, PluginGuid);
            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded.");
        }
    }
}
