using BepInEx.Configuration;

namespace VRVisionBoost
{
    /// <summary>
    /// All tunables. BepInEx names the file after the plugin GUID, so it lands at
    /// <c>BepInEx/config/vrvisionboost.cfg</c> after the first launch. Edit it and press
    /// the reload key in game.
    ///
    /// Every value is read at its use site on each tick, so a reload applies immediately -
    /// nothing here is parsed or cached at construction. Keep it that way: if you add an entry
    /// that needs parsing, you must also re-derive it on reload or it will silently not apply.
    /// </summary>
    public sealed class Settings
    {
        public ConfigEntry<bool> Enabled;
        public ConfigEntry<string> ToggleKey;
        public ConfigEntry<string> DumpKey;
        public ConfigEntry<string> ReloadKey;
        public ConfigEntry<string> GlowToggleKey;
        public ConfigEntry<float> DumpComponentsRange;

        public ConfigEntry<float> IntervalMs;
        public ConfigEntry<float> VisionRange;
        public ConfigEntry<bool> RevealAllUnits;
        public ConfigEntry<bool> SeeThroughWalls;
        public ConfigEntry<float> HudDistance;
        public ConfigEntry<bool> VisibleFromFlight;

        public ConfigEntry<bool> KeepModelsLoaded;
        public ConfigEntry<float> ModelShowRange;

        public ConfigEntry<bool> GlowEnabled;
        public ConfigEntry<float> GlowHighMin;
        public ConfigEntry<float> GlowPerfectMin;
        public ConfigEntry<string> GlowHighColor;
        public ConfigEntry<string> GlowPerfectColor;
        public ConfigEntry<float> GlowIntensity;
        public ConfigEntry<int> GlowImportance;
        public ConfigEntry<float> GlowScanMs;
        public ConfigEntry<string> GlowQualityScale;
        public ConfigEntry<bool> GlowUseBloodComponent;
        public ConfigEntry<string> GlowProperty;
        public ConfigEntry<string> GlowMode;
        public ConfigEntry<string> GlowRendererProperty;

        public Settings(ConfigFile cfg)
        {
            Enabled = cfg.Bind("1. General", "Enabled", false,
                "Start with the vision boost active. Off by default - toggle it in game with ToggleKey.");
            ToggleKey = cfg.Bind("1. General", "ToggleKey", "F6",
                "Key that toggles the vision boost on/off.");
            DumpKey = cfg.Bind("1. General", "DumpKey", "F10",
                "Key that logs every unit the client currently knows about, with distance, hide state, blood quality and whether the glow selected it. The farthest entry is the server's real streaming radius - read that number before assuming a setting is broken.");
            ReloadKey = cfg.Bind("1. General", "ReloadKey", "F11",
                "Key that reloads this config file. Leave empty to disable.");
            GlowToggleKey = cfg.Bind("1. General", "GlowToggleKey", "F7",
                "Key that toggles the blood-quality glow on/off, independently of ToggleKey. Flips GlowEnabled and saves it, so the state survives a restart. NOTE: this plugin cannot see another plugin's bindings, so if something else you have installed also uses this key, one press triggers both - rebind whichever is easier. Leave empty to disable.");
            DumpComponentsRange = cfg.Bind("1. General", "DumpComponentsRange", 3f,
                "When the dump key is pressed, also print the FULL component list of every unit within this many meters, plus a diagnosis of why it is or is not glowing. Stand next to the unit you are asking about and press the dump key. 0 disables it. Keep it small - each unit prints well over a hundred component names.");

            IntervalMs = cfg.Bind("2. Vision", "IntervalMs", 100f,
                "How often the vision components are rewritten (ms). The game's VisibilitySystem_Client recomputes every frame, so this has to keep up; lower is smoother, higher is cheaper.");
            VisionRange = cfg.Bind("2. Vision", "VisionRange", 120f,
                "Your own vision radius in meters (ProjectM.Vision.Range). Rewritten every tick rather than once, so anything the game does to your vision is overwritten again on the next pass. 0 = leave your vision alone and only un-hide the units around you.");
            RevealAllUnits = cfg.Bind("2. Vision", "RevealAllUnits", true,
                "Un-hide mobs, VBloods and critters that the game's fog of war would otherwise cut off. This is what the plugin is for: those entities stream with the world chunks and stay available out to roughly 90m, so raising VisionRange genuinely shows them further away. Off makes the plugin's vision half do nothing; the glow is independent and keeps working.");
            SeeThroughWalls = cfg.Bind("2. Vision", "SeeThroughWalls", false,
                "Off, the plugin widens the distance check only and lets the game run its normal line-of-sight test, so you see units further away but not through anything. On, it sets Hideable.IgnoreLoS so they stay visible through walls, rocks and terrain. On also means fighting the game over IsHidden every tick, which is why it is not the default.");
            HudDistance = cfg.Bind("2. Vision", "HudDistance", 0f,
                "Also raise CheckOnScreen.MaxDistanceForHudAndFadeOut to this many meters so health bars / nameplates and the distance fade-out do not cut off before the unit itself does. Defaults to 20 in game. 0 = leave the HUD alone.");
            VisibleFromFlight = cfg.Bind("2. Vision", "VisibleFromFlight", true,
                "Keep units visible while you are FLYING, and without this the rest of the vision boost does nothing in bat form. VisibilitySystem_Client takes a flying flag for the viewer and reads a ProjectM.VisibleFromFlight tag on each target: while you fly, an entity WITHOUT that tag is hidden at any distance - measured, a unit 7m below was still hidden=True. Almost no unit ships with the tag (only the Manticore variants do), so the plugin adds the game's own tag to the units it reveals and removes exactly those again on toggle-off. Not a wallhack: line of sight and stealth are still evaluated normally.");

            KeepModelsLoaded = cfg.Bind("3. Models", "KeepModelsLoaded", false,
                "Second gate, separate from hiding: a unit's visual model (HybridModelUser) is unloaded after it has not been seen for a while, so an un-hidden unit can still render as nothing until its model streams back in. This keeps TimeSinceLastSeen pinned at 0 for the entities being revealed. Costs memory and asset streaming. Off by default - turn it on only if the dump shows a revealed unit with a rising lastSeen and no model.");
            ModelShowRange = cfg.Bind("3. Models", "ModelShowRange", 0f,
                "0 = leave alone. Otherwise the radius (meters) inside which unit models are instantiated at all - HybridModelSystem.SHOW_INSIDE_RANGE_SQ. A unit can be un-hidden and still render as nothing because it is outside this radius. Try 80-150 if revealed units stay invisible at distance. This is a process-wide static, so the plugin restores it on toggle-off.");

            GlowEnabled = cfg.Bind("4. Blood glow", "GlowEnabled", false,
                "Tint units by blood quality so a good feed target stands out at a glance - the point being to pick the 100% prisoner out of a cell block without clicking through each one. Independent of the vision boost and toggled separately with GlowToggleKey.");
            GlowHighMin = cfg.Bind("4. Blood glow", "GlowHighMin", 90f,
                "Blood quality percentage at or above which a unit is tinted GlowHighColor.");
            GlowPerfectMin = cfg.Bind("4. Blood glow", "GlowPerfectMin", 100f,
                "Blood quality percentage at or above which a unit is tinted GlowPerfectColor instead. Checked first, so it wins over GlowHighMin.");
            GlowHighColor = cfg.Bind("4. Blood glow", "GlowHighColor", "0,1,0",
                "Colour for units at or above GlowHighMin, as r,g,b in the 0-1 range. Default is green.");
            GlowPerfectColor = cfg.Bind("4. Blood glow", "GlowPerfectColor", "1,0,1",
                "Colour for units at or above GlowPerfectMin, as r,g,b in the 0-1 range. Default is magenta.");
            GlowIntensity = cfg.Bind("4. Blood glow", "GlowIntensity", 1f,
                "Multiplier on the colour. Higher is brighter and washes toward flat colour; lower is a subtler sheen. Try 0.3-2.");
            GlowImportance = cfg.Bind("4. Blood glow", "GlowImportance", 0,
                "Sequencer mode only: arbitration against the game's own uses of the channel named in GlowProperty - its hit flash writes there too. Higher should win. If a glowing unit stops flashing red when struck, lower this; if the glow is intermittent in combat, raise it.");
            GlowScanMs = cfg.Bind("4. Blood glow", "GlowScanMs", 500f,
                "How often blood quality is rescanned to rebuild the tint list (ms). In Sequencer mode the tint is re-emitted every frame regardless - that is required, the game drains its change list per frame - so this only controls how quickly a newly streamed unit starts glowing.");
            GlowMode = cfg.Bind("4. Blood glow", "GlowMode", "Both",
                "How the tint is applied, and you almost certainly want Both. Units come in two rendering flavours and each route only reaches one of them: HybridModelUser.ModelType = GameObject means a real Unity object exists and the Renderer route can paint its renderers directly, while Rukhanka means the unit is GPU-skinned through DOTS with no GameObject at all, so only the Sequencer route can reach it. Renderer = write MaterialPropertyBlocks on the model's renderers; nothing competes with you, but this plugin then owns putting them back. Sequencer = hand a change to the game's MaterialPropertySystem for the channel named in GlowProperty; self-reverting, but the game drives some of those channels itself and then the last write wins. Both = do each, which is why 'some units glow and others do not' was the symptom for so long.");
            GlowRendererProperty = cfg.Bind("4. Blood glow", "GlowRendererProperty", "BaseColor",
                "Renderer mode only: which shader colour to write - BaseColor (repaints the albedo; reads as a strong tint) or EmissiveColor (brightness that only blooms into a halo if BloomQuality is above 0 in the game's graphics settings - with bloom off it just looks brighter). Ignored in Sequencer mode.");
            GlowProperty = cfg.Bind("4. Blood glow", "GlowProperty", "_BlinkColor",
                "Sequencer mode only: which material channel carries the tint, from ProjectM.Sequencer.SupportedDotsProperty: _BlinkColor, _DissolveColor, _AlphaMultiply, _DitherAlpha, _DissolveHeightMultiplier, _RustleForceVector, _RustleAnimationTime, _OverlappingAnimationTime. _BlinkColor is the game's own hit-flash channel, which is also what it uses for the red aura on caged prisoners - so on those units the two writes compete and the colour you get is whichever landed last. Switching to a channel the game is not driving on that unit avoids the fight entirely. Only _BlinkColor and _DissolveColor carry a colour; the rest take a single number and will do something other than tint.");
            GlowUseBloodComponent = cfg.Bind("4. Blood glow", "GlowUseBloodComponent", true,
                "Also read quality from ProjectM.Blood.Quality for units that carry no BloodConsumeSource. Servants and some castle units have the one but not the other, so with this off they can never glow whatever their quality. Turn it off if it makes too many things glow.");
            GlowQualityScale = cfg.Bind("4. Blood glow", "GlowQualityScale", "Auto",
                "How to read BloodConsumeSource.BloodQuality: Auto, Fraction (0-1) or Percent (0-100). Auto treats anything at or below 1 as a fraction, which is ambiguous for exactly one value - a raw 1.0 is either 1% or 100%. Press the dump key, read the bloodQ= field, then pin this.");
        }
    }
}
