using HarmonyLib;
using System;

namespace SVS_VirginBlood
{
    /// <summary>
    /// Keeps the H scene's virginity in line with the game's own record of the character
    /// (CharaData.charasGameParam), and the other two copies (her card and its copy in the save)
    /// in line with that. The game reads its own record for conversations and H, but never updates
    /// the other copies, so they go stale once she has sex.
    /// </summary>
    /// <remarks>
    /// HActor's constructor would be the natural place, but constructors can't be hooked in native code.
    /// </remarks>
    internal static class VirginFix
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SV.H.HScene), nameof(SV.H.HScene.InitializeParameter))]
        private static void SceneInitialized(SV.H.HScene __instance) => ApplyAll(__instance, "initialize");

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SV.H.HScene), "Start")]
        private static void SceneStarting(SV.H.HScene __instance) => ApplyAll(__instance, "start");

        private static void ApplyAll(SV.H.HScene scene, string context)
        {
            try
            {
                var actors = scene?.Actors;
                if (actors == null) return;
                foreach (var hActor in actors)
                    Apply(hActor, context);
            }
            catch (Exception e)
            {
                VirginBloodPlugin.Log.LogWarning($"Virgin fix failed ({context}): {e}");
            }
        }

        // Returns true if anything was corrected
        internal static bool Apply(SV.H.HActor hActor, string context)
        {
            if (!VirginBloodPlugin.FixVirginity.Value || hActor == null || hActor.IsMan) return false;
            var status = hActor.hStatus;
            // The game's own record, not her card: the card copies are never updated after she has sex
            var game = hActor.Actor?.charasGameParam;
            if (status == null || game == null) return false;

            var fixedVagina = Fix(status, SV.H.HActor.HStatus.VirginPart.Vagina, game.isVirgin);
            var fixedAnal = Fix(status, SV.H.HActor.HStatus.VirginPart.Anal, game.isAnalVirgin);
            if (fixedVagina || fixedAnal)
                VirginBloodPlugin.Log.LogInfo($"Restored virginity for {hActor.Name} ({context}): vagina {fixedVagina}, anal {fixedAnal}");
            return fixedVagina || fixedAnal;
        }

        // The game doesn't write a loss back for a character it set up as not a virgin, so without
        // this she would be restored and lose her virginity again in every H scene
        internal static void SaveLoss(SaveData.Actor actor, string name, bool vagina, bool anal)
        {
            if (actor == null) return;
            // The game's own record, plus her card and its copy in the save so they stay in step
            var game = actor.charasGameParam;
            if (game != null)
            {
                if (vagina) game.isVirgin = false;
                if (anal) game.isAnalVirgin = false;
            }
            foreach (var param in new[] { actor.gameParameter, actor.chaCtrl?.data?.GameParameter })
            {
                if (param == null) continue;
                if (vagina) param.isVirgin = false;
                if (anal) param.isAnalVirgin = false;
            }
            VirginBloodPlugin.Log.LogInfo($"Saved virginity loss for {name}: vagina {vagina}, anal {anal}");
        }

        // Makes a character a virgin again everywhere the game keeps it, and wipes her sex history,
        // as if she never had sex. Only works on characters in the loaded save, not in the maker
        internal static void ResetToVirgin(UnityEngine.Transform root)
        {
            var charas = Manager.Game.Charas;
            if (root == null || charas == null) return;
            SaveData.Actor actor = null;
            var id = -1;
            foreach (var pair in charas)
            {
                if (pair.Value?.chaCtrl != null && pair.Value.chaCtrl.transform == root)
                {
                    actor = pair.Value;
                    id = pair.Key;
                    break;
                }
            }
            if (actor == null)
            {
                VirginBloodPlugin.Log.LogWarning($"{root.name} is not a character in the loaded save, nothing reset");
                return;
            }

            foreach (var param in new[] { actor.gameParameter, actor.chaCtrl?.data?.GameParameter })
            {
                if (param == null) continue;
                param.isVirgin = true;
                param.isAnalVirgin = true;
            }

            var game = actor.charasGameParam;
            var cleared = 0;
            if (game != null)
            {
                game.isVirgin = true;
                game.isAnalVirgin = true;
                game.isHPlay = false;
                var memory = game.memory;
                if (memory?.countParam != null)
                    memory.countParam.hCounter = 0;
                if (memory?.pairTable != null)
                {
                    foreach (var pair in memory.pairTable)
                    {
                        if (!ClearPair(pair.Value?.saveInfo)) continue;
                        cleared++;
                        // The partner keeps their own record of her, which has to match
                        if (charas.ContainsKey(pair.Key))
                        {
                            var partnerPairs = charas[pair.Key]?.charasGameParam?.memory?.pairTable;
                            if (partnerPairs != null && partnerPairs.ContainsKey(id))
                                ClearPair(partnerPairs[id]?.saveInfo);
                        }
                    }
                }
            }
            VirginBloodPlugin.Log.LogInfo($"Reset {actor.chaCtrl?.fileParam?.fullname ?? root.name} to a virgin, " +
                $"cleared H history with {cleared} partner(s). Save the game to keep it");
        }

        // Returns true if there was any sex history to clear
        private static bool ClearPair(SaveData.SavePairInfo info)
        {
            if (info == null) return false;
            var had = info.totalH != 0 || info.insertion != 0 || info.anal != 0 || info.caress != 0 || info.service != 0;
            info.totalH = 0;
            info.insertion = 0;
            info.anal = 0;
            info.caress = 0;
            info.service = 0;
            info.special = 0;
            info.finish = 0;
            info.oneDayH = 0;
            info.yesterdayH = 0;
            info.continueDayH = 0;
            return had;
        }

        private static bool Fix(SV.H.HActor.HStatus status, SV.H.HActor.HStatus.VirginPart part, bool savedVirgin)
        {
            var data = status.GetVirgin(part);
            if (data == null || !savedVirgin || data.IsInitVirgin || data.IsLost) return false;
            data.IsInitVirgin = true;
            data.IsVirgin = true;
            return true;
        }
    }
}
