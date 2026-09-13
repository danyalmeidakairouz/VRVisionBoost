using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using ProjectM;
using ProjectM.Hybrid;
using ProjectM.Sequencer;

namespace VRVisionBoost
{
    /// <summary>
    /// Tints units by blood quality so a good feed target is findable at a glance.
    ///
    /// This drives the game's OWN tint channel rather than touching materials. The sequencer's
    /// MaterialPropertySystem_Hybrid exposes a public AddChange(Entity, importance, ref
    /// ChangeData), and SupportedDotsProperty._BlinkColor is the float4 the game itself flashes
    /// a unit with when it takes a hit. Held steady instead of flashed, that is a glow.
    ///
    /// Nothing here needs a revert path, and that is a property of the engine rather than luck.
    /// Neither MaterialPropertySystem has any Clear/Reset/Remove method, so "this effect ended"
    /// can only be expressed by no longer calling AddChange - which is also how every hit flash
    /// and dissolve in the game terminates, thousands of times a session. UsedThisFrameData even
    /// carries a NeedsBlinkCleanup flag. So: emit every frame to hold a tint, stop emitting to
    /// drop it. Do NOT try to "clear" a tint by writing a zero float4 - zero is a colour, not an
    /// absence, and the engine has no API meaning what that would be trying to say.
    ///
    /// It must run EVERY FRAME, not on the config interval: the change list is drained and
    /// rebuilt per frame, so emitting at 10Hz would strobe. The expensive half (scanning blood
    /// quality) runs on the interval and caches; the per-frame half is only the emit loop.
    /// </summary>
    internal sealed class BloodGlow
    {
        private const float AttachRetrySeconds = 5f;

        private readonly Settings _cfg;
        private readonly ManualLogSource _log;

        private World _client;
        private MaterialPropertySystem_Hybrid _matProp;
        private MaterialPropertySystem_Dots _matPropDots;
        private HybridModelSystem _hybridModels;

        // Renderers this plugin has written, with the property block each had BEFORE we touched
        // it. Unlike the sequencer channel, a MaterialPropertyBlock persists until something
        // overwrites it - so here the project's usual rule applies in full: assume nothing
        // self-reverts, capture the original, put it back.
        private readonly List<UnityEngine.Renderer> _touched = new List<UnityEngine.Renderer>();
        private readonly List<MaterialPropertyBlock> _touchedOriginal = new List<MaterialPropertyBlock>();
        private MaterialPropertyBlock _scratch;
        private EntityQuery _bloodQuery;
        private EntityQuery _bloodCompQuery;
        private bool _ready;
        private bool _queryBuilt;
        private float _nextAttachAttempt;
        private float _nextScan;
        private bool _layoutOk;
        private bool _reportPending = true;

        // Entities to hold a tint on, rebuilt on the scan interval and emitted every frame.
        private readonly List<Entity> _targets = new List<Entity>();
        private readonly List<float4> _colors = new List<float4>();

        // Why a selected unit produced no pixel. Every drop below used to be a bare `continue`
        // or a silent `catch {}`, so a run in which EVERY write failed logged exactly the same
        // cheerful "12 units glowing" as a run in which they all worked. That is the worst
        // property a diagnostic channel can have, and this plugin already has a rule about it
        // ("Never let a counter report suppressed work"). These make the failures countable;
        // _lastDrops keeps the log silent while nothing changes.
        private int _dropNoGameObject, _dropNoRendererComp, _dropNoRenderers, _dropApplyThrew;
        private int _dropRukhanka;
        private int _dropSelectThrew;
        private int _paintedRenderers;
        private int _emitOk, _emitDead, _emitThrewHybrid, _emitThrewDots;
        private string _lastDrops = "";
        private string _resolvedVia = "none";

        public BloodGlow(Settings cfg, ManualLogSource log) { _cfg = cfg; _log = log; }

        /// <summary>
        /// Is this entity currently being driven with a tint? For the dump, so a unit that is not
        /// glowing can be told apart three ways: not selected (a data/threshold problem), selected
        /// but with no model (a visibility problem), or selected with a model (a render problem).
        /// </summary>
        public bool IsTarget(Entity e) => _targets.Contains(e);

        public int TargetCount => _targets.Count;

        /// <summary>Drop every tint. Clearing the list IS the revert - see the class comment.</summary>
        public void Reset()
        {
            RestoreRenderers();
            _targets.Clear();
            _colors.Clear();
            _reportPending = true;
        }

        public void Update()
        {
            if (!_cfg.GlowEnabled.Value)
            {
                if (_targets.Count > 0) Reset();
                return;
            }
            if (!Ensure()) return;

            float now = Time.unscaledTime;
            if (now >= _nextScan)
            {
                _nextScan = now + Mathf.Max(_cfg.GlowScanMs.Value, 50f) / 1000f;
                Rescan();
            }
            Emit();
        }

        /// <summary>Rebuild the target list. Interval work, not per-frame work.</summary>
        private void Rescan()
        {
            // Tear down before rebuilding: a unit that has dropped out of the set, or whose
            // model was destroyed, must get its original block back or the tint outlives its
            // reason for existing.
            RestoreRenderers();

            _targets.Clear();
            _colors.Clear();
            if (!_layoutOk) return;

            float hiMin = _cfg.GlowHighMin.Value;
            float perfectMin = _cfg.GlowPerfectMin.Value;
            float4 hiColor = ParseColor(_cfg.GlowHighColor.Value, new float4(0f, 1f, 0f, 1f));
            float4 perfectColor = ParseColor(_cfg.GlowPerfectColor.Value, new float4(1f, 0f, 1f, 1f));
            float intensity = Mathf.Max(_cfg.GlowIntensity.Value, 0f);

            var em = _client.EntityManager;
            int scanned = 0;
            var ents = _bloodQuery.ToEntityArray(Allocator.Temp);
            try
            {
                scanned = ents.Length;
                for (int i = 0; i < ents.Length; i++)
                {
                    var e = ents[i];
                    // Select() is INSIDE the try. It used to sit after it, so a throw from it -
                    // it reads HybridModelUser - escaped Rescan entirely and skipped
                    // ApplyRenderers() and ReportDrops(), the same failure the
                    // GlowUseBloodComponent early return caused, through a different door. One
                    // unlucky entity would silently cost the whole scan.
                    try
                    {
                        float pct = Percent(Read<BloodConsumeSource>(em, e).BloodQuality);
                        Select(em, e, pct, hiMin, perfectMin, hiColor, perfectColor, intensity);
                    }
                    catch { _dropSelectThrew++; }
                }
            }
            finally { ents.Dispose(); }

            // Second source. Servants and some castle units show a blood quality in the UI while
            // carrying no BloodConsumeSource at all, so a query over that component alone can
            // never see them. Skip anything already covered above, and skip players - they carry
            // Blood too, and lighting up your own character is not the feature.
            // Skipped as a BLOCK, not with an early return. This used to be
            // `if (!GlowUseBloodComponent) return;`, which also skipped ApplyRenderers() and
            // ReportDrops() below - so switching off a setting that is only supposed to narrow
            // the second blood source silently disabled the entire renderer route and the log
            // line that would have said so. Turning a knob off must never skip the apply phase.
            if (_cfg.GlowUseBloodComponent.Value)
            {
                var ents2 = _bloodCompQuery.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < ents2.Length; i++)
                    {
                        var e = ents2[i];
                        try
                        {
                            if (Has<PlayerCharacter>(em, e) || Has<BloodConsumeSource>(em, e)) continue;
                            float pct = Percent(Read<Blood>(em, e).Quality);
                            Select(em, e, pct, hiMin, perfectMin, hiColor, perfectColor, intensity);
                        }
                        catch { _dropSelectThrew++; }
                    }
                    scanned += ents2.Length;
                }
                finally { ents2.Dispose(); }
            }

            // Zeroed HERE, not inside ApplyRenderers: that method early-returns when the renderer
            // route is off, so resetting inside it would leave the previous mode's numbers frozen
            // in the report forever after a Both -> Sequencer switch - a stale line claiming work
            // that is no longer happening.
            _dropNoGameObject = _dropNoRendererComp = _dropNoRenderers = _dropApplyThrew = 0;
            _paintedRenderers = 0;
            _dropRukhanka = 0;
            _dropSelectThrew = 0;
            _resolvedVia = "none";

            ApplyRenderers();
            ReportDrops();

            if (_reportPending)
            {
                _reportPending = false;
                _log.LogInfo($"Blood glow on: {_targets.Count} of {scanned} scanned units are at "
                           + $"or above {hiMin}% AND have a model to tint. Units the game has "
                           + "hidden have no model and are skipped - emitting at them cannot "
                           + $"produce a pixel. Rescanning every {_cfg.GlowScanMs.Value}ms, "
                           + "emitting every frame.");
            }
        }

        /// <summary>
        /// One line, only when the picture changes, saying what became of the selected units.
        /// Emit counts are sums since the previous report - Emit runs every frame, so they are
        /// per-scan totals, not per-frame. "painted" is renderers the renderer route wrote;
        /// "emitOk" is AddChange calls the sequencer route accepted without throwing.
        /// </summary>
        private void ReportDrops()
        {
            // Emit runs per frame, so the raw counts are targets x frames-since-last-scan - they
            // drift by a few every scan and would defeat the change-detection below, printing a
            // near-identical line twice a second forever. Collapse them to the state they are
            // actually evidence of; a count that only moves when something breaks stays a count.
            string emit = _emitOk > 0 && _emitThrewHybrid == 0 ? "ok"
                        : _emitOk > 0 ? "partial"
                        : _emitThrewHybrid > 0 ? "ALL-THREW"
                        : "idle";
            string s = $"targets={_targets.Count} painted={_paintedRenderers} via={_resolvedVia} "
                     + $"noGameObject={_dropNoGameObject} (rukhanka={_dropRukhanka}) "
                     + $"noRendererComp={_dropNoRendererComp} "
                     + $"noRenderers={_dropNoRenderers} applyThrew={_dropApplyThrew} "
                     + (_dropSelectThrew > 0 ? $"selectThrew={_dropSelectThrew} " : "")
                     + $"emit={emit}"
                     + (_emitDead > 0 ? " emitDead=some" : "")
                     + (_emitThrewDots > 0 ? " dotsThrew=some" : "");
            _emitOk = _emitDead = _emitThrewHybrid = _emitThrewDots = 0;
            if (string.Equals(s, _lastDrops, StringComparison.Ordinal)) return;
            _lastDrops = s;
            _log.LogInfo($"Blood glow [{_cfg.GlowMode.Value}]: {s}");
        }

        /// <summary>
        /// Answer "why is this unit not the colour I asked for" from the renderer itself rather
        /// than from theory. Prints the model behind the entity, the renderers on it, the shader
        /// each one uses, whether that shader actually declares the channel being written, and
        /// what reads back out of the property block afterwards.
        ///
        /// The middle one is the point. HybridModelRendererComponent.BaseColor / .EmissiveColor
        /// are Shader.PropertyToID values baked by the game; nothing guarantees the shader on a
        /// given character declares them, and writing a property a shader does not have is
        /// SILENT - Unity stores it in the block and no pixel changes. Six explanations for a
        /// missing glow have already been disproven here; this one is measurable, so measure it.
        /// </summary>
        public void Probe(Entity e)
        {
            if (_hybridModels == null) { _log.LogInfo("    glow probe: not attached."); return; }

            int idx = _targets.IndexOf(e);
            string want = idx >= 0
                ? $"selected, want rgb=({_colors[idx].x:0.##},{_colors[idx].y:0.##},{_colors[idx].z:0.##})"
                : "NOT selected (blood below threshold, or no model at the last scan)";

            // What the game thinks this character's model is, printed before any lookup, because
            // the lookup KEY is the thing under suspicion and a miss is meaningless without it.
            var em = _client.EntityManager;
            string modelInfo = "no HybridModelUser";
            try
            {
                if (Has<HybridModelUser>(em, e))
                {
                    var hm = Read<HybridModelUser>(em, e);
                    modelInfo = $"modelEntity={hm.HybridEntity.Index}:{hm.HybridEntity.Version} "
                              + $"modelType={hm.ModelType}";
                }
            }
            catch (Exception ex) { modelInfo = $"HybridModelUser unreadable: {ex.Message}"; }

            string via;
            GameObject go = ResolveModel(em, e, out via);
            if (go == null)
            {
                _log.LogInfo($"    glow probe: {want}; {modelInfo}; found under NEITHER key in "
                           + "the entity->model map, so the renderer route has nothing to paint. "
                           + "Whether the sequencer route can still resolve it is not shown here "
                           + "- AddChange does its own lookup inside native code.");

                // The whole point of the next step: a Rukhanka unit draws from its MODEL entity's
                // archetype, so that archetype names the only components capable of tinting it.
                var model = ModelEntityOf(em, e);
                if (model != Entity.Null && em.Exists(model))
                    LogArchetype(em, model, "model entity archetype");
                return;
            }

            HybridModelRendererComponent hr = null;
            try { hr = go.GetComponentInChildren<HybridModelRendererComponent>(); } catch { }
            if (hr == null)
            {
                _log.LogInfo($"    glow probe: {want}; {modelInfo}; resolved via {via} key, but "
                           + $"model '{SafeName(go)}' carries no HybridModelRendererComponent.");
                return;
            }

            var list = new Il2CppSystem.Collections.Generic.List<UnityEngine.Renderer>();
            try { hr.GetAllRenderers(list); }
            catch (Exception ex)
            {
                _log.LogInfo($"    glow probe: {want}; GetAllRenderers threw: {Describe(ex)}");
                return;
            }

            _log.LogInfo($"    glow probe: {want}; {modelInfo}; resolved via {via} key; "
                       + $"model='{SafeName(go)}' renderers={list.Count} "
                       + $"baseColorId={HybridModelRendererComponent.BaseColor} "
                       + $"emissiveColorId={HybridModelRendererComponent.EmissiveColor}");

            var block = new MaterialPropertyBlock();
            int shown = list.Count < 4 ? list.Count : 4;
            for (int r = 0; r < shown; r++)
            {
                var rend = list[r];
                if (rend == null) { _log.LogInfo($"      [{r}] <null renderer>"); continue; }
                string line = $"      [{r}] {SafeName(rend)}";
                try
                {
                    var mat = rend.sharedMaterial;
                    if (mat == null) line += " material=<none>";
                    else
                        line += $" shader='{(mat.shader == null ? "?" : mat.shader.name)}'"
                              + $" hasBaseColor={mat.HasProperty(HybridModelRendererComponent.BaseColor)}"
                              + $" hasEmissive={mat.HasProperty(HybridModelRendererComponent.EmissiveColor)}";
                }
                catch (Exception ex) { line += $" material=<threw: {ex.Message}>"; }
                try
                {
                    rend.GetPropertyBlock(block);
                    var b = block.GetColor(HybridModelRendererComponent.BaseColor);
                    var m = block.GetColor(HybridModelRendererComponent.EmissiveColor);
                    line += $" blockBase=({b.r:0.##},{b.g:0.##},{b.b:0.##},{b.a:0.##})"
                          + $" blockEmissive=({m.r:0.##},{m.g:0.##},{m.b:0.##},{m.a:0.##})";
                }
                catch (Exception ex) { line += $" block=<threw: {ex.Message}>"; }
                _log.LogInfo(line);
            }
            if (list.Count > shown)
                _log.LogInfo($"      ... {list.Count - shown} more renderers not shown");
        }

        private static string SafeName(UnityEngine.Object o)
        {
            try { return o.name; } catch { return "<name unreadable>"; }
        }

        private void Select(EntityManager em, Entity e, float pct, float hiMin, float perfectMin,
                            float4 hiColor, float4 perfectColor, float intensity)
        {
            float4 c;
            if (pct >= perfectMin) c = perfectColor;
            else if (pct >= hiMin) c = hiColor;
            else return;

            // A tint lands on the unit's MODEL. An entity the game has hidden has had its model
            // destroyed outright (HybridModelSystem's own list is named
            // entitiesToRemoveBecauseHidden), so emitting at it every frame is work that cannot
            // produce a pixel. Measured on a live dump: 13 of 14 selected units were in exactly
            // that state, so this drops roughly 93% of the per-frame interop calls.
            //
            // Checked here, on the scan interval, rather than per frame - the check itself costs
            // a component lookup, and doing it 150 times a second to save 10 calls would be worse
            // than the disease. Cost is up to GlowScanMs of lag before a unit whose model has just
            // streamed in starts glowing.
            if (!HasModel(em, e)) return;

            c.xyz *= intensity;
            _targets.Add(e);
            _colors.Add(c);
        }

        /// <summary>
        /// Find the GameObject for a character, trying both keys the map could use.
        ///
        /// The key was never the problem. Measured: `via=character noGameObject=5` with
        /// `painted=20` in the same scan - the character entity resolves fine for most units and
        /// misses for a few. What separates them is HybridModelUser.ModelType:
        ///
        ///   GameObject -> a real Unity GameObject exists, it is in this map, renderers paint.
        ///   Rukhanka   -> GPU-skinned through DOTS. There is NO GameObject and no entry in this
        ///                 map under any key, so the renderer route cannot reach it at all.
        ///
        /// Keep the model-entity fallback anyway: it costs one dictionary probe on a path that
        /// has already missed, and it rules the question out permanently instead of leaving a
        /// second untested hypothesis lying around.
        /// </summary>
        private GameObject ResolveModel(EntityManager em, Entity e, out string via)
        {
            via = "none";
            Il2CppSystem.Collections.Generic.Dictionary<Entity, GameObject> map;
            try { map = _hybridModels.GetEntityToGameObjectMap(); } catch { return null; }
            if (map == null) return null;

            GameObject go = null;
            try { if (map.TryGetValue(e, out go) && go != null) { via = "character"; return go; } }
            catch { }

            Entity model = Entity.Null;
            try { if (Has<HybridModelUser>(em, e)) model = Read<HybridModelUser>(em, e).HybridEntity; }
            catch { }
            if (model == Entity.Null) return null;

            try { if (map.TryGetValue(model, out go) && go != null) { via = "model"; return go; } }
            catch { }
            return null;
        }

        private static HybridModelType ModelTypeOf(EntityManager em, Entity e)
        {
            try
            {
                if (!Has<HybridModelUser>(em, e)) return HybridModelType.None;
                return Read<HybridModelUser>(em, e).ModelType;
            }
            catch { return HybridModelType.None; }
        }

        private static Entity ModelEntityOf(EntityManager em, Entity e)
        {
            try
            {
                if (!Has<HybridModelUser>(em, e)) return Entity.Null;
                return Read<HybridModelUser>(em, e).HybridEntity;
            }
            catch { return Entity.Null; }
        }

        /// <summary>
        /// Print an entity's full component list. For a Rukhanka unit the interesting archetype is
        /// the MODEL entity, not the character: the character carries gameplay data, the model
        /// entity carries whatever Entities Graphics uses to actually draw it. Whatever colour
        /// override exists for these units lives there, and this is how we find its name instead
        /// of guessing at one.
        /// </summary>
        private void LogArchetype(EntityManager em, Entity e, string label)
        {
            NativeArray<ComponentType> types;
            try { types = em.GetComponentTypes(e, Allocator.Temp); }
            catch (Exception ex) { _log.LogInfo($"      {label}: unreadable ({ex.Message})"); return; }
            try
            {
                var sb = new System.Text.StringBuilder($"      {label} [{e.Index}:{e.Version}] ({types.Length}): ");
                for (int t = 0; t < types.Length; t++)
                {
                    string n;
                    try { n = TypeManager.GetType(types[t].TypeIndex)?.FullName; }
                    catch { n = null; }
                    if (string.IsNullOrEmpty(n)) { try { n = types[t].ToString(); } catch { n = "?"; } }
                    sb.Append(n).Append(t + 1 < types.Length ? ", " : "");
                }
                _log.LogInfo(sb.ToString());
            }
            finally { types.Dispose(); }
        }

        private static bool HasModel(EntityManager em, Entity e)
        {
            try
            {
                if (!Has<HybridModelUser>(em, e)) return false;
                return Read<HybridModelUser>(em, e).HybridEntity != Entity.Null;
            }
            catch { return false; }
        }

        /// <summary>Hold the tint. Every frame - the game drains its change list per frame.</summary>
        /// <summary>
        /// Write the tint straight onto the model's renderers, bypassing the sequencer entirely.
        ///
        /// Entity -> GameObject comes from HybridModelSystem.GetEntityToGameObjectMap(), which the
        /// game exposes publicly. The model root carries HybridModelRendererComponent, whose
        /// GetAllRenderers hands back every renderer the character draws with.
        ///
        /// GetPropertyBlock BEFORE writing is not optional: SetPropertyBlock replaces a renderer's
        /// block wholesale, so a block containing only our colour would destroy that unit's alpha,
        /// dither, customization and transmog values. Copy what is there, add one key, put it back.
        ///
        /// Runs on the scan interval, not per frame: a property block STAYS until something
        /// overwrites it, so rewriting it 150 times a second would be pure waste. If the game does
        /// overwrite ours, the next scan restores it within GlowScanMs.
        /// </summary>
        private void ApplyRenderers()
        {
            if (!RendererMode || _hybridModels == null || _targets.Count == 0) return;
            if (_scratch == null) _scratch = new MaterialPropertyBlock();

            int propId = string.Equals(_cfg.GlowRendererProperty.Value, "BaseColor",
                                       StringComparison.OrdinalIgnoreCase)
                       ? HybridModelRendererComponent.BaseColor
                       : HybridModelRendererComponent.EmissiveColor;

            var em = _client.EntityManager;
            var renderers = new Il2CppSystem.Collections.Generic.List<UnityEngine.Renderer>();
            for (int i = 0; i < _targets.Count; i++)
            {
                string via;
                GameObject go = ResolveModel(em, _targets[i], out via);
                if (go == null)
                {
                    _dropNoGameObject++;
                    // Separate "no model yet" from "this unit is GPU-skinned and the renderer
                    // route can never touch it". The second is permanent and needs another route.
                    if (ModelTypeOf(em, _targets[i]) == HybridModelType.Rukhanka) _dropRukhanka++;
                    continue;
                }
                _resolvedVia = via;

                HybridModelRendererComponent hr = null;
                try { hr = go.GetComponentInChildren<HybridModelRendererComponent>(); }
                catch { }
                if (hr == null) { _dropNoRendererComp++; continue; }

                var c = _colors[i];
                var colour = new Color(c.x, c.y, c.z, 1f);
                try
                {
                    renderers.Clear();
                    hr.GetAllRenderers(renderers);
                    if (renderers.Count == 0) { _dropNoRenderers++; continue; }
                    for (int r = 0; r < renderers.Count; r++)
                    {
                        var rend = renderers[r];
                        if (rend == null) continue;

                        var original = new MaterialPropertyBlock();
                        rend.GetPropertyBlock(original);   // whatever the game last set
                        _touched.Add(rend);
                        _touchedOriginal.Add(original);

                        rend.GetPropertyBlock(_scratch);
                        _scratch.SetColor(propId, colour);
                        rend.SetPropertyBlock(_scratch);
                        _paintedRenderers++;
                    }
                }
                catch { _dropApplyThrew++; /* model torn down mid-walk; next scan retries */ }
            }
        }

        /// <summary>
        /// Put every renderer we wrote back exactly as we found it.
        ///
        /// Iterated BACKWARDS, and that is not cosmetic. Nothing stops the same renderer being
        /// recorded twice in one scan if two selected units ever resolve to one GameObject: the
        /// first entry holds the true original, and the second holds whatever GetPropertyBlock
        /// returned AFTER the first write - which is our own colour. Restoring forwards lets that
        /// second entry win and leaves the unit permanently tinted, with no record of the real
        /// original left to recover from - surviving Reset, both toggles and OnDestroy. Going
        /// backwards applies the earliest capture last, so the true original always wins.
        /// </summary>
        private void RestoreRenderers()
        {
            for (int i = _touched.Count - 1; i >= 0; i--)
            {
                try
                {
                    var rend = _touched[i];
                    if (rend != null) rend.SetPropertyBlock(_touchedOriginal[i]);
                }
                catch { /* renderer destroyed with its model; nothing to restore it onto */ }
            }
            _touched.Clear();
            _touchedOriginal.Clear();
        }

        private bool RendererMode =>
            _cfg.GlowMode.Value.IndexOf("Renderer", StringComparison.OrdinalIgnoreCase) >= 0
            || string.Equals(_cfg.GlowMode.Value, "Both", StringComparison.OrdinalIgnoreCase);

        private bool SequencerMode =>
            _cfg.GlowMode.Value.IndexOf("Sequencer", StringComparison.OrdinalIgnoreCase) >= 0
            || string.Equals(_cfg.GlowMode.Value, "Both", StringComparison.OrdinalIgnoreCase);

        private static string Describe(Exception e)
            => e.InnerException == null ? e.Message : $"{e.Message} -- cause: {e.InnerException.Message}";

        private void Emit()
        {
            if (!SequencerMode) return;   // renderer mode writes on the scan, not per frame
            if (_targets.Count == 0) return;
            int importance = _cfg.GlowImportance.Value;
            var prop = CurrentProperty();      // cached; re-parsed only when the setting changes
            bool isColour = _propertyIsColour;
            var em = _client.EntityManager;

            for (int i = 0; i < _targets.Count; i++)
            {
                var e = _targets[i];
                // Entities die between scans; emitting at a dead one would throw every frame.
                if (!em.Exists(e)) { _emitDead++; continue; }

                var change = new ChangeData
                {
                    Property = prop,
                    PropertyType = isColour ? MaterialPropertyTypeEnum.Float4
                                            : MaterialPropertyTypeEnum.Float,
                    Float4Value = _colors[i],
                    FloatValue = _colors[i].x,
                    UseCustomProperty = false,
                    RendererMask = ~0,      // every tagged renderer on the model
                };
                try { _matProp.AddChange(e, importance, ref change); _emitOk++; }
                catch { _emitThrewHybrid++; /* no model yet; next frame, or never */ }

                if (_matPropDots != null)
                {
                    // Aim the DOTS call at the MODEL entity when there is one. _matProp resolves
                    // through HybridModelSystem, which is GameObject-only and therefore cannot
                    // serve a Rukhanka unit at all; the DOTS system is the only route left for
                    // those, and the entity that actually gets drawn is the model entity, not the
                    // gameplay character. Falls back to the character entity when there is no
                    // model entity, which is what it always used to pass.
                    var model = ModelEntityOf(em, e);
                    var dotsTarget = model != Entity.Null && em.Exists(model) ? model : e;
                    var dotsChange = change;   // AddChange takes it by ref and may write back
                    try { _matPropDots.AddChange(dotsTarget, importance, ref dotsChange); }
                    catch { _emitThrewDots++; /* not on the DOTS path either */ }
                }
            }
        }

        /// <summary>
        /// Normalise blood quality to a percentage. The game's scale is not self-describing, so
        /// Auto treats anything at or below 1 as a fraction. That is ambiguous for exactly one
        /// value - a raw 1.0 is either 1% or 100% - which is why the setting can be pinned to
        /// Fraction or Percent once a dump has shown which scale this build uses.
        /// </summary>
        private float Percent(float raw)
        {
            string mode = _cfg.GlowQualityScale.Value;
            if (string.Equals(mode, "Fraction", StringComparison.OrdinalIgnoreCase)) return raw * 100f;
            if (string.Equals(mode, "Percent", StringComparison.OrdinalIgnoreCase)) return raw;
            return raw <= 1f ? raw * 100f : raw;
        }

        /// <summary>
        /// Resolve the configured channel name, falling back to _BlinkColor. Kept as a string in
        /// config so a channel can be A/B tested with the reload key instead of a rebuild - which
        /// matters because which channel the game is already driving on a given unit is not
        /// something the component data reveals.
        /// </summary>
        private string _propertyName;
        private SupportedDotsProperty _propertyCached = SupportedDotsProperty._BlinkColor;
        private bool _propertyIsColour = true;

        /// <summary>
        /// The configured channel, parsed only when the config string actually changes.
        ///
        /// This used to call ParseProperty every frame, and ParseProperty calls Enum.GetValues -
        /// which allocates an array and boxes every element - to resolve a value that only moves
        /// when someone presses the reload key. One string compare per frame replaces roughly
        /// nine allocations per frame.
        /// </summary>
        private SupportedDotsProperty CurrentProperty()
        {
            string name = _cfg.GlowProperty.Value;
            if (!string.Equals(name, _propertyName, StringComparison.Ordinal))
            {
                _propertyName = name;
                _propertyCached = ParseProperty(name);
                // Only these two are float4 colours; the others take FloatValue instead.
                _propertyIsColour = _propertyCached == SupportedDotsProperty._BlinkColor
                                 || _propertyCached == SupportedDotsProperty._DissolveColor;
            }
            return _propertyCached;
        }

        private SupportedDotsProperty ParseProperty(string name)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                foreach (SupportedDotsProperty p in Enum.GetValues(typeof(SupportedDotsProperty)))
                {
                    if (string.Equals(p.ToString(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                        return p;
                }
                if (!_warnedProperty)
                {
                    _warnedProperty = true;
                    _log.LogWarning($"GlowProperty '{name}' is not a SupportedDotsProperty name - "
                                  + "using _BlinkColor.");
                }
            }
            return SupportedDotsProperty._BlinkColor;
        }

        private bool _warnedProperty;

        private static float4 ParseColor(string s, float4 fallback)
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            var parts = s.Split(',');
            if (parts.Length < 3) return fallback;
            var v = new float[3];
            for (int i = 0; i < 3; i++)
            {
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out v[i])) return fallback;
            }
            return new float4(v[0], v[1], v[2], 1f);
        }

        private bool Ensure()
        {
            if (_client != null && _client.IsCreated && _ready) return true;

            float now = Time.unscaledTime;
            if (now < _nextAttachAttempt) return false;
            _nextAttachAttempt = now + AttachRetrySeconds;

            // Entity keys are per-world and this list holds them across the gap; a new world
            // reuses indices from 0, so a stale entry could name an unrelated unit. Same hazard
            // VisionBooster documents, same answer: drop them, never carry them over.
            // Entity keys are per-world and these renderers belong to the old world's models.
            RestoreRenderers();
            _targets.Clear();
            _colors.Clear();

            if (_queryBuilt)
            {
                try { _bloodQuery.Dispose(); } catch { }
                try { _bloodCompQuery.Dispose(); } catch { }
                _queryBuilt = false;
            }

            _client = null;
            _ready = false;
            _matProp = null;

            var all = World.s_AllWorlds;
            for (int i = 0; i < all.Count; i++)
            {
                var w = all[i];
                if (w != null && w.IsCreated && w.Name == "Client_0") { _client = w; break; }
            }
            if (_client == null) return false;

            try
            {
                _matProp = _client.GetExistingSystemManaged<MaterialPropertySystem_Hybrid>();
                try { _matPropDots = _client.GetExistingSystemManaged<MaterialPropertySystem_Dots>(); }
                catch { _matPropDots = null; }
                try { _hybridModels = _client.GetExistingSystemManaged<HybridModelSystem>(); }
                catch { _hybridModels = null; }
            }
            catch (Exception e)
            {
                _log.LogError("MaterialPropertySystem_Hybrid could not be resolved, so blood glow "
                            + $"is off (everything else still works): {e.Message}");
                return false;
            }
            if (_matProp == null)
            {
                _log.LogError("MaterialPropertySystem_Hybrid is not present in the client world, "
                            + "so blood glow cannot run.");
                return false;
            }

            try
            {
                _bloodQuery = _client.EntityManager.CreateEntityQuery(
                    new[] { ComponentType.ReadOnly<BloodConsumeSource>() });
                _bloodCompQuery = _client.EntityManager.CreateEntityQuery(
                    new[] { ComponentType.ReadOnly<Blood>() });
                _queryBuilt = true;
            }
            catch (Exception e)
            {
                _log.LogError($"Could not create the blood query (retrying in {AttachRetrySeconds}s): {e.Message}");
                return false;
            }

            _layoutOk = VerifyBloodLayout();
            _ready = true;
            _reportPending = true;
            _log.LogInfo($"Blood glow attached to client world. Mode={_cfg.GlowMode.Value}. "
                       + $"hybrid=yes dots={(_matPropDots != null ? "yes" : "no")} "
                       + $"hybridModelSystem={(_hybridModels != null ? "yes" : "no")}");
            return true;
        }

        /// <summary>
        /// BloodConsumeSource is read and never written, so a layout mismatch suppresses this
        /// feature rather than disabling the plugin's writes - and rather than driving a tint
        /// off a misread float, which would look like working code producing nonsense colours.
        /// </summary>
        private bool VerifyBloodLayout()
        {
            try
            {
                IntPtr klass = Il2CppClassPointerStore<BloodConsumeSource>.NativeClassPtr;
                if (klass == IntPtr.Zero) return true;   // fail open, as VerifyLayout does
                uint align = 0;
                int native = (int)IL2CPP.il2cpp_class_value_size(klass, ref align);
                int managed = Marshal.SizeOf<BloodConsumeSource>();

                IntPtr k2 = Il2CppClassPointerStore<Blood>.NativeClassPtr;
                if (k2 != IntPtr.Zero)
                {
                    uint a2 = 0;
                    int n2 = (int)IL2CPP.il2cpp_class_value_size(k2, ref a2);
                    int m2 = Marshal.SizeOf<Blood>();
                    if (n2 != m2)
                    {
                        _log.LogError($"ProjectM.Blood layout mismatch: il2cpp says {n2} bytes, "
                                    + $"Marshal.SizeOf says {m2}. Blood glow disabled.");
                        return false;
                    }
                }

                if (native == managed) return true;
                _log.LogError($"BloodConsumeSource layout mismatch: il2cpp says {native} bytes, "
                            + $"Marshal.SizeOf says {managed}. Blood glow disabled - rebuild the "
                            + "plugin against the current interop assemblies.");
                return false;
            }
            catch { return true; }
        }

        /// <summary>
        /// ComponentType for T, resolved once. Same reasoning as the identical cache in
        /// VisionBooster: the per-call Il2CppType.Of&lt;T&gt;() lookup was the most repeated piece
        /// of work in the scan, and it never changes for a given T.
        /// </summary>
        private static class Ct<T> where T : struct
        {
            internal static readonly ComponentType Value = new ComponentType(Il2CppType.Of<T>());
        }

        private static bool Has<T>(EntityManager em, Entity e) where T : struct
        {
            try { return em.HasComponent(e, Ct<T>.Value); }
            catch { return false; }
        }

        private static unsafe T Read<T>(EntityManager em, Entity e) where T : struct
        {
            var ct = Ct<T>.Value;
            void* p = em.GetComponentDataRawRO(e, ct.TypeIndex);
            if (p == null) throw new InvalidOperationException($"no {typeof(T).Name}");
            return Marshal.PtrToStructure<T>(new IntPtr(p));
        }
    }
}
