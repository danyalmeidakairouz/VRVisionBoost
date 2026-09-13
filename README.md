# VRVisionBoost

A BepInEx 6 (IL2CPP) client plugin for V Rising that does four independent things:

1. **Keeps units visible further away.** Mobs, VBloods and critters normally wink out at the
   edge of the game's fog of war. This pushes that cutoff back so they stay drawn out to roughly
   the distance the server actually streams them — about 90 m.
2. **Tints units by blood quality.** A 100 % feed target glows magenta, anything from 90 % up
   glows green, so you can pick the good one out of a cell block at a glance instead of clicking
   through each prisoner.
3. **Clears the fog and clouds in bat form.** Flying is normally a view of grey soup; this
   switches off the game's own `BatFormFog` screen effect and, optionally, the cloud cover
   underneath it, so you can see the ground you are flying over.
4. **Makes world chests glow.** Tints world chests by type so a gold one stands out from the
   surrounding clutter — and, by default, only while it still has loot in it.

The four features are separate and separately toggled. None synthesizes input: the plugin only
reads and writes ECS component and render state, and reads key state.

## The one limit you cannot tune away

**This plugin only affects entities the server has already sent you.** Which entities those are
is decided server-side — the `SyncToUser` bitmask machinery, `ProjectM.Network.PrioritizeSystem`,
`UserActivityGridSystem`, `CalculateRelevantAabbs` — in a **separate process**,
`VRising_Server\VRisingServer.exe`, even when you "play solo". A client-side plugin cannot reach
into it.

This limit applies to the vision half only. The blood glow and the bat form fog work regardless
of what the server streams.

In practice that ceiling is generous for units: they arrive with the world chunks and stay
available past 90 m, which is well beyond the ~40 m the game draws them at. That gap is exactly
what this plugin recovers.

If something you expected is missing, press the dump key before assuming a setting is broken. It
prints every unit the client is actually holding and how far away the farthest one is. That
number is your ceiling.

This is not a guess about the architecture. V Rising does **not** use Unity NetCode for Entities
— there is no `Unity.NetCode.dll` in the interop set. Replication is Stunlock's own snapshot
system (`ProjectM.GeneratedNetCode.dll`, `Stunlock.Network.*`, `Lidgren.Network`), and it is
server-authoritative.

## Install

**Prerequisites.** BepInEx **6.x for IL2CPP** must already be installed and working — launch the
game once and confirm `BepInEx/LogOutput.log` exists. BepInEx 5, or the Mono build of 6, will not
load this plugin: it derives from `BasePlugin` and registers its component through
Il2CppInterop, neither of which exists there. Building also needs the **.NET SDK** (6.0 or newer;
the project targets `net6.0` to match the CoreCLR runtime the game loads).

### From a prebuilt DLL

This is the quickest route and needs no .NET SDK — only BepInEx.

1. **Get the DLL.** Either download `VRVisionBoost.dll` from the
   [latest release](https://github.com/danyalmeidakairouz/VRVisionBoost/releases/latest), or take
   the copy committed at [`dist/VRVisionBoost.dll`](dist/VRVisionBoost.dll) in this repo. They are
   the same file.
2. **Drop it in.** Copy it into `<game>\BepInEx\plugins\`, so you end up with
   `...\VRising\BepInEx\plugins\VRVisionBoost.dll`. No subfolder — BepInEx does not recurse by
   default.
3. **Launch the game once.** That creates the config file at:

   ```
   <game>\BepInEx\config\vrvisionboost.cfg
   ```

   `<game>` is the folder holding `VRising.exe` — on a default Steam install,
   `C:\Program Files (x86)\Steam\steamapps\common\VRising`. BepInEx creates the `config\` folder
   itself, so it will not exist until the first launch with a plugin present.
4. **Confirm it loaded.** Open `BepInEx/LogOutput.log` and look for:

   ```
   [Info   :   BepInEx] Loading [VRVisionBoost 1.0.2]
   [Info   :VRVisionBoost] Hotkeys - vision: F6  glow: F7  batfog: F8  dump: F10  reload: F11
   ```

   If those lines are missing, the DLL is in the wrong folder or BepInEx is not the IL2CPP
   build — see **Prerequisites** above.
5. **Turn it on.** The plugin starts **inactive** unless `Enabled = true` in the config. **F6 is
   the master switch** — nothing works until it is on. Then **F7** adds the blood glow and **F8**
   clears the bat form fog.

To configure it, edit the generated `.cfg` and press **F11** in game — no restart needed. For a
tuned starting point instead of bare defaults, see **Example config** below.

**To update**, replace the DLL with the newer one and relaunch. Your config is kept; new settings
are appended to it with their defaults.

**To uninstall**, delete `BepInEx/plugins/VRVisionBoost.dll`. Nothing it writes outlives the
process, so no cleanup is needed beyond deleting `BepInEx/config/vrvisionboost.cfg` if you want
the settings gone too.

### From source

```powershell
dotnet build -c Release -p:VRisingDir="D:\Steam\steamapps\common\VRising"
```

Pass `VRisingDir` explicitly — every assembly reference resolves from it, and the project's
default points at `C:\Program Files (x86)\Steam\...`, which is wrong on most installs. The
`CopyToBepInEx` target then drops the DLL straight into `BepInEx/plugins/`.

To compile without deploying — safe while the game is running, and the right call for a
check-only build:

```powershell
dotnet build -c Release -p:VRisingDir="D:\Steam\steamapps\common\VRising" -p:CopyToPlugins=false
```

**Windows locks a loaded DLL**, so a deploying build fails while the game is open. Close it
first, or build with `-p:CopyToPlugins=false` and copy afterwards.

### After a game update

`BepInEx/interop/` holds the generated proxy assemblies this plugin compiles against, guarded by
`interop/assembly-hash.txt`. A patch changes `GameAssembly.dll`, invalidates that hash and
regenerates every proxy — so after any update, rebuild before trusting anything, and re-read the
log for layout warnings.

## Example config

[`vrvisionboost.example.cfg`](vrvisionboost.example.cfg) is a working configuration — a verbatim
copy of a generated file, carrying tuned values rather than bare defaults.

To use it, save it here, **renamed**:

```
<game>\BepInEx\config\vrvisionboost.cfg
```

The filename matters: BepInEx looks the config up by the plugin's GUID (`vrvisionboost`) and will
ignore a file called anything else, silently generating a fresh one at defaults beside it. Create
the `config\` folder yourself if the game has not been launched with a plugin installed yet.

Do this **before** launching and the plugin picks it up on first run. If you have already
launched, overwrite the generated file — either quit first, or replace it while running and press
**F11** to reload.

It is a **snapshot**, not a source of truth. The plugin rewrites the live config's `##`
descriptions and `# Default value:` lines from `Settings.cs` whenever it binds a setting, so if
the two ever disagree, `Settings.cs` is right.

## Hotkeys

| Key | Does |
|-----|------|
| **F6** | **Master switch.** Toggles the whole plugin on/off — vision, blood glow, bat form fog and chest glow together — and **undoes** everything all four wrote. The other keys do nothing while this is off |
| **F7** | Toggle the blood glow on/off, independently of the other features. Saves the new state, so it survives a restart. Requires F6 to be on |
| **F8** | Toggle the bat form fog removal on/off, independently of the other features. Saves the new state, so it survives a restart. Requires F6 to be on |
| **F9** | Toggle the chest glow on/off, independently of the other features. Saves the new state, so it survives a restart. Requires F6 to be on |
| **F10** | Dump every unit the client knows about: distance, hide state, blood quality, whether the glow selected it. Also lists world chests with their prefab names and whether either tint route can reach them. Also prints the full component list — and a glow diagnosis — for anything within `DumpComponentsRange` |
| **F11** | Reload the config file |

> **Check for hotkey collisions.** A plugin cannot see another plugin's bindings, so if something
> else you have installed uses the same key, one press triggers both. Rebind whichever is easier.

All six are configurable, but **single keys only** — `F1`-`F24`, `A`-`Z`, `0`-`9`, `Space`,
`Insert`, `Numpad0`-`Numpad9` and similar. Modifier combinations like `Ctrl+F6` are not
supported; if you configure one the plugin warns in the log and falls back to the default. The
log always names the key that was actually bound. Hotkeys are ignored unless the V Rising window
has focus.

## Config

`BepInEx/config/vrvisionboost.cfg`. Every value is read live on each tick, so F11 applies
everything immediately — no rebuild, no restart.

| Key | Default | Does |
|-----|---------|------|
| `Enabled` | `false` | Start with the boost active. Off by default; toggle with F6. |
| `IntervalMs` | `100` | How often the components are rewritten. The game recomputes every frame, so this has to keep up. Lower is smoother, higher is cheaper. |
| `VisionRange` | `120` | Your own vision radius in meters. `0` leaves your vision alone and only un-hides the units around you. |
| `RevealAllUnits` | `true` | Un-hide mobs, VBloods and critters. The plugin's actual purpose. |
| `SeeThroughWalls` | `false` | Drop the line-of-sight test (`Hideable.IgnoreLoS`) so units stay visible through walls and terrain. Kept separate from distance and off by default. |
| `HudDistance` | `0` | Also raise the HUD/fade-out distance so nameplates and health bars do not cut off early. `0` leaves the HUD alone. Defaults to 20 m in game. |
| `VisibleFromFlight` | `true` | **Required to see anything in bat form.** Tags revealed units with the game's own `VisibleFromFlight` component. See below. |
| `KeepModelsLoaded` | `false` | Keep revealed units' visual models from being unloaded (see below). Costs memory. |
| `ModelShowRange` | `0` | `0` = leave alone. Radius in meters inside which unit models are instantiated at all. Try 80-150 if revealed units stay invisible at distance. |
| `DumpComponentsRange` | `3` | On F10, also print the full component list of every unit within this many meters. `0` disables it. Keep it small — each unit prints well over a hundred names. |

Hotkeys are `ToggleKey`, `GlowToggleKey`, `DumpKey` and `ReloadKey` in `[1. General]`.

### Flying is a separate gate from distance

`VisibilitySystem_Client` takes a flying flag for the viewer and reads a
`ProjectM.VisibleFromFlight` tag on each target. **While you fly, an entity without that tag is
hidden at any distance** — measured in bat form, a unit 7 m horizontally below read `hidden=True`
while 69 units were streamed and the farthest sat at 129 m. Widening the distance check does
nothing against this; it is a different condition entirely.

Almost nothing carries the tag natively (only the Manticore variants do), so `VisibleFromFlight`
adds the game's own tag to the units the plugin reveals, and removes exactly those again when you
toggle off. It is not a wallhack — line of sight and stealth are still evaluated normally.

The F10 dump prints `+flightTag` or `NO-FLIGHT-TAG` per unit. A dump taken airborne that is all
`hidden=True NO-FLIGHT-TAG` means this setting is off, not that something is out of range.

## Blood glow

Tints units by **blood quality** so a good feed target is findable at a glance. Toggle it with
**F7**, independently of the vision boost.

| Key | Default | Does |
|-----|---------|------|
| `GlowEnabled` | `false` | Master switch. F7 flips this and saves it. |
| `GlowHighMin` | `90` | Blood quality at or above which a unit is tinted `GlowHighColor`. |
| `GlowPerfectMin` | `100` | Blood quality at or above which it gets `GlowPerfectColor` instead. Checked **first**, so it wins over `GlowHighMin`. |
| `GlowHighColor` | `0,1,0` | Colour for the `GlowHighMin` band, `r,g,b` in the 0-1 range. Green. |
| `GlowPerfectColor` | `1,0,1` | Colour for the `GlowPerfectMin` band. Magenta. |
| `GlowIntensity` | `1` | Multiplier on the colour. Above 1 oversaturates toward flat colour; below 1 is a subtler sheen. |
| `GlowMode` | `Both` | Which write route to use — see below. `Both` is strongly recommended. |
| `GlowRendererProperty` | `BaseColor` | Renderer route: `BaseColor` repaints the albedo (a strong tint), `EmissiveColor` raises brightness — which only becomes a *halo* if `BloomQuality > 0` in the game's graphics settings. With bloom off, emissive just looks brighter. |
| `GlowProperty` | `_BlinkColor` | Sequencer route: which `SupportedDotsProperty` channel to drive. `_BlinkColor` is the game's own hit-flash channel. |
| `GlowImportance` | `0` | Sequencer route: arbitration against the game's own writes to that channel. |
| `GlowScanMs` | `500` | How often blood quality is rescanned. In Sequencer mode the tint is re-emitted every frame regardless; this only sets how quickly a newly streamed unit starts glowing. |
| `GlowUseBloodComponent` | `true` | Also scan `ProjectM.Blood`, which catches servants and castle units that show a quality in the UI but carry no `BloodConsumeSource`. |
| `GlowQualityScale` | `Auto` | How to read the raw number. Measured on this build the scale is 0-100, so `Percent` is the safe pin. |

### Why there are two routes

Units come in **two rendering flavours**, and each route only reaches one of them. The deciding
field is `ProjectM.Hybrid.HybridModelUser.ModelType`:

- **`GameObject`** — a real Unity object exists and appears in
  `HybridModelSystem.GetEntityToGameObjectMap()`. The **Renderer** route paints its renderers
  directly with a `MaterialPropertyBlock`.
- **`Rukhanka`** — the unit is GPU-skinned through DOTS. There is **no GameObject at all**, it is
  in no map under any key, and the Renderer route can never touch it. Only the **Sequencer**
  route reaches these.

This is the whole reason "some units glow and others do not" was the symptom for so long, and it
is why `GlowMode = Both` is the default: either route alone leaves half your prisoners untinted.

### Reading the log

Each rescan prints one line, but only when the picture changes:

```
Blood glow [Both]: targets=11 painted=20 via=character noGameObject=5 (rukhanka=5) emit=ok
```

`targets` were selected, `painted` renderers were written by the Renderer route, `emit=ok` means
the Sequencer route's `AddChange` calls were all accepted. A non-zero `rukhanka=` is not a fault
— those units are the Sequencer route's job. `emit=ALL-THREW` is a fault.

Standing within `DumpComponentsRange` of a unit and pressing **F10** adds a per-unit diagnosis
naming its model entity, its `ModelType`, which lookup key resolved it, each renderer's shader,
and whether that shader actually declares the colour property being written.

## Bat form fog

Press **F8** in flight. Two separate things make bat form look like grey soup, and the plugin
treats them separately because one of them is not really a bat form setting at all:

| What | Setting | Scope |
|------|---------|-------|
| `BatFormFog`, a full-screen effect the game ships for exactly this purpose | `BatFogEnabled` | bat form only |
| `DayNightCycle.Cloudiness` — real cloud cover, with ground shadows, normally `0.65` | `ZeroCloudiness` | **the whole world, all the time** |

Removing only the first leaves clouds still drifting below you, which is why `ZeroCloudiness`
defaults to on. But be clear about what it does: it is **global weather**, so while the toggle is
on the sky is clear everywhere, not just while you are flying. Set it to `false` to keep the
weather untouched and strip only the screen effect. Both are restored when you toggle off, when
you switch the setting off mid-session, and on shutdown.

**If nothing happens the first time**, look in the log. The game may not create the effect until
you have entered bat form once, and the plugin says so explicitly rather than failing quietly:

```
Bat form fog is ON but no BatFormFog effect was found yet - the game may not create it
until you first enter bat form. Shapeshift once, then check this log again.
```

Shapeshift, and it should pick it up within two seconds. Once it has, the log names how many
instances it found and how it found them.

**Prior art.** [RetroCamera](https://thunderstore.io/c/v-rising/p/zfolmt/RetroCamera/) has done
this for a while and does it well; if you also want its camera changes, use it instead — this is
not an attempt to replace it. The implementation here is independent and deliberately narrower:
it switches the effect off through its `active` flag rather than destroying the material, and it
does not touch the camera at all.

## Chest glow

Press **F9**. World chests get tinted by type, so a gold one stands out — and by default only
while it still has loot in it.

The plugin identifies them by **prefab name**, asked of the running game rather than hardcoded,
so a patch that renumbers prefab GUIDs changes nothing:

| Prefab | What it is |
|--------|------------|
| `TM_WorldChest_Epic_01_Full` / `_Empty` | the gold-trimmed one |
| `TM_WorldChest_Iron_01_Full` / `_Empty` | iron |
| `TM_WorldChest_Simple_01_Full` / `_Empty` | plain |
| `TM_WorldChest_Simple_GloomRot_01_Full` / `_Empty` | plain, Gloomrot |
| `TM_WorldChest_Simple_SludgePools_01_Full` / `_Empty` | plain, Sludge Pools |

Two things fall out of that naming, and both are worth more than the tint itself:

- **`ChestNameContains` defaults to `WorldChest_Epic`**, so out of the box only gold chests light
  up. Widen it to `WorldChest` for all of them, or add `Container` to catch castle furniture. It
  takes a comma-separated list of name substrings — or raw prefab GUIDs, which matters only if
  name lookup ever fails on your build.
- **`_Full` versus `_Empty` is in the prefab name**, so with `ChestUnlootedOnly` on (the default)
  a chest stops glowing within a second of you emptying it. That is the difference between a
  marker and a to-do list.

| Key | Default | Does |
|-----|---------|------|
| `ChestGlowEnabled` | `false` | Master switch for this feature. F9 flips and saves it |
| `ChestNameContains` | `WorldChest_Epic` | Which prefabs count as a chest. Name substrings or raw GUIDs, comma separated. Blank matches **nothing** |
| `ChestUnlootedOnly` | `true` | Glow only chests that still have loot |
| `ChestColor` | `1,0.84,0` | Colour for an unlooted chest — gold |
| `ChestEmptyColor` | `0.35,0.3,0.15` | Colour for a looted one. Only used when `ChestUnlootedOnly` is off |
| `ChestGlowIntensity` | `1` | Multiplier on the colour. Try 0.3–2 |
| `ChestGlowRange` | `0` | `0` = every chest the client knows about; otherwise a radius in metres |
| `ChestScanMs` | `1000` | How often the chest list is rebuilt. Slower than the blood glow's scan because chests do not move |
| `ChestGlowMode` | `Both` | `Static`, `Renderer`, `Sequencer` or `Both` — see below |
| `ChestRendererProperty` | `EmissiveColor` | Renderer route only: `EmissiveColor` or `BaseColor` |
| `ChestGlowProperty` | `_BlinkColor` | The material channel the tint is written to |
| `ChestGlowImportance` | `0` | Sequencer route only: arbitration against the game's own use of that channel |
| `ChestDumpFilter` | `Chest,Container` | Used **only** by F10, and deliberately wider — this is how you discover the prefab names on your build |

### Three routes, because containers are drawn three different ways

A world chest's gameplay entity carries **no rendering components at all** — no `RenderBounds`,
no `MaterialMeshInfo`, no `HybridModelUser`. Its visuals live on `StaticHierarchyBuffer`
children, each a real Entities Graphics renderable carrying the game's own
`ShaderProperty_BlinkColor` override. So the geometry is not merged into a combined static batch,
and each piece is individually addressable.

| Route | Reaches | How |
|-------|---------|-----|
| **Static** | world chests | writes `ShaderProperty_BlinkColor` on each render child |
| Renderer | some castle props | writes `MaterialPropertyBlock`s on the model's renderers |
| Sequencer | GPU-skinned (Rukhanka) props | hands a change to `MaterialPropertySystem_Dots` |

`Both` runs all three, which is why some containers tint and others do not when you widen the
filter — that is the rendering flavour, not a bug. The log names the route that reached each one.

Everything the static and renderer routes write is captured before the first write and restored
on rescan, on F9 off, on F6 off and on shutdown. The sequencer route self-reverts by definition:
ceasing to emit *is* the revert.

### You cannot spot them through a roof

The glow tints the chest's own geometry, so it makes a chest that **is** being drawn obvious. It
cannot make one that is not being drawn appear. Three things stop it, and none is fixable by
tinting harder:

- **Occlusion.** A tent or roof in front of the chest is opaque geometry and wins the depth test.
- **Culling.** Those children carry `PerInstanceCullingTag`; a small object at altitude is culled
  or LOD'd out before distance alone would hide it.
- **Streaming.** Chests stop arriving at roughly 70–75 m, measured — shorter than the ~90 m for
  units. Nothing client-side exceeds that.

`SeeThroughWalls` does **not** help here. It writes `Hideable.IgnoreLoS`, and world chests carry
no `Hideable` at all; it also only stops the game *hiding* things, it never makes geometry
transparent.

### Reading the log

Same discipline as the blood glow — a counter never reports work that was suppressed:

```
Chest glow [Both]: targets=1 staticChildren=8 noHierarchy=0 noTintableChild=0
painted=0 viaRendererComp=0 viaPlainRenderers=0 noGameObject=0 (rukhanka=0)
noRenderers=0 noShaderProp=0 applyThrew=0 sharedModel=0 emit=idle
```

- `staticChildren=8` is the success signal for a world chest: eight render children written.
- `targets=1 noTintableChild=1` means the chest was selected and its hierarchy read, but nothing
  on it took a colour.
- `childWriteFailed` non-zero means the layout check disabled the write route; there will be a
  `LAYOUT MISMATCH` error above it naming the two sizes.
- `noShaderProp` counts renderers whose shader does not declare the property being written —
  writing a property a shader does not have is silent, so without this a run where every chest
  ignored the tint would look identical to one that worked.
- `names=Nfail` only appears when a prefab name could not be resolved.

Press **F10** for the per-chest breakdown: prefab name, raw GUID, distance, whether your filter
selects it, whether it is looted, and `children=8/8` for how much of it is tintable.

## How it works

The plugin writes the *inputs* of the game's own visibility system rather than fighting its
outputs. `ProjectM.VisibilitySystem_Client` recomputes visibility every frame for everything
carrying `ProjectM.Hideable`, so anything written as an output gets overwritten immediately;
inputs are read and never written back, which is what makes them stick.

| Component | Field | Why |
|---|---|---|
| `ProjectM.Hideable` | `AdditionalHideRangeSq` | **Input, and the only one needed to see further.** Squared distance; set large so the hide check never trips. |
| `ProjectM.Hideable` | `IgnoreLoS` | **Input, separate concern.** Drops the line-of-sight test. Written only when `SeeThroughWalls` is on. |
| `ProjectM.Hideable` | `IsHidden`, `Visibility` | Outputs, forced only when `SeeThroughWalls` is on. With it off the game owns them, and forcing them anyway would flicker against it every tick. |
| `ProjectM.Vision` | `Range._Value` | Your fog-of-war radius. Rewritten every tick, so a buff that overwrites it cannot silently undo the setting. |
| `ProjectM.CheckOnScreen` | `MaxDistanceForHudAndFadeOut`, `IgnoreLineOfSight` | Optional; stops nameplates and the distance fade from cutting off early. |
| `ProjectM.Hybrid.HybridModelUser` | `TimeSinceLastSeen` | Optional (`KeepModelsLoaded`); stops the model being unloaded. |

Because none of those inputs revert on their own, the plugin captures each entity's original
values before its first write and puts them back on toggle-off, config change and shutdown.

### The second gate: model streaming

Un-hiding a unit is not always enough. Its *visual model* is tracked separately by
`ProjectM.Hybrid.HybridModelUser`, and the game unloads it once `TimeSinceLastSeen` gets large or
the unit falls outside `HybridModelSystem.SHOW_INSIDE_RANGE_SQ`. So a unit can be perfectly
un-hidden and still render as nothing, because there is no model attached.

`KeepModelsLoaded` pins `TimeSinceLastSeen` to 0 for the entities being revealed; `ModelShowRange`
widens the instantiation radius. Both are **off by default** because they keep assets resident and
the memory cost was not measured. Turn them on only if the F10 dump shows a revealed unit with a
rising `lastSeen` and an empty `model=`. The dump reports both, so you can check rather than guess.

## Known limitations

- **The server's streaming radius is the ceiling.** Nothing here can exceed it. See above.
- **A disabled entity cannot be revealed.** If the game has switched a unit off
  (`Unity.Entities.Disabled`), no plain query selects it and neither the reveal nor the glow can
  reach it. The dump lists these separately so they are not mistaken for "never streamed".
- **A glowing chest still has to be drawn to be seen.** The tint colours the chest's own
  geometry, so a roof in front of it, distance culling, or the ~70–75 m streaming limit will all
  hide it regardless. See "You cannot spot them through a roof" above.
- **Not every container is tintable.** Containers are drawn three different ways and each route
  reaches one of them; widen `ChestNameContains` and some will light up while others do not. F10
  shows `children=N/M` and the log names the route that reached each one.
- **`Marshal.SizeOf` must match the component's real chunk size.** The plugin verifies this at
  runtime against `il2cpp_class_value_size` and disables its writes if they ever disagree, which
  is what stops a regenerated interop set from silently corrupting adjacent component data. A
  failed check is logged loudly; writes being disabled is never silent.

## Links

- **Repository:** <https://github.com/danyalmeidakairouz/VRVisionBoost>
- **Releases:** <https://github.com/danyalmeidakairouz/VRVisionBoost/releases>
- **Issues:** <https://github.com/danyalmeidakairouz/VRVisionBoost/issues> — include the relevant
  lines from `BepInEx/LogOutput.log` and your `vrvisionboost.cfg`; the log names what the plugin
  actually found and is usually enough to diagnose a problem without a back-and-forth.

Bat form fog removal is independent of [RetroCamera](https://thunderstore.io/c/v-rising/p/zfolmt/RetroCamera/),
which also offers a fog toggle alongside its camera changes. Use whichever suits you; there is no
need for both.
