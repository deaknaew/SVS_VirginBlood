using Character;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SVS_VirginBlood
{
    public class BloodController : MonoBehaviour
    {
        public BloodController(IntPtr ptr) : base(ptr) { }

        // One blood overlay per character, drawn as an extra material on the body mesh
        private class Stain
        {
            public Transform Root;
            public SkinnedMeshRenderer Body;
            public Material Material;
            public BodyOverlay.BloodTexture Blood;
            public int Seed;
            // Placed because the character lost her virginity, rather than by hand
            public bool Auto;
            // The save data actor she belongs to, so her blood can follow her out of the H scene
            public IntPtr Actor;
            public string Name;
            // How far each leg's drips have run (0-1). They only run while that leg points down
            public bool Flowing;
            public float FlowLeft = 1f;
            public float FlowRight = 1f;
            public float LastFlowUpdate;
            public Transform HipLeft, KneeLeft, HipRight, KneeRight;
        }

        // A leg counts as pointing down when its knee is at least this far below the hip, per unit of thigh length
        private const float LegDownThreshold = 0.35f;

        // How often running drips are redrawn; each redraw rewrites the blood pixels and uploads the texture
        private const float FlowUpdateInterval = 1f / 12f;

        private bool _showMenu = false;
        private readonly List<Stain> _stains = new List<Stain>();
        private class CharaEntry
        {
            public Transform Root;
            public string Label;
            public bool IsFemale;
        }

        private readonly List<CharaEntry> _characters = new List<CharaEntry>();
        private Transform _currentCharacterRoot;
        private Vector2 _charScrollPos;
        private Rect _windowRect = new Rect(20, 20, 420, 480);
        private readonly System.Random _rng = new System.Random();

        // Keeps mouse drags on the menu from moving the game camera
        private bool _dragStartedInWindow;

        // Shape sliders regenerate the texture, which is slow, so wait until the drag ends
        private bool _shapeDirty;

        // The game rebuilds body materials on outfit/body changes, so re-attach the overlay
        private int _watchdogFrames;

        private readonly List<Behaviour> _disabledCameraControls = new List<Behaviour>();

        // First time detection: the H scene's virginity flags are polled, since the game's
        // Lost() call is small enough to be inlined where a hook would never see it
        private bool _wasInH;
        private int _hPollFrames;
        private readonly HashSet<IntPtr> _bledThisScene = new HashSet<IntPtr>();
        private readonly HashSet<IntPtr> _seenActors = new HashSet<IntPtr>();
        private readonly Dictionary<IntPtr, (SaveData.Actor Actor, string Name, bool Vagina, bool Anal)> _pendingLoss =
            new Dictionary<IntPtr, (SaveData.Actor Actor, string Name, bool Vagina, bool Anal)>();
        private readonly Dictionary<IntPtr, bool> _virginAtStart = new Dictionary<IntPtr, bool>();
        private bool _hErrorLogged;

        // Blood from the H scene that carries on into the dialogue after it: save actor -> blood shape.
        // The H scene works on copies of the characters, so the blood has to be placed on the dialogue's own bodies
        private readonly Dictionary<IntPtr, (int Seed, string Name)> _postH = new Dictionary<IntPtr, (int Seed, string Name)>();
        private float _postHSince;
        private bool _postHSawDialogue;
        private float _postHLog;
        private const float PostHMaxSeconds = 90f;

        private void Update()
        {
            UpdateFirstTime();
            UpdatePostH();

            if (Input.GetKeyDown(VirginBloodPlugin.StainKey.Value))
                AddStain();

            if (Input.GetKeyDown(VirginBloodPlugin.MenuKey.Value))
            {
                _showMenu = !_showMenu;
                if (_showMenu)
                    RefreshCharacters();
            }

            if (_shapeDirty && !Input.GetMouseButton(0))
            {
                _shapeDirty = false;
                RerollLast(keepSeed: true);
            }

            UpdateFlow();
            UpdateCumMix();

            if (++_watchdogFrames >= 30)
            {
                _watchdogFrames = 0;
                KeepAttached();
            }

            UpdateInputBlocking();
        }

        [HideFromIl2Cpp]
        private void UpdateInputBlocking()
        {
            var anyButton = Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2);
            var overWindow = _showMenu && IsMouseOverWindow();

            if (!anyButton)
                _dragStartedInWindow = false;
            else if (overWindow && (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2)))
                _dragStartedInWindow = true;

            // Keep blocking while a slider drag that began in the menu leaves the window
            var block = _showMenu && (overWindow || _dragStartedInWindow);
            if (block)
            {
                Input.ResetInputAxes();
                DisableCameraControls();
            }
            else
            {
                RestoreCameraControls();
            }
        }

        [HideFromIl2Cpp]
        private bool IsMouseOverWindow()
        {
            // GUI space has Y pointing down, Input space has Y pointing up
            var mouse = Input.mousePosition;
            return _windowRect.Contains(new Vector2(mouse.x, Screen.height - mouse.y));
        }

        [HideFromIl2Cpp]
        private void DisableCameraControls()
        {
            if (_disabledCameraControls.Count > 0) return;
            foreach (var cam in FindObjectsOfType<BaseCameraControl_Ver2>())
            {
                if (cam != null && cam.enabled)
                {
                    cam.enabled = false;
                    _disabledCameraControls.Add(cam);
                }
            }
        }

        [HideFromIl2Cpp]
        private void RestoreCameraControls()
        {
            if (_disabledCameraControls.Count == 0) return;
            foreach (var cam in _disabledCameraControls)
            {
                if (cam != null)
                    cam.enabled = true;
            }
            _disabledCameraControls.Clear();
        }

        private void OnGUI()
        {
            if (!_showMenu) return;

            _windowRect = GUILayout.Window(
                9374202,
                _windowRect,
                (GUI.WindowFunction)DrawWindow,
                $"{VirginBloodPlugin.PluginName} v{VirginBloodPlugin.Version}",
                GUILayout.MinWidth(420),
                GUILayout.MinHeight(300)
            );
        }

        [HideFromIl2Cpp]
        private void DrawWindow(int windowId)
        {
            GUILayout.BeginVertical();

            // --- Character ---
            GUILayout.Label("<b>Character</b>", GUI.skin.label);
            if (GUILayout.Button("Refresh Characters"))
                RefreshCharacters();

            if (_characters.Count > 0)
            {
                _charScrollPos = GUILayout.BeginScrollView(_charScrollPos, GUILayout.Height(70));
                foreach (var chara in _characters)
                {
                    if (chara.Root == null) continue;
                    var label = chara.Root == _currentCharacterRoot ? $"> {chara.Label}" : chara.Label;
                    if (GUILayout.Button(label, GUILayout.Height(20)))
                        _currentCharacterRoot = chara.Root;
                }
                GUILayout.EndScrollView();
            }
            else
            {
                GUILayout.Label("No characters found. Open the menu during a scene with a character.");
            }
            if (_currentCharacterRoot != null && GUILayout.Button("Reset Selected to Virgin (clears her H history)"))
                VirginFix.ResetToVirgin(_currentCharacterRoot);

            GUILayout.Space(6);

            // --- Stain controls ---
            GUILayout.Label($"<b>Blood</b> ({_stains.Count(s => s.Body != null)} characters)", GUI.skin.label);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button($"Add / Re-roll ({VirginBloodPlugin.StainKey.Value})"))
                AddStain();
            if (GUILayout.Button("Re-roll Last"))
                RerollLast();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Remove Last"))
                RemoveLast();
            if (GUILayout.Button("Clear All"))
                RemoveAllStains();
            GUILayout.EndHorizontal();

            GUILayout.Label("Sliders below adjust the last placed blood.");
            GUILayout.Space(4);

            // --- Look ---
            GUI.changed = false;
            GUILayout.Label("<b>Look</b>", GUI.skin.label);
            VirginBloodPlugin.Opacity.Value = DrawSlider("Opacity", VirginBloodPlugin.Opacity.Value, 0.05f, 1f);
            VirginBloodPlugin.WetShine.Value = DrawSlider("Wet Shine", VirginBloodPlugin.WetShine.Value, 0f, 1f);
            if (GUI.changed)
                StainFactory.ApplyColor(LastStain()?.Material);

            // --- Shape ---
            GUI.changed = false;
            GUILayout.Label("<b>Shape</b>", GUI.skin.label);
            VirginBloodPlugin.Spread.Value = DrawSlider("Spread", VirginBloodPlugin.Spread.Value, 0.3f, 2.5f);
            VirginBloodPlugin.StreakLength.Value = DrawSlider("Streak Length", VirginBloodPlugin.StreakLength.Value, 0.2f, 1.5f);
            if (GUI.changed)
                _shapeDirty = true;

            GUILayout.BeginHorizontal();
            GUILayout.Label("Drips down thighs");
            var drips = GUILayout.Toggle(VirginBloodPlugin.Drips.Value, VirginBloodPlugin.Drips.Value ? "On" : "Off");
            GUILayout.EndHorizontal();
            if (drips != VirginBloodPlugin.Drips.Value)
            {
                VirginBloodPlugin.Drips.Value = drips;
                RerollLast(keepSeed: true);
            }

            GUILayout.Space(6);
            if (GUILayout.Button("Restore Defaults"))
                RestoreDefaults();

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        [HideFromIl2Cpp]
        private float DrawSlider(string label, float value, float min, float max)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{label}: {value:F2}", GUILayout.Width(160));
            value = Mathf.Round(GUILayout.HorizontalSlider(value, min, max) * 100f) / 100f;
            GUILayout.EndHorizontal();
            return value;
        }

        [HideFromIl2Cpp]
        private void RefreshCharacters()
        {
            _characters.Clear();
            _characters.AddRange(FindCurrentCharacters());

            var stillPresent = false;
            CharaEntry preferred = null;
            foreach (var chara in _characters)
            {
                if (chara.Root == _currentCharacterRoot) stillPresent = true;
                // Prefer a female character
                if (preferred == null || (chara.IsFemale && !preferred.IsFemale)) preferred = chara;
            }
            if (_currentCharacterRoot == null || !stillPresent)
                _currentCharacterRoot = preferred?.Root;

            VirginBloodPlugin.Log.LogInfo($"Found {_characters.Count} characters");
        }

        [HideFromIl2Cpp]
        private List<CharaEntry> FindCurrentCharacters()
        {
            // Same scene-based lookup as SVS_CheatTools' GameUtilities.GetCurrentHumans
            var result = new List<CharaEntry>();
            List<Human> humans;
            try
            {
                humans = GetCurrentHumans();
            }
            catch (Exception e)
            {
                VirginBloodPlugin.Log.LogWarning($"Scene character lookup failed, listing all loaded characters: {e.Message}");
                humans = GetLoadedHumans();
            }
            foreach (var human in humans)
                AddCharaEntry(result, human);
            return result;
        }

        [HideFromIl2Cpp]
        private static List<Human> GetCurrentHumans()
        {
            var humans = new List<Human>();

            // Character maker: only the character being edited
            if (CharacterCreation.HumanCustom.Initialized)
            {
                var maker = CharacterCreation.HumanCustom.Instance;
                if (maker?.Human != null)
                    humans.Add(maker.Human);
                return humans;
            }

            // H scene: the actors taking part (copies of the overworld characters)
            if (SV.H.HScene.Active())
            {
                var actors = SV.H.HScene._instance?.Actors;
                if (actors != null)
                {
                    foreach (var hActor in actors)
                    {
                        var human = hActor?.Actor?.chaCtrl;
                        if (human != null) humans.Add(human);
                    }
                }
                return humans;
            }

            // ADV / talk scene: conversation partners and the player
            var talkManager = Manager.TalkManager._instance;
            if (talkManager != null && ADV.ADVManager._instance?.IsADV == true)
            {
                foreach (var actor in new[] { talkManager.Npc1, talkManager.Npc2, talkManager.Npc3, talkManager.Npc4, talkManager.PlayerHi })
                {
                    var human = actor?.chaCtrl;
                    if (human != null) humans.Add(human);
                }
                return humans;
            }

            // Overworld: every loaded character that is currently active
            return GetLoadedHumans();
        }

        [HideFromIl2Cpp]
        private static List<Human> GetLoadedHumans()
        {
            var humans = new List<Human>();
            var list = Human.list;
            if (list == null) return humans;
            var count = list.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<Human>>().Count;
            for (int i = 0; i < count; i++)
            {
                var human = list[i];
                if (human != null) humans.Add(human);
            }
            return humans;
        }

        [HideFromIl2Cpp]
        private static void AddCharaEntry(List<CharaEntry> result, Human human)
        {
            if (human == null || human.disposed) return;
            var root = human.transform;
            if (root == null || !root.gameObject.activeInHierarchy) return;

            var isFemale = human.sex == 1;
            var name = human.fileParam?.fullname;
            if (string.IsNullOrEmpty(name))
                name = root.name;

            result.Add(new CharaEntry
            {
                Root = root,
                IsFemale = isFemale,
                Label = $"{name} ({(isFemale ? "F" : "M")})",
            });
        }

        [HideFromIl2Cpp]
        private void AddStain()
        {
            if (_currentCharacterRoot == null)
                RefreshCharacters();
            if (_currentCharacterRoot == null)
            {
                VirginBloodPlugin.Log.LogWarning("No character selected");
                return;
            }
            PlaceStain(_currentCharacterRoot, auto: false);
        }

        [HideFromIl2Cpp]
        private Stain PlaceStain(Transform root, bool auto, int? fixedSeed = null, bool settled = false)
        {
            // A character that already has blood gets a new shape instead of a second layer
            var existing = _stains.FirstOrDefault(s => s.Root == root && s.Body != null);
            if (existing != null)
            {
                _stains.Remove(existing);
                _stains.Add(existing);
                Regenerate(existing, fixedSeed ?? _rng.Next());
                return existing;
            }

            var body = BodyOverlay.FindBody(root);
            if (body == null)
            {
                VirginBloodPlugin.Log.LogWarning($"No body mesh found on {root.name}");
                return null;
            }

            var seed = fixedSeed ?? _rng.Next();
            var blood = BodyOverlay.GenerateTexture(body, seed, VirginBloodPlugin.Drips.Value,
                VirginBloodPlugin.Spread.Value, VirginBloodPlugin.StreakLength.Value, VirginBloodPlugin.Position,
                VirginBloodPlugin.GetColor(), VirginBloodPlugin.Flow.Value && !settled ? 0f : 1f);
            if (blood == null) return null;
            var material = StainFactory.CreateMaterial(blood, body.sharedMaterial);
            if (material == null)
            {
                blood.Destroy();
                return null;
            }

            var stain = new Stain { Root = root, Body = body, Material = material, Blood = blood, Seed = seed, Auto = auto };
            (stain.Actor, stain.Name) = HSceneActorOf(root);
            StartFlow(stain);
            // Blood carried over from the H scene has already run down her legs
            if (settled)
            {
                stain.Flowing = false;
                stain.FlowLeft = stain.FlowRight = 1f;
            }
            _stains.Add(stain);
            Attach(stain);

            VirginBloodPlugin.Log.LogInfo($"Blood placed on {root.name}/{body.name}{(auto ? " (first time)" : "")}");
            return stain;
        }

        // The save data actor behind a character in the running H scene, or zero outside of one
        [HideFromIl2Cpp]
        private static (IntPtr Actor, string Name) HSceneActorOf(Transform root)
        {
            try
            {
                if (!SV.H.HScene.Active()) return (IntPtr.Zero, null);
                var actors = SV.H.HScene._instance?.Actors;
                if (actors == null) return (IntPtr.Zero, null);
                foreach (var hActor in actors)
                {
                    var human = hActor?.Human;
                    if (human != null && human.transform == root)
                        return (hActor.Actor?.Pointer ?? IntPtr.Zero, human.fileParam?.fullname);
                }
            }
            catch (Exception e)
            {
                VirginBloodPlugin.Log.LogWarning($"H scene actor lookup failed: {e.Message}");
            }
            return (IntPtr.Zero, null);
        }

        // Puts every look and behaviour setting back to its default (keys are left alone) and redraws the last blood
        [HideFromIl2Cpp]
        private void RestoreDefaults()
        {
            VirginBloodPlugin.RestoreDefaults();
            var last = LastStain();
            if (last == null) return;
            StainFactory.ApplyColor(last.Material);
            RerollLast(keepSeed: true);
        }

        [HideFromIl2Cpp]
        private void UpdateFirstTime()
        {
            if (!VirginBloodPlugin.AutoFirstTime.Value) return;

            bool inH;
            try
            {
                inH = SV.H.HScene.Active();
            }
            catch (Exception e)
            {
                if (!_hErrorLogged)
                    VirginBloodPlugin.Log.LogWarning($"H scene check failed: {e.Message}");
                _hErrorLogged = true;
                inH = false;
            }
            if (!inH)
            {
                if (_wasInH)
                    OnHSceneEnded();
                _wasInH = false;
                return;
            }
            if (!_wasInH)
                _postH.Clear();
            _wasInH = true;

            if (++_hPollFrames < 15) return;
            _hPollFrames = 0;

            var actors = SV.H.HScene._instance?.Actors;
            if (actors == null) return;
            foreach (var hActor in actors)
            {
                if (hActor == null) continue;
                var status = hActor.hStatus;
                if (status == null) continue;
                // Backup for the scene setup hooks, in case the game built the actor some other way
                if (_seenActors.Add(hActor.Pointer))
                    VirginFix.Apply(hActor, "scene check");
                if (hActor.IsMan) continue;
                if (status.IsLost(SV.H.HActor.HStatus.VirginPart.Anal))
                    RecordLoss(hActor, vagina: false, anal: true);
                if (!LostVirginity(hActor, status)) continue;
                RecordLoss(hActor, vagina: true, anal: false);

                var human = hActor.Human;
                if (human == null || !_bledThisScene.Add(human.Pointer)) continue;

                VirginBloodPlugin.Log.LogInfo($"{human.fileParam?.fullname ?? human.name} lost her virginity");
                var stain = PlaceStain(human.transform, auto: true);
                if (stain != null)
                    stain.Actor = hActor.Actor?.Pointer ?? IntPtr.Zero;
            }
        }

        // The game's own record of her virginity, which it follows for conversations and H
        [HideFromIl2Cpp]
        private static bool GameVirgin(SV.H.HActor hActor) => hActor.Actor?.charasGameParam?.isVirgin == true;

        // The voice system tracks what kind of insertion is happening and whether the actor is a virgin
        [HideFromIl2Cpp]
        private static SV.H.Words.GeneralWords GeneralWords(SV.H.HActor hActor) => hActor.WordPlayer?.FlagManager?._general;

        [HideFromIl2Cpp]
        private bool LostVirginity(SV.H.HActor hActor, SV.H.HActor.HStatus status)
        {
            var part = SV.H.HActor.HStatus.VirginPart.Vagina;
            if (status.IsLost(part)) return true;

            var game = GameVirgin(hActor);
            var words = GeneralWords(hActor);
            var key = hActor.Pointer;
            if (!_virginAtStart.TryGetValue(key, out var startVirgin))
            {
                _virginAtStart[key] = status.IsInitVirgin(part) || game;
                return false;
            }
            if (!startVirgin) return false;

            // When the game tracks her virginity it marks the loss itself at penetration, checked above.
            // The voice system's insert type turns on as soon as an insertion position is picked,
            // before penetration, so it is only a fallback for when the game isn't tracking it
            if (status.IsInitVirgin(part)) return false;
            if (words != null && words._insertType == SV.H.Words.FlagManager.InsertType.Vagina) return true;
            return !game;
        }

        // Losses are written to the save data when the scene ends, so nothing changes under the running scene
        [HideFromIl2Cpp]
        private void RecordLoss(SV.H.HActor hActor, bool vagina, bool anal)
        {
            var actor = hActor.Actor;
            if (actor == null || !VirginBloodPlugin.FixVirginity.Value) return;
            _pendingLoss.TryGetValue(actor.Pointer, out var pending);
            _pendingLoss[actor.Pointer] = (actor, hActor.Name, pending.Vagina || vagina, pending.Anal || anal);
        }

        [HideFromIl2Cpp]
        private void OnHSceneEnded()
        {
            foreach (var loss in _pendingLoss.Values)
                VirginFix.SaveLoss(loss.Actor, loss.Name, loss.Vagina, loss.Anal);
            _pendingLoss.Clear();
            _seenActors.Clear();
            _virginAtStart.Clear();
            _bledThisScene.Clear();
            foreach (var stain in _stains.ToList())
            {
                // Blood on an H scene character, placed by hand or by her first time, follows her into the dialogue
                var carry = VirginBloodPlugin.KeepInDialogue.Value && stain.Actor != IntPtr.Zero;
                if (carry)
                    _postH[stain.Actor] = (stain.Seed, stain.Name);
                if (!carry && !(stain.Auto && !VirginBloodPlugin.KeepAfterH.Value)) continue;
                DestroyStain(stain);
                _stains.Remove(stain);
            }
            _postHSince = Time.time;
            _postHSawDialogue = false;
            _postHLog = 0f;
            if (_postH.Count > 0)
                VirginBloodPlugin.Log.LogInfo($"Carrying blood of {_postH.Count} character(s) into the dialogue after H");
        }

        // The dialogue after H shows the characters with their own bodies, not the H scene's copies,
        // so the blood is placed again on whoever from the scene is on screen, and removed when she leaves
        [HideFromIl2Cpp]
        private void UpdatePostH()
        {
            if (_postH.Count == 0 || _wasInH) return;

            var talkManager = Manager.TalkManager._instance;
            var inDialogue = talkManager != null && ADV.ADVManager._instance?.IsADV == true;
            if (inDialogue)
                _postHSawDialogue = true;

            var elapsed = Time.time - _postHSince;
            if (elapsed < 20f && Time.time - _postHLog >= 2f)
            {
                _postHLog = Time.time;
                VirginBloodPlugin.Log.LogInfo($"After H ({elapsed:F0}s): dialogue={inDialogue}");
            }

            // Only the dialogue's own characters count: the H scene's copies are still around for a moment after it ends
            if (inDialogue)
            {
                foreach (var actor in new[] { talkManager.Npc1, talkManager.Npc2, talkManager.Npc3, talkManager.Npc4 })
                {
                    var human = actor?.chaCtrl;
                    if (human == null || human.disposed) continue;
                    var name = human.fileParam?.fullname;
                    var pending = _postH.FirstOrDefault(p => p.Value.Name != null && p.Value.Name == name);
                    if (pending.Value.Name == null) continue;
                    var root = human.transform;
                    if (_stains.Any(s => s.Root == root && s.Body != null)) continue;
                    var stain = PlaceStain(root, auto: true, fixedSeed: pending.Value.Seed, settled: true);
                    if (stain != null)
                    {
                        stain.Actor = pending.Key;
                        stain.Name = name;
                        VirginBloodPlugin.Log.LogInfo($"Blood carried into the dialogue on {name}");
                    }
                }
            }

            // The dialogue is over, or none ever showed up
            if ((_postHSawDialogue && !inDialogue) || elapsed > PostHMaxSeconds)
                EndPostH();
        }

        [HideFromIl2Cpp]
        private void EndPostH()
        {
            foreach (var stain in _stains.Where(s => s.Auto).ToList())
            {
                DestroyStain(stain);
                _stains.Remove(stain);
            }
            _postH.Clear();
        }

        [HideFromIl2Cpp]
        private static bool IsAttached(Stain stain)
        {
            foreach (var m in stain.Body.sharedMaterials)
                if (m != null && m.Pointer == stain.Material.Pointer) return true;
            return false;
        }

        [HideFromIl2Cpp]
        private static void Attach(Stain stain)
        {
            // Materials past the last submesh draw that submesh again, here the whole body
            var materials = stain.Body.sharedMaterials.ToList();
            materials.Add(stain.Material);
            stain.Body.sharedMaterials = materials.ToArray();
        }

        [HideFromIl2Cpp]
        private static void Detach(Stain stain)
        {
            if (stain.Body == null || stain.Material == null) return;
            var materials = stain.Body.sharedMaterials.Where(m => m == null || m.Pointer != stain.Material.Pointer).ToArray();
            stain.Body.sharedMaterials = materials;
        }

        [HideFromIl2Cpp]
        private void KeepAttached()
        {
            foreach (var stain in _stains.ToList())
            {
                if (stain.Body == null || stain.Root == null)
                {
                    DestroyStain(stain);
                    _stains.Remove(stain);
                    continue;
                }
                if (!IsAttached(stain))
                    Attach(stain);
            }
        }

        [HideFromIl2Cpp]
        private Stain LastStain()
        {
            _stains.RemoveAll(s => s.Body == null);
            return _stains.Count > 0 ? _stains[_stains.Count - 1] : null;
        }

        [HideFromIl2Cpp]
        private void Regenerate(Stain stain, int seed)
        {
            var blood = BodyOverlay.GenerateTexture(stain.Body, seed, VirginBloodPlugin.Drips.Value,
                VirginBloodPlugin.Spread.Value, VirginBloodPlugin.StreakLength.Value, VirginBloodPlugin.Position,
                VirginBloodPlugin.GetColor(), VirginBloodPlugin.Flow.Value ? 0f : 1f);
            if (blood == null) return;

            var old = stain.Blood;
            stain.Seed = seed;
            stain.Blood = blood;
            StartFlow(stain);
            StainFactory.ApplyTexture(stain.Material, blood);
            StainFactory.ApplyColor(stain.Material);
            old?.Destroy();
        }

        [HideFromIl2Cpp]
        private static void StartFlow(Stain stain)
        {
            stain.Flowing = VirginBloodPlugin.Flow.Value;
            stain.FlowLeft = stain.FlowRight = stain.Flowing ? 0f : 1f;
            stain.LastFlowUpdate = Time.time;
            foreach (var bone in stain.Body.bones)
            {
                if (bone == null) continue;
                switch (bone.name)
                {
                    case "cf_s_thigh01_L": stain.HipLeft = bone; break;
                    case "cf_s_leg01_L": stain.KneeLeft = bone; break;
                    case "cf_s_thigh01_R": stain.HipRight = bone; break;
                    case "cf_s_leg01_R": stain.KneeRight = bone; break;
                }
            }
        }

        // Blood runs down a thigh only while gravity pulls it toward the knee, so a raised leg
        // or one lying flat holds its drips where they are until it points down again
        [HideFromIl2Cpp]
        private static bool LegPointsDown(Transform hip, Transform knee)
        {
            if (!VirginBloodPlugin.Gravity.Value || hip == null || knee == null) return true;
            var thigh = knee.position - hip.position;
            return thigh.magnitude > 1e-4f && thigh.y / thigh.magnitude < -LegDownThreshold;
        }

        // Moves running drips further down the thighs; the stain at the opening never changes
        [HideFromIl2Cpp]
        private void UpdateFlow()
        {
            foreach (var stain in _stains)
            {
                if (!stain.Flowing || stain.Blood == null || Time.time - stain.LastFlowUpdate < FlowUpdateInterval) continue;
                var step = (Time.time - stain.LastFlowUpdate) / Mathf.Max(0.1f, VirginBloodPlugin.FlowSeconds.Value);
                stain.LastFlowUpdate = Time.time;

                var changed = false;
                if (stain.FlowLeft < 1f && LegPointsDown(stain.HipLeft, stain.KneeLeft))
                {
                    stain.FlowLeft = Mathf.Min(1f, stain.FlowLeft + step);
                    changed = true;
                }
                if (stain.FlowRight < 1f && LegPointsDown(stain.HipRight, stain.KneeRight))
                {
                    stain.FlowRight = Mathf.Min(1f, stain.FlowRight + step);
                    changed = true;
                }
                if (changed)
                    stain.Blood.Colorize(VirginBloodPlugin.GetColor(), stain.FlowLeft, stain.FlowRight);
                if (stain.FlowLeft >= 1f && stain.FlowRight >= 1f)
                    stain.Flowing = false;
            }
        }

        // The game's cum is Obi fluid. Each emitter coming out of a girl who has blood gets its renderer tinted
        // toward red, and goes back to its own colour when she has no blood any more
        private const float CumTickSeconds = 0.2f;
        private const float CumOwnerRange = 0.8f;
        private float _cumTick;
        private readonly Dictionary<IntPtr, Color> _cumOriginal = new Dictionary<IntPtr, Color>();
        private readonly HashSet<IntPtr> _cumLogged = new HashSet<IntPtr>();

        [HideFromIl2Cpp]
        private static Vector3 Pelvis(Stain stain)
        {
            if (stain.HipLeft != null && stain.HipRight != null)
                return (stain.HipLeft.position + stain.HipRight.position) * 0.5f;
            return stain.Root.position + Vector3.up * 0.8f;
        }

        [HideFromIl2Cpp]
        private void UpdateCumMix()
        {
            if (Time.time - _cumTick < CumTickSeconds) return;
            _cumTick = Time.time;
            var enabled = VirginBloodPlugin.MixWithCum.Value;
            if (!enabled && _cumOriginal.Count == 0) return;

            try
            {
                var tint = Color.Lerp(Color.white, new Color(0.85f, 0.05f, 0.08f, 1f), VirginBloodPlugin.CumMix.Value);
                foreach (var emitter in FindObjectsOfType<Obi.ObiEmitter>())
                {
                    if (emitter == null) continue;
                    var renderers = emitter.GetComponentsInChildren<Obi.ObiParticleRenderer>(true);
                    if (renderers == null || renderers.Length == 0)
                        renderers = emitter.GetComponentsInParent<Obi.ObiParticleRenderer>(true);
                    if (renderers == null) continue;

                    Stain owner = null;
                    var best = CumOwnerRange;
                    if (enabled)
                    {
                        foreach (var stain in _stains)
                        {
                            if (stain.Body == null || stain.Root == null) continue;
                            var d = Vector3.Distance(emitter.transform.position, Pelvis(stain));
                            if (d < best) { best = d; owner = stain; }
                        }
                    }

                    foreach (var renderer in renderers)
                    {
                        if (renderer == null) continue;
                        var key = renderer.Pointer;
                        if (!_cumOriginal.TryGetValue(key, out var original))
                        {
                            original = renderer.particleColor;
                            _cumOriginal[key] = original;
                        }
                        if (_cumLogged.Count < 8 && _cumLogged.Add(key))
                            VirginBloodPlugin.Log.LogInfo($"Cum emitter {emitter.name} at {emitter.transform.position}, colour {original}, shader {renderer.ParticleMaterial?.shader?.name}, owner {(owner != null ? owner.Root.name : "none")}");
                        var target = owner != null
                            ? new Color(original.r * tint.r, original.g * tint.g, original.b * tint.b, original.a)
                            : original;
                        if (renderer.particleColor != target)
                            renderer.particleColor = target;
                    }
                }
            }
            catch (Exception e)
            {
                if (!_cumLogged.Contains(IntPtr.Zero))
                {
                    _cumLogged.Add(IntPtr.Zero);
                    VirginBloodPlugin.Log.LogWarning($"Cum mixing failed: {e.Message}");
                }
            }
        }

        [HideFromIl2Cpp]
        private void RestoreCum()
        {
            try
            {
                foreach (var renderer in FindObjectsOfType<Obi.ObiParticleRenderer>())
                {
                    if (renderer != null && _cumOriginal.TryGetValue(renderer.Pointer, out var original))
                        renderer.particleColor = original;
                }
            }
            catch (Exception) { }
            _cumOriginal.Clear();
        }

        [HideFromIl2Cpp]
        private void RerollLast(bool keepSeed = false)
        {
            var stain = LastStain();
            if (stain == null) return;
            Regenerate(stain, keepSeed ? stain.Seed : _rng.Next());
        }

        [HideFromIl2Cpp]
        private void DestroyStain(Stain stain)
        {
            Detach(stain);
            if (stain.Material != null)
                Destroy(stain.Material);
            stain.Blood?.Destroy();
        }

        [HideFromIl2Cpp]
        private void RemoveLast()
        {
            var stain = LastStain();
            if (stain == null) return;
            DestroyStain(stain);
            _stains.Remove(stain);
        }

        [HideFromIl2Cpp]
        private void RemoveAllStains()
        {
            foreach (var stain in _stains)
                DestroyStain(stain);
            _stains.Clear();
        }

        private void OnDestroy()
        {
            RestoreCameraControls();
            RemoveAllStains();
            RestoreCum();
        }
    }
}
