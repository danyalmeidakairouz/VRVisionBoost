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
        public const string VERSION = "0.1.0";

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
        private float _nextTick;
        private int _toggleKey, _dumpKey, _reloadKey, _glowKey;

        void Awake()
        {
            _keys = new Hotkeys();
            _vision = new VisionBooster(Plugin.Cfg, Plugin.Logger);
            _glow = new BloodGlow(Plugin.Cfg, Plugin.Logger);
            _vision.Glow = _glow;   // so the dump can say why a unit is or is not glowing
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

            // Two actions on one key silently means only the first ever fires: WasPressed
            // consumes the up-to-down edge for that key, so the second call sees it already down.
            WarnIfShared(_toggleKey, toggle, "toggle", _dumpKey, dump, "dump");
            WarnIfShared(_toggleKey, toggle, "toggle", _reloadKey, reload, "reload");
            WarnIfShared(_dumpKey, dump, "dump", _reloadKey, reload, "reload");
            WarnIfShared(_toggleKey, toggle, "toggle", _glowKey, glow, "glow toggle");
            WarnIfShared(_dumpKey, dump, "dump", _glowKey, glow, "glow toggle");
            WarnIfShared(_reloadKey, reload, "reload", _glowKey, glow, "glow toggle");

            // Report what is actually bound, never what was merely configured: the log is the
            // only feedback channel, so echoing an unparseable string back would send someone
            // hunting for a broken hotkey that is in fact bound to the default.
            Plugin.Logger.LogInfo($"Hotkeys - vision: {toggle}  glow: {glow}  dump: {dump}  "
                                + $"reload: {reload}");
        }

        private static void WarnIfShared(int vkA, string nameA, string labelA,
                                        int vkB, string nameB, string labelB)
        {
            if (vkA == 0 || vkA != vkB) return;
            Plugin.Logger.LogWarning($"{labelA} and {labelB} are both bound to {nameA} - only "
                                   + $"{labelA} will fire. Give them different keys.");
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
                Plugin.Logger.LogInfo($"Vision boost {(_vision.Toggle() ? "ON" : "OFF")}");
                _nextTick = 0f; // apply immediately rather than waiting out the interval
            }
            if (_keys.WasPressed(_glowKey))
            {
                // Writing .Value persists it, so the choice survives a restart. BloodGlow reads
                // the setting at the top of every frame and clears its own targets when off, so
                // nothing else has to be undone here.
                bool on = !Plugin.Cfg.GlowEnabled.Value;
                Plugin.Cfg.GlowEnabled.Value = on;
                Plugin.Logger.LogInfo($"Blood glow {(on ? "ON" : "OFF")}");
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
            try { _glow.Update(); }
            catch (Exception e) { Plugin.Logger.LogError($"Blood glow error: {Describe(e)}"); }

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
            try { _vision?.Revert(); }
            catch (Exception e) { Plugin.Logger.LogError($"Revert on shutdown failed: {e.Message}"); }
        }
    }
}
