using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace SVS_VirginBlood
{
    [BepInPlugin(GUID, PluginName, Version)]
    public class VirginBloodPlugin : BasePlugin
    {
        public const string GUID = "com.svs.virginblood";
        public const string PluginName = "SVS_VirginBlood";
        public const string Version = "0.1.0";

        internal static new ManualLogSource Log;

        // Configuration
        internal static ConfigEntry<KeyCode> StainKey;
        internal static ConfigEntry<KeyCode> MenuKey;
        internal static ConfigEntry<bool> AutoFirstTime;
        internal static ConfigEntry<bool> KeepAfterH;
        internal static ConfigEntry<bool> KeepInDialogue;
        internal static ConfigEntry<bool> FixVirginity;
        internal static ConfigEntry<float> Spread;
        internal static ConfigEntry<float> StreakLength;
        internal static ConfigEntry<float> Opacity;
        internal static ConfigEntry<bool> Drips;
        internal static ConfigEntry<bool> Flow;
        internal static ConfigEntry<float> FlowSeconds;
        internal static ConfigEntry<bool> Gravity;
        internal static ConfigEntry<bool> MixWithCum;
        internal static ConfigEntry<float> CumMix;
        internal static ConfigEntry<float> WetShine;

        // Fresh blood red; the shading on the body darkens it further
        private static readonly Color BloodColor = new Color(0.85f, 0.02f, 0.04f);

        // Where the blood is centred, from the top of the slit (0) to the anus (1): about where the opening is
        internal const float Position = 0.4f;

        public override void Load()
        {
            Log = base.Log;

            ClassInjector.RegisterTypeInIl2Cpp<BloodController>();

            StainKey = Config.Bind("General", "Stain Key", KeyCode.F9,
                "Key to place a blood stain on the selected character");
            MenuKey = Config.Bind("General", "Menu Key", KeyCode.F10,
                "Key to open/close the configuration menu");
            AutoFirstTime = Config.Bind("General", "First Time Blood", true,
                "Add blood automatically when a character loses her virginity in an H scene");
            KeepAfterH = Config.Bind("General", "Keep After H", false,
                "Keep first time blood after the H scene ends instead of removing it");
            KeepInDialogue = Config.Bind("General", "Keep In Dialogue", true,
                "Keep first time blood on the character through the dialogue after the H scene, then remove it");
            FixVirginity = Config.Bind("General", "Fix H Virginity", true,
                "Keep the H scene and her card in step with the game's own virginity record, and save the loss after her first time");
            Spread = Config.Bind("Stain", "Spread", 1f,
                new ConfigDescription("Size of the blood around the crotch", new AcceptableValueRange<float>(0.3f, 2.5f)));
            StreakLength = Config.Bind("Stain", "Streak Length", 1f,
                new ConfigDescription("How far the drips run down the thighs", new AcceptableValueRange<float>(0.2f, 1.5f)));
            WetShine = Config.Bind("Stain", "Wet Shine", 0.6f,
                new ConfigDescription("Glossy wet reflection on the blood, like sweat. 0 turns it off", new AcceptableValueRange<float>(0f, 1f)));
            Opacity = Config.Bind("Stain", "Opacity", 0.9f,
                new ConfigDescription("Stain opacity", new AcceptableValueRange<float>(0.05f, 1f)));
            Drips = Config.Bind("Stain", "Drips", true,
                "Add drip streaks running down the inner thighs");
            Flow = Config.Bind("Stain", "Flow", true,
                "Drips run down the thighs over time instead of appearing at once. The stain at the opening appears immediately");
            FlowSeconds = Config.Bind("Stain", "Flow Seconds", 6f,
                new ConfigDescription("How long the drips take to run all the way down", new AcceptableValueRange<float>(1f, 30f)));
            Gravity = Config.Bind("Stain", "Gravity", true,
                "Drips only run down a thigh while its knee is below the hip. Raised or flat legs hold their drips until they point down");

            MixWithCum = Config.Bind("Cum", "Mix With Cum", true,
                "Tint the game's cum that comes out of a girl who has blood, so it looks mixed with the blood");
            CumMix = Config.Bind("Cum", "Mix Strength", 0.35f,
                new ConfigDescription("How red the cum turns when mixed with blood. 0 leaves it white", new AcceptableValueRange<float>(0f, 1f)));

            foreach (var entry in new[] { Spread, StreakLength, WetShine, Opacity, FlowSeconds, CumMix })
                KeepTwoDecimals(entry);

            // Create persistent controller GameObject
            var go = new GameObject("VirginBloodController");
            GameObject.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<BloodController>();

            var harmony = new HarmonyLib.Harmony(GUID);
            harmony.PatchAll(typeof(VirginFix));

            Log.LogInfo($"{PluginName} v{Version} loaded");
        }

        // Dragging a slider in the configuration manager gives values like 1.0234567; keep two decimals
        private static void KeepTwoDecimals(ConfigEntry<float> entry)
        {
            void Round()
            {
                var rounded = Mathf.Round(entry.Value * 100f) / 100f;
                if (rounded != entry.Value)
                    entry.Value = rounded;
            }
            Round();
            entry.SettingChanged += (_, _) => Round();
        }

        internal static Color GetColor()
        {
            return new Color(BloodColor.r, BloodColor.g, BloodColor.b, Opacity.Value);
        }

        // Every setting except the keys, which people set up to fit their own keyboard
        internal static void RestoreDefaults()
        {
            var entries = new ConfigEntryBase[]
            {
                AutoFirstTime, KeepAfterH, KeepInDialogue, FixVirginity, Spread, StreakLength, WetShine, Opacity,
                Drips, Flow, FlowSeconds, Gravity, MixWithCum, CumMix,
            };
            foreach (var entry in entries)
                entry.BoxedValue = entry.DefaultValue;
            Log.LogInfo("Settings restored to defaults");
        }
    }
}
