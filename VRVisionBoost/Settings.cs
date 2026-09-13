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

        public ConfigEntry<bool> ChestGlowEnabled;
        public ConfigEntry<string> ChestGlowKey;
        public ConfigEntry<string> ChestNameContains;
        public ConfigEntry<bool> ChestUnlootedOnly;
        public ConfigEntry<string> ChestColor;
        public ConfigEntry<string> ChestEmptyColor;
        public ConfigEntry<float> ChestGlowIntensity;
        public ConfigEntry<float> ChestGlowRange;
        public ConfigEntry<float> ChestScanMs;
        public ConfigEntry<string> ChestGlowMode;
        public ConfigEntry<string> ChestRendererProperty;
        public ConfigEntry<string> ChestGlowProperty;
        public ConfigEntry<int> ChestGlowImportance;
        public ConfigEntry<string> ChestDumpFilter;

        public ConfigEntry<bool> BatFogEnabled;
        public ConfigEntry<string> BatFogKey;
        public ConfigEntry<bool> ZeroCloudiness;
        public ConfigEntry<bool> DisableSkyClouds;
        public ConfigEntry<string> BatFogTargets;
        public ConfigEntry<bool> DestroyFogMaterial;

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

            BatFogEnabled = cfg.Bind("5. Bat form fog", "BatFogEnabled", false,
                "Remove the fog and clouds that close in around you in bat form. The game draws these with a dedicated full-screen effect called BatFormFog, and this switches that effect off. Independent of the vision boost and the glow, and toggled separately with BatFogKey. Off by default - turn it on in game and look down from altitude to judge it.");
            BatFogKey = cfg.Bind("5. Bat form fog", "BatFogKey", "F8",
                "Key that toggles the bat form fog removal on/off. Flips BatFogEnabled and saves it, so the choice survives a restart. Leave empty to disable.");
            ZeroCloudiness = cfg.Bind("5. Bat form fog", "ZeroCloudiness", true,
                "Also drive the world's cloud cover to zero while the fog removal is active (DayNightCycle.Cloudiness, normally 0.65). The screen effect above is only half of what you see in bat form - the rest is actual cloud cover with its own ground shadows, and leaving it alone means clouds still drift below you. This is GLOBAL weather, not a bat form setting: it changes the sky everywhere for as long as the toggle is on, and is restored when you switch it off. Set false to keep the weather untouched and remove only the screen fog.");
            DisableSkyClouds = cfg.Bind("5. Bat form fog", "DisableSkyClouds", true,
                "Also switch off the sky's own cloud layers - HDRP's VolumetricClouds and CloudLayer, which are a third thing again, separate from both the bat form screen effect and ZeroCloudiness above. Measured in game: with the first two off, clouds were still drawing, and these are what was left. Like ZeroCloudiness this is GLOBAL and restored on toggle-off. Set false if the sky ends up looking too empty.");
            BatFogTargets = cfg.Bind("5. Bat form fog", "BatFogTargets", "BatFormFog,StunlockFogVolumeComponent",
                "Which volume components to switch off, by type name, comma separated. This is a list rather than fixed code because V Rising does NOT use Unity's stock cloud system - the scene's 'Scene PostProcess' volume carries Stunlock's own StunlockSky and StunlockFogVolumeComponent instead of VolumetricClouds and CloudLayer, so the right target had to be found by looking rather than guessing. Names are matched against the component's type name, case-insensitively. Press the reload key after editing - no rebuild needed. Candidates seen in game: BatFormFog, StunlockFogVolumeComponent, StunlockSky, ExponentialFog, VolumetricFog, Fog, VolumetricClouds, CloudLayer, PhysicallyBasedSky, GradientSky, HDRISky. Add StunlockSky if clouds are still drawing, but expect it to change the whole sky, not just the clouds. Everything listed is restored on toggle-off.");
            DestroyFogMaterial = cfg.Bind("5. Bat form fog", "DestroyFogMaterial", true,
                "Destroy the BatFormFog effect's material instead of only switching the component off. Measured in game: setting the component inactive and zeroing its intensity left the fog drawing, and destroying the material is what actually stops it - this is also what the RetroCamera mod does, which is the only known working implementation. The material is copied before it is destroyed and rebuilt from that copy on toggle-off. Set false to use only the inactive/intensity route, which is gentler but did not work here.");

            ChestGlowEnabled = cfg.Bind("6. Chest glow", "ChestGlowEnabled", false,
                "Tint world chests by prefab name so a golden chest is findable from the air. Independent of the vision boost and the blood glow, and toggled separately with ChestGlowKey. Off by default - turn it on, stand next to a chest you can see and press the dump key to confirm the plugin agrees with you about what it is.");
            ChestGlowKey = cfg.Bind("6. Chest glow", "ChestGlowKey", "F9",
                "Key that toggles the chest glow on/off. Flips ChestGlowEnabled and saves it, so the choice survives a restart. Leave empty to disable.");
            ChestNameContains = cfg.Bind("6. Chest glow", "ChestNameContains", "WorldChest_Epic",
                "Which prefabs count as a chest, as a comma-separated case-insensitive list. A token that is a plain number is matched against the raw prefab GUID; every other token is matched as a substring of the prefab's authoring name. The world chests are TM_WorldChest_<kind>_01_Full and _Empty, where <kind> is Epic, Iron, Simple, Simple_GloomRot or Simple_SludgePools - and Epic is the gold-trimmed one, so the default lights up only those. Widen to 'WorldChest' for every world chest, or add 'Container' to include castle furniture. A BLANK value matches nothing rather than everything: the other reading would tint the whole world the moment you cleared it to see what it did. Names are read from the running game rather than hardcoded, so a patch that renumbers prefab GUIDs changes nothing here - and if name lookup ever fails on your build, the dump prints a guid= for each entity that you can paste in here instead.");
            ChestUnlootedOnly = cfg.Bind("6. Chest glow", "ChestUnlootedOnly", true,
                "Glow only chests that still have loot in them. A looted chest swaps to its _Empty prefab, so 'still worth walking to' is readable straight off the name and a chest visibly stops glowing once you empty it. Off also glows looted chests, in ChestEmptyColor.");
            ChestColor = cfg.Bind("6. Chest glow", "ChestColor", "1,0.84,0",
                "Colour for an unlooted chest, as r,g,b in the 0-1 range. Default is gold.");
            ChestEmptyColor = cfg.Bind("6. Chest glow", "ChestEmptyColor", "0.35,0.3,0.15",
                "Colour for a chest you have already looted. Only used when ChestUnlootedOnly is off - a dull version of the gold, so an emptied chest reads as 'been here' rather than as a target.");
            ChestGlowIntensity = cfg.Bind("6. Chest glow", "ChestGlowIntensity", 1f,
                "Multiplier on the colour. Higher is brighter and washes toward flat colour; lower is a subtler sheen. Try 0.3-2.");
            ChestGlowRange = cfg.Bind("6. Chest glow", "ChestGlowRange", 0f,
                "0 = every chest the client knows about. Otherwise only tint chests within this many meters of you. Chests are static scenery and there are a lot of them inside a castle, so this is here to keep the work down, not because distant chests are a problem.");
            ChestScanMs = cfg.Bind("6. Chest glow", "ChestScanMs", 1000f,
                "How often the chest list is rebuilt (ms). Much slower than the blood glow's scan on purpose: chests do not move and do not change quality, so the only thing this controls is how quickly a newly streamed chest starts glowing and how quickly one you just looted stops. In Sequencer mode the tint is still re-emitted every frame regardless - that is required, the game drains its change list per frame.");
            ChestGlowMode = cfg.Bind("6. Chest glow", "ChestGlowMode", "Both",
                "How the tint is applied, and you almost certainly want Both. Containers are drawn three different ways and each route reaches exactly one of them. Static = write the game's own ShaderProperty_BlinkColor on each of the entity's static render children; this is the ONLY route that reaches a world chest, whose gameplay entity carries no rendering components at all. Renderer = write MaterialPropertyBlocks on a hybrid model's renderers, falling back to the model's own child renderers when the character-only HybridModelRendererComponent is absent. Sequencer = hand a change to the game's MaterialPropertySystem for the channel named in ChestGlowProperty, the only way to reach a GPU-skinned (Rukhanka) prop. Both = do all three, which is why widening the filter makes some containers light up and not others - that is the rendering flavour, not a bug. The log names the route that reached each one.");
            ChestRendererProperty = cfg.Bind("6. Chest glow", "ChestRendererProperty", "EmissiveColor",
                "Renderer route only: which shader colour to write - EmissiveColor (brightness, which only blooms into a halo if BloomQuality is above 0 in the game's graphics settings) or BaseColor (repaints the albedo; reads as a strong flat tint). Emissive is the default here rather than BaseColor because a chest is meant to catch your eye from altitude, not to change colour. Ignored in Sequencer mode.");
            ChestGlowProperty = cfg.Bind("6. Chest glow", "ChestGlowProperty", "_BlinkColor",
                "Sequencer route only: which material channel carries the tint, from ProjectM.Sequencer.SupportedDotsProperty: _BlinkColor, _DissolveColor, _AlphaMultiply, _DitherAlpha, _DissolveHeightMultiplier, _RustleForceVector, _RustleAnimationTime, _OverlappingAnimationTime. Only _BlinkColor and _DissolveColor carry a colour; the rest take a single number and will do something other than tint. The Static route ignores this setting and always writes ShaderProperty_BlinkColor, which is the override world chests actually carry.");
            ChestGlowImportance = cfg.Bind("6. Chest glow", "ChestGlowImportance", 0,
                "Sequencer route only: arbitration against the game's own uses of the channel named in ChestGlowProperty. Higher should win.");
            ChestDumpFilter = cfg.Bind("6. Chest glow", "ChestDumpFilter", "Chest,Container",
                "Used ONLY by the dump key, and deliberately wider than ChestNameContains: this is how you discover what the prefabs on your build are actually called before you commit to a filter. Every entity whose prefab name matches is printed with its name, raw GUID, distance, whether it is looted, whether your current ChestNameContains selects it, and how it is drawn - children=8/8 means eight static render children and every one of them tintable. Entities whose prefab name cannot be resolved at all are listed too when they are within 15m of you, capped at 25 lines, so the dump still tells you something in the one state it most needs to. Widen to 'TM_' if nothing shows up.");
        }
    }
}
