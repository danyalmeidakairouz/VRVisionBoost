using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace VRVisionBoost
{
    [BepInPlugin(GUID, NAME, VERSION)]
    [BepInProcess("VRising.exe")]
    public class Plugin : BasePlugin
    {
        // The GUID determines the config filename. Changing it orphans the existing config.
        public const string GUID = "vrvisionboost";
        public const string NAME = "VRVisionBoost";
        public const string VERSION = "1.0.3";

        internal static ManualLogSource Logger;
        internal static Settings Cfg;

        public override void Load()
        {
            Logger = Log;
            Cfg = new Settings(Config);
            ClassInjector.RegisterTypeInIl2Cpp<VisionBehaviour>();
            AddComponent<VisionBehaviour>();
            Log.LogInfo($"{NAME} {VERSION} loaded.");
            Log.LogInfo("Reveals only what the server already sent you - it cannot exceed the server's "
                      + "streaming radius. Client-side only. Intended for your own private server / solo play.");
        }
    }

    /// <summary>MonoBehaviour that re-applies the vision boost on its own clock.</summary>
    public class VisionBehaviour : MonoBehaviour
    {
        public VisionBehaviour(IntPtr ptr) : base(ptr) { }

        private Hotkeys _keys;
        private VisionBooster _vision;
        private BloodGlow _glow;
        private BatFog _batFog;
        private ChestGlow _chests;
        private float _nextTick;
        private int _toggleKey, _dumpKey, _reloadKey, _glowKey, _batFogKey, _chestKey;

        void Awake()
        {
            _keys = new Hotkeys();
            _vision = new VisionBooster(Plugin.Cfg, Plugin.Logger);
            _glow = new BloodGlow(Plugin.Cfg, Plugin.Logger);
            _batFog = new BatFog(Plugin.Cfg, Plugin.Logger);
            _chests = new ChestGlow(Plugin.Cfg, Plugin.Logger);
            _vision.Glow = _glow;       // so the dump can say why a unit is or is not glowing
            _vision.Chests = _chests;   // so the dump can also list world chests
            ParseHotkeys();
        }

        // Every config value is read at its use site, so a reload needs nothing re-derived
        // except the hotkeys, which are parsed from strings here.
        private void ParseHotkeys()
        {
            _toggleKey = Bind(Plugin.Cfg.ToggleKey.Value, "F6", "ToggleKey", out string toggle);
            _dumpKey = Bind(Plugin.Cfg.DumpKey.Value, "F10", "DumpKey", out string dump);

            // Reload may legitimately be unbound, so an empty value is not a mistake.
            string reloadCfg = Plugin.Cfg.ReloadKey.Value;
            _reloadKey = Hotkeys.Parse(reloadCfg);
            string reload = _reloadKey != 0 ? reloadCfg.Trim() : "(none)";
            if (_reloadKey == 0 && !string.IsNullOrWhiteSpace(reloadCfg))
                Plugin.Logger.LogWarning($"ReloadKey '{reloadCfg}' is not a key name this plugin "
                                       + "understands - the reload hotkey is disabled.");

            // The glow key may legitimately be unbound too.
            string glowCfg = Plugin.Cfg.GlowToggleKey.Value;
            _glowKey = Hotkeys.Parse(glowCfg);
            string glow = _glowKey != 0 ? glowCfg.Trim() : "(none)";
            if (_glowKey == 0 && !string.IsNullOrWhiteSpace(glowCfg))
                Plugin.Logger.LogWarning($"GlowToggleKey '{glowCfg}' is not a key name this plugin "
                                       + "understands - the glow hotkey is disabled.");

            // The bat fog key may legitimately be unbound too.
            string batFogCfg = Plugin.Cfg.BatFogKey.Value;
            _batFogKey = Hotkeys.Parse(batFogCfg);
            string batFog = _batFogKey != 0 ? batFogCfg.Trim() : "(none)";
            if (_batFogKey == 0 && !string.IsNullOrWhiteSpace(batFogCfg))
                Plugin.Logger.LogWarning($"BatFogKey '{batFogCfg}' is not a key name this plugin "
                                       + "understands - the bat fog hotkey is disabled.");

            // The chest glow key may legitimately be unbound too.
            string chestCfg = Plugin.Cfg.ChestGlowKey.Value;
            _chestKey = Hotkeys.Parse(chestCfg);
            string chest = _chestKey != 0 ? chestCfg.Trim() : "(none)";
            if (_chestKey == 0 && !string.IsNullOrWhiteSpace(chestCfg))
                Plugin.Logger.LogWarning($"ChestGlowKey '{chestCfg}' is not a key name this plugin "
                                       + "understands - the chest glow hotkey is disabled.");

            // Two actions on one key silently means only the first ever fires: WasPressed
            // consumes the up-to-down edge for that key, so the second call sees it already down.
            //
            // This used to be one hand-written call per pair, with a comment saying to add the
            // new row when a key was added. Five keys is ten pairs and six is fifteen, so that
            // list was one forgotten line away from a hotkey clash going unreported - which is
            // precisely the failure this check exists to catch, and the log is the only feedback
            // channel this plugin has. The pairs are now generated, so adding a seventh key means
            // adding one row to the table below and nothing else.
            //
            // THE ORDER OF THIS ARRAY IS LOAD-BEARING and must match the order of the
            // WasPressed() if-chain in Update(). WarnIfShared reports the EARLIER entry as the one
            // that fires, because WasPressed consumes the up-to-down edge and whichever branch
            // runs first therefore wins. The hand-written list this replaced was ordered
            // toggle/dump/reload/glow/..., which does NOT match Update(), so a dump+glow clash
            // announced "only dump will fire" when glow is in fact the one that fires - sending
            // you to debug the half that was working. Reorder Update() and you must reorder this.
            var bound = new[]
            {
                new KeyBinding(_toggleKey, toggle, "toggle"),
                new KeyBinding(_glowKey, glow, "glow toggle"),
                new KeyBinding(_batFogKey, batFog, "bat fog toggle"),
                new KeyBinding(_chestKey, chest, "chest glow toggle"),
                new KeyBinding(_dumpKey, dump, "dump"),
                new KeyBinding(_reloadKey, reload, "reload"),
            };
            for (int i = 0; i < bound.Length; i++)
                for (int j = i + 1; j < bound.Length; j++)
                    WarnIfShared(bound[i], bound[j]);

            // Report what is actually bound, never what was merely configured: the log is the
            // only feedback channel, so echoing an unparseable string back would send someone
            // hunting for a broken hotkey that is in fact bound to the default.
            Plugin.Logger.LogInfo($"Hotkeys - vision: {toggle}  glow: {glow}  batfog: {batFog}  "
                                + $"chests: {chest}  dump: {dump}  reload: {reload}");
        }

        /// <summary>A parsed hotkey plus what to call it in a warning. Name is what was actually
        /// bound, not what was configured - see <see cref="Bind"/>.</summary>
        private readonly struct KeyBinding
        {
            internal readonly int Vk;
            internal readonly string Name;
            internal readonly string Label;
            internal KeyBinding(int vk, string name, string label) { Vk = vk; Name = name; Label = label; }
        }

        private static void WarnIfShared(KeyBinding a, KeyBinding b)
        {
            // Vk 0 is "unbound", and any number of actions may be unbound at once.
            if (a.Vk == 0 || a.Vk != b.Vk) return;
            Plugin.Logger.LogWarning($"{a.Label} and {b.Label} are both bound to {a.Name} - only "
                                   + $"{a.Label} will fire. Give them different keys.");
        }

        /// <summary>
        /// Resolve a configured key name, falling back to <paramref name="fallback"/> and saying
        /// so out loud. <paramref name="effective"/> is what actually got bound.
        /// </summary>
        private static int Bind(string configured, string fallback, string label, out string effective)
        {
            int vk = Hotkeys.Parse(configured);
            if (vk != 0) { effective = configured.Trim(); return vk; }

            if (!string.IsNullOrWhiteSpace(configured))
                Plugin.Logger.LogWarning($"{label} '{configured}' is not a key name this plugin "
                                       + $"understands - using {fallback} instead. Single keys only "
                                       + "(F1-F24, A-Z, 0-9, Space, Insert, Numpad0-9, ...); "
                                       + "modifier combinations like Ctrl+F6 are not supported.");
            effective = fallback;
            return Hotkeys.Parse(fallback);
        }

        void Update()
        {
            _keys.Update();

            if (_keys.WasPressed(_toggleKey))
            {
                // Master switch: this turns the whole plugin off, not just the vision half.
                // Each feature owns undoing its own writes, so all three are told explicitly -
                // skipping their Update() alone would strand whatever they had already applied.
                bool on = _vision.Toggle();
                if (!on)
                {
                    try { _glow.Reset(); } catch { }
                    try { _batFog.Revert(); } catch { }
                    try { _chests.Reset(); } catch { }
                }
                Plugin.Logger.LogInfo($"VRVisionBoost {(on ? "ON" : "OFF")} - all features");
                _nextTick = 0f; // apply immediately rather than waiting out the interval
            }
            if (_keys.WasPressed(_glowKey))
            {
                // Writing .Value persists it, so the choice survives a restart. BloodGlow reads
                // the setting at the top of every frame and clears its own targets when off, so
                // nothing else has to be undone here.
                bool on = !Plugin.Cfg.GlowEnabled.Value;
                Plugin.Cfg.GlowEnabled.Value = on;
                Plugin.Logger.LogInfo($"Blood glow {(on ? "ON" : "OFF")}"
                                    + (_vision.Active ? "" : " (nothing will happen until the master toggle is on)"));
            }
            if (_keys.WasPressed(_batFogKey))
            {
                // Same shape as the glow toggle: persist the choice, and let BatFog notice the
                // setting changed on its next frame and revert itself. Nothing to undo here.
                bool on = !Plugin.Cfg.BatFogEnabled.Value;
                Plugin.Cfg.BatFogEnabled.Value = on;
                Plugin.Logger.LogInfo($"Bat form fog removal {(on ? "ON" : "OFF")}"
                                    + (_vision.Active ? "" : " (nothing will happen until the master toggle is on)"));
            }
            if (_keys.WasPressed(_chestKey))
            {
                // Same shape as the other two feature toggles: persist the choice, and let
                // ChestGlow notice the setting is off on its next frame and put its renderer
                // property blocks back itself. Nothing to undo here.
                bool on = !Plugin.Cfg.ChestGlowEnabled.Value;
                Plugin.Cfg.ChestGlowEnabled.Value = on;
                Plugin.Logger.LogInfo($"Chest glow {(on ? "ON" : "OFF")}"
                                    + (_vision.Active ? "" : " (nothing will happen until the master toggle is on)"));
            }
            if (_keys.WasPressed(_dumpKey))
            {
                try { _vision.Dump(); }
                catch (Exception e) { Plugin.Logger.LogError($"Dump failed: {e}"); }
            }
            if (_keys.WasPressed(_reloadKey))
            {
                // ConfigFile.Reload() calls File.ReadAllLines with no catch of its own, so a
                // deleted or locked .cfg would throw straight out of Update().
                try
                {
                    Plugin.Cfg.Enabled.ConfigFile.Reload(); // re-reads every entry in place
                    ParseHotkeys();
                    Plugin.Logger.LogInfo("Config reloaded.");
                }
                catch (Exception e) { Plugin.Logger.LogError($"Config reload failed: {e.Message}"); }
            }

            // Deliberately outside the interval gate below: the game drains its material
            // change list every frame, so a tint emitted at 10Hz would strobe.
            // Gated on the master switch, and deliberately outside the interval gate below: the
            // game drains its material change list every frame and HDRP re-blends its volume
            // stack every frame, so a value written at 10Hz would strobe or be blended back.
            if (_vision.Active)
            {
                try { _glow.Update(); }
                catch (Exception e) { Plugin.Logger.LogError($"Blood glow error: {Describe(e)}"); }

                try { _batFog.Update(); }
                catch (Exception e) { Plugin.Logger.LogError($"Bat fog error: {Describe(e)}"); }

                // Above the gate for the same reason as the blood glow: its sequencer route has
                // to re-emit every frame or the tint strobes, and its own scan interval
                // (ChestScanMs) throttles the expensive half internally.
                try { _chests.Update(); }
                catch (Exception e) { Plugin.Logger.LogError($"Chest glow error: {Describe(e)}"); }
            }

            float now = Time.unscaledTime;
            if (now < _nextTick) return;
            _nextTick = now + Mathf.Max(Plugin.Cfg.IntervalMs.Value, 1f) / 1000f;

            try
            {
                _vision.Tick();
            }
            catch (Exception e)
            {
                // never let an interop hiccup spam every frame
                Plugin.Logger.LogError($"Vision error (pausing 2s): {Describe(e)}");
                _nextTick = now + 2f;
            }
        }

        /// <summary>
        /// Message plus inner cause. A failure inside a static generic initialiser - such as the
        /// ComponentType cache resolving a component the interop set no longer has after a game
        /// patch - arrives as TypeInitializationException, whose own Message says only "the type
        /// initializer for X threw an exception" and leaves the actual reason in InnerException.
        /// The log is this plugin's only feedback channel, so printing just .Message would repeat
        /// a useless line every 2 seconds forever.
        /// </summary>
        private static string Describe(Exception e)
            => e.InnerException == null ? e.Message : $"{e.Message} -- cause: {e.InnerException.Message}";

        // SHOW_INSIDE_RANGE_SQ is a process-wide static, and the component values this plugin
        // writes are inputs the game reads but never writes back. Neither expires on its own, so
        // leaving without undoing them would strand the game in a modified state.
        void OnDestroy()
        {
            try { _glow?.Reset(); } catch { }
            try { _batFog?.Reset(); } catch { }
            try { _chests?.Reset(); } catch { }
            try { _vision?.Revert(); }
            catch (Exception e) { Plugin.Logger.LogError($"Revert on shutdown failed: {e.Message}"); }
        }
    }
}
