using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Rendering;               // VolumeComponent, VolumeStack, VolumeManager
using UnityEngine.Rendering.HighDefinition; // BatFormFog's base, VolumetricClouds, CloudLayer
using ProjectM;

namespace VRVisionBoost
{
    /// <summary>
    /// Switches off the fog and clouds that close in around you in bat form.
    ///
    /// Two separate things produce that view, and removing one without the other leaves the job
    /// half done:
    ///
    /// 1. <c>BatFormFog</c> - a full-screen HDRP custom post-process the game ships for exactly
    ///    this purpose. Note it is in the GLOBAL namespace, not <c>ProjectM</c>, despite living
    ///    in ProjectM.dll. Its chain is
    ///    <c>BatFormFog -> CustomPostProcessVolumeComponent -> VolumeComponent</c>, so it
    ///    inherits a settable <c>active</c> flag, which is what this class drives.
    /// 2. <c>DayNightCycle.Cloudiness</c> - actual world cloud cover with its own ground
    ///    shadows, normally 0.65. This is GLOBAL weather, not a bat form value, which is why it
    ///    sits behind its own setting.
    ///
    /// **Everything here is re-applied every frame, and that is the whole design.** HDRP
    /// re-evaluates its volume stack per frame and blends component values into it, so
    /// <c>active</c> may well be an output the renderer recomputes rather than an input it
    /// merely reads - the exact distinction VisionBooster is built around. Separately,
    /// DayNightCycle appears in ProjectM.GeneratedNetCode.dll, so Cloudiness may be re-synced
    /// from the server. A one-shot write would survive neither, and would fail the way those
    /// failures always look: correct for one frame, then flickering forever. Re-applying costs
    /// a walk over a handful of objects, so it is not worth being clever about.
    ///
    /// What could NOT be established without running the game: whether <c>active = false</c> is
    /// enough to stop the effect drawing. Every method on these types is an Il2CppInterop
    /// marshalling stub with no managed body, so there is nothing to read. If it turns out to be
    /// insufficient, the fallbacks in descending order of cleanliness are: set
    /// <c>intensity.value = 0</c> AND <c>intensity.overrideState = true</c> (an HDRP parameter
    /// only contributes when overridden); destroy <c>m_Material</c> and rebuild it from a copy
    /// on revert, which is what RetroCamera does; or go after <c>fogComponent</c>, a
    /// StunlockFogVolumeComponent inside the patched HDRP assembly.
    /// </summary>
    internal sealed class BatFog
    {
        private const float AttachRetrySeconds = 5f;
        private const float RescanSeconds = 2f;        // while still looking
        private const float SettledRescanSeconds = 15f; // once found
        // The material name the real BatFormFog carries; templates do not match it.
        private const string FogShaderName = "Hidden/Shader/BatFormFog";

        private readonly Settings _cfg;
        private readonly ManualLogSource _log;

        private World _client;
        private EntityQuery _cycleQuery;
        private bool _queryBuilt;
        private bool _ready;
        private float _nextAttachAttempt;
        private float _nextRescan;
        private bool _layoutOk;

        /// <summary>
        /// One tracked effect, its identity, and what it looked like before we touched it.
        ///
        /// <c>Ptr</c> is the identity that matters. Il2CppInterop hands back a **new managed
        /// wrapper** every time the same native object is enumerated or cast, so two wrappers for
        /// one effect are not reference-equal and a <c>List.Contains</c> over wrappers never
        /// matches. The first version of this class deduplicated that way, and the result was one
        /// duplicate added per rescan - "29 effect instance(s)" for a single effect - with each
        /// duplicate capturing <c>active = false</c> as its "original" because we had already
        /// written it. Revert applies originals in order, so the last write won and the fog would
        /// have stayed off forever. That is the Capture-poisoning chain from CLAUDE.md, reached
        /// by a different road. Compare pointers, never wrappers.
        /// </summary>
        private sealed class Tracked
        {
            internal VolumeComponent Comp;   // BatFormFog is one of these; so are the sky layers
            internal IntPtr Ptr;
            internal bool OriginalActive;
            internal string Label;

            // The component's own parameter, captured before the first write. Which of the two
            // value fields is meaningful depends on Label; HasParam says whether either is.
            internal bool HasParam;
            internal bool OrigOverride;
            internal bool OrigBool;
            internal float OrigFloat;

            // BatFormFog only: a copy of m_Material taken before it is destroyed. Destroying
            // the material is RetroCamera's mechanism and the only one measured to actually
            // stop the effect drawing - active=false does not.
            internal UnityEngine.Material MatBackup;
            internal bool MatDestroyed;
        }

        // The live effects, refreshed on RescanSeconds and written every frame. One list, not
        // three parallel ones - keeping an instance, its pointer and its captured original in
        // lockstep across two removal paths was its own latent bug.
        private readonly List<Tracked> _fogs = new List<Tracked>();
        private bool _haveCloudiness;
        private float _originalCloudiness;
        private bool _applied;

        private string _foundVia = "none";
        private bool _warnedNoInstances;
        private bool _warnedNoStack;
        private bool _reportPending = true;
        private string _lastReport = "";

        public BatFog(Settings cfg, ManualLogSource log) { _cfg = cfg; _log = log; }

        /// <summary>How many effect instances are currently being held off. For the dump.</summary>
        public int FogCount => _fogs.Count;

        public void Update()
        {
            if (!_cfg.BatFogEnabled.Value)
            {
                if (_applied) Revert();
                return;
            }
            if (!Ensure()) return;

            float now = Time.unscaledTime;
            if (now >= _nextRescan)
            {
                // FindObjectsOfType walks every loaded object, so once the effect is in hand
                // there is no reason to keep paying for it on a 2s cadence.
                Rescan();
                _nextRescan = now + (_fogs.Count > 0 ? SettledRescanSeconds : RescanSeconds);
                Report();
            }
            Apply();
        }

        /// <summary>
        /// Refresh the instance list. Interval work: the set only changes when the game creates
        /// or destroys the effect, which it may not do until you first enter bat form - so an
        /// empty result is "not yet", never "give up".
        /// </summary>
        private void Rescan()
        {
            // Drop destroyed instances. One list, so nothing can fall out of step.
            for (int i = _fogs.Count - 1; i >= 0; i--)
            {
                if (_fogs[i].Comp == null) _fogs.RemoveAt(i);
            }

            // BatFormFog discovery, deliberately matching RetroCamera exactly.
            //
            // Getting this "cleverer" is what broke it. Preferring the static
            // CustomPostProcessVolumeComponent.instances registry found FOUR BatFormFog
            // components, and destroying materials across all of them changed nothing on
            // screen - those are template/inactive instances, not the one that renders.
            // RetroCamera uses FindObjectsOfType (ACTIVE objects only), filters on the
            // material's NAME containing the shader path, and takes the LAST match. All three
            // details are load-bearing; none of them were guessable from metadata.
            try
            {
                BatFormFog chosen = null;
                var found = UnityEngine.Object.FindObjectsOfType<BatFormFog>();
                if (found != null)
                {
                    for (int i = 0; i < found.Length; i++)
                    {
                        var f = found[i];
                        if (f == null) continue;
                        UnityEngine.Material m = null;
                        try { m = f.m_Material; } catch { continue; }
                        if (m == null) continue;
                        string mn;
                        try { mn = m.name; } catch { continue; }
                        if (mn == null || mn.IndexOf(FogShaderName, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        chosen = f;   // keep going: we want the LAST match, not the first
                    }
                }

                if (chosen != null && IsTarget("BatFormFog")) Track(chosen, "BatFormFog", "FindObjectsOfType");
            }
            catch (Exception e)
            {
                if (!_warnedNoInstances)
                {
                    _warnedNoInstances = true;
                    _log.LogWarning($"BatFormFog scan failed: {e.Message}");
                }
            }

            if (_cfg.DisableSkyClouds.Value) TrackSkyClouds();
        }

        /// <summary>
        /// Record an instance and its pre-modification state, in that order - and only if this
        /// native object is not already tracked. Identity is the pointer, never the wrapper; see
        /// <see cref="Tracked"/> for what goes wrong otherwise.
        /// </summary>
        private void Track(VolumeComponent comp, string label, string via)
        {
            IntPtr ptr;
            bool original;
            try
            {
                ptr = comp.Pointer;
                if (ptr == IntPtr.Zero) return;
                for (int i = 0; i < _fogs.Count; i++)
                    if (_fogs[i].Ptr == ptr) return;   // already ours; do NOT re-capture
                original = comp.active;
            }
            catch { return; }   // unusable instance; do not add it half-tracked

            var t = new Tracked { Comp = comp, Ptr = ptr, OriginalActive = original, Label = label };
            CaptureParams(t);
            _fogs.Add(t);
            _foundVia = via;
        }

        /// <summary>Record the parameter state before anything writes to it.</summary>
        private void CaptureParams(Tracked t)
        {
            try
            {
                var vc = t.Comp.TryCast<VolumetricClouds>();
                if (vc != null)
                {
                    t.OrigOverride = vc.enable.overrideState;
                    t.OrigBool = vc.enable.value;
                    t.HasParam = true;
                    return;
                }

                var cl = t.Comp.TryCast<CloudLayer>();
                if (cl != null)
                {
                    t.OrigOverride = cl.opacity.overrideState;
                    t.OrigFloat = cl.opacity.value;
                    t.HasParam = true;
                    return;
                }

                // Stunlock's own fog, which is what the scene volume actually carries -
                // stock HDRP Fog is not in that profile at all. EnableFog is a purpose-built
                // off-switch, far more targeted than blanking the component with active=false.
                var sf = t.Comp.TryCast<StunlockFogVolumeComponent>();
                if (sf != null)
                {
                    t.OrigOverride = sf.EnableFog.overrideState;
                    t.OrigBool = sf.EnableFog.value;
                    t.HasParam = true;
                    return;
                }

                var fg = t.Comp.TryCast<Fog>();
                if (fg != null)
                {
                    t.OrigOverride = fg.enabled.overrideState;
                    t.OrigBool = fg.enabled.value;
                    t.HasParam = true;
                    return;
                }

                var bf = t.Comp.TryCast<BatFormFog>();
                if (bf != null)
                {
                    t.OrigOverride = bf.intensity.overrideState;
                    t.OrigFloat = bf.intensity.value;
                    t.HasParam = true;
                }
            }
            catch { }
        }

        private static string SafeTypeName(Il2CppSystem.Object o)
        {
            try { return o.GetIl2CppType().Name; } catch { return "(?)"; }
        }

        /// <summary>Put the parameter back exactly as it was found.</summary>
        private void RestoreParams(Tracked t)
        {
            if (!t.HasParam) return;
            try
            {
                var vc = t.Comp.TryCast<VolumetricClouds>();
                if (vc != null)
                {
                    vc.enable.value = t.OrigBool;
                    vc.enable.overrideState = t.OrigOverride;
                    return;
                }

                var cl = t.Comp.TryCast<CloudLayer>();
                if (cl != null)
                {
                    cl.opacity.value = t.OrigFloat;
                    cl.opacity.overrideState = t.OrigOverride;
                    return;
                }

                var sf = t.Comp.TryCast<StunlockFogVolumeComponent>();
                if (sf != null)
                {
                    sf.EnableFog.value = t.OrigBool;
                    sf.EnableFog.overrideState = t.OrigOverride;
                    return;
                }

                var fg = t.Comp.TryCast<Fog>();
                if (fg != null)
                {
                    fg.enabled.value = t.OrigBool;
                    fg.enabled.overrideState = t.OrigOverride;
                    return;
                }

                var bf = t.Comp.TryCast<BatFormFog>();
                if (bf != null)
                {
                    if (t.MatDestroyed && t.MatBackup != null)
                    {
                        bf.m_Material = new UnityEngine.Material(t.MatBackup);
                        t.MatDestroyed = false;
                    }
                    bf.intensity.value = t.OrigFloat;
                    bf.intensity.overrideState = t.OrigOverride;
                }
            }
            catch { /* component destroyed with its profile */ }
        }

        /// <summary>
        /// The sky's own cloud layers, which are NOT the bat form effect and NOT V Rising's
        /// Cloudiness value. Switching off BatFormFog and zeroing Cloudiness still leaves these
        /// drawing - measured in game, which is the only way this was ever going to be settled.
        ///
        /// These live in HDRP's evaluated volume stack rather than in the custom post-process
        /// registry, so they are reached through VolumeManager.instance.stack. Same capture and
        /// restore as everything else.
        /// </summary>
        private void TrackSkyClouds()
        {
            // Go after the PROFILES, not VolumeManager.instance.stack.
            //
            // This was got wrong the first time and the mistake is worth keeping written down,
            // because it is this plugin's own central lesson wearing a different hat. The stack
            // is an OUTPUT: VolumeManager re-blends the scene's VolumeProfile assets into it
            // every frame, during rendering - which is AFTER MonoBehaviour.Update(). So a write
            // to the stack is overwritten before the frame ever draws. It succeeds, it logs
            // happily, and it changes nothing on screen. Measured in game: BatFormFog,
            // VolumetricClouds and CloudLayer all reported switched off via the stack, and the
            // clouds were still there.
            //
            // The profiles are the INPUT the blend reads. Write those and the result survives.
            try
            {
                var volumes = UnityEngine.Object.FindObjectsOfType<Volume>();
                if (volumes == null) return;
                for (int i = 0; i < volumes.Length; i++)
                {
                    var v = volumes[i];
                    if (v == null) continue;
                    // sharedProfile ONLY. Reading Volume.profile INSTANTIATES a private copy of
                    // the shared asset as a side effect, so a read-only scan would quietly
                    // mutate scene state and leave every Volume with its own orphaned clone.
                    TrackProfile(v.sharedProfile);
                }
            }
            catch (Exception e)
            {
                if (!_warnedNoStack)
                {
                    _warnedNoStack = true;
                    _log.LogWarning($"HDRP volume profiles could not be read ({e.Message}); the "
                                  + "sky cloud layers will be left alone.");
                }
            }

            // Also take the stack. It IS the recomputed output and a write here loses the race
            // most frames - but when the components live only in HDRP's default profile and no
            // scene Volume carries them, it is the only handle that exists. Writing it every
            // frame at least contests the blend rather than conceding.
            try
            {
                var stack = VolumeManager.instance?.stack;
                if (stack == null) return;
                var sc = stack.components;
                if (sc == null) return;
                // Walk it rather than asking for known types: the stack holds 59 components
                // including Stunlock's own, and the interesting ones are not the stock HDRP set.
                foreach (var kv in sc)
                {
                    var comp = kv.Value;
                    if (comp == null) continue;
                    string tn = SafeTypeName(comp);
                    if (IsTarget(tn)) Track(comp, tn + "(stack)", "stack");
                }
            }
            catch { }
        }

        private void TrackFromStack(VolumeStack stack, Il2CppSystem.Type type, string label)
        {
            try
            {
                var comp = stack.GetComponent(type);
                if (comp != null) Track(comp, label, "stack");
            }
            catch { }
        }

        /// <summary>
        /// Is this component type one the config asks us to switch off? Matched by name and
        /// case-insensitively, so a candidate can be tried with the reload key.
        /// </summary>
        private bool IsTarget(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            string cfg = _cfg.BatFogTargets.Value;
            if (string.IsNullOrWhiteSpace(cfg)) return false;

            // Re-split only when the string actually changes - this runs per component per
            // scan, and the value only moves when someone presses reload.
            if (!string.Equals(cfg, _targetsRaw, StringComparison.Ordinal))
            {
                _targetsRaw = cfg;
                _targets.Clear();
                foreach (var part in cfg.Split(','))
                {
                    string t = part.Trim();
                    if (t.Length > 0) _targets.Add(t);
                }
            }

            for (int i = 0; i < _targets.Count; i++)
                if (string.Equals(_targets[i], typeName, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private string _targetsRaw;
        private readonly List<string> _targets = new List<string>();

        /// <summary>
        /// Walk a profile's component list and pick out the cloud ones.
        ///
        /// Iterating rather than asking for a type by name on purpose: <c>GetComponent(Type)</c>
        /// lives on VolumeStack, NOT on VolumeProfile, which is a distinction easy to get wrong
        /// from a metadata listing alone. A walk needs no such API and cannot be wrong about it.
        /// </summary>
        private void TrackProfile(VolumeProfile profile)
        {
            if (profile == null) return;
            try
            {
                var comps = profile.components;
                if (comps == null) return;
                for (int i = 0; i < comps.Count; i++)
                {
                    var c = comps[i];
                    if (c == null) continue;
                    // Match on TYPE NAME against the configured list, not on TryCast against
                    // types known at compile time. V Rising does not use Unity's stock cloud
                    // system: the scene volume carries StunlockSky and
                    // StunlockFogVolumeComponent, and no VolumetricClouds or CloudLayer at all.
                    // Hardcoding stock HDRP types meant this walk matched nothing, every
                    // component fell through to the stack, and four rounds of writes went to
                    // components the game never renders. A name list also lets a candidate be
                    // tried with the reload key instead of a rebuild.
                    string tn = SafeTypeName(c);
                    if (IsTarget(tn)) Track(c, tn, "volumeProfile");
                }
            }
            catch { }
        }

        /// <summary>
        /// Drive the component's own parameter, not just <c>active</c>.
        ///
        /// <c>active</c> alone was not enough in game. HDRP reads parameter VALUES, and a
        /// parameter only contributes to the blend when <c>overrideState</c> is set - so both
        /// halves are required, and writing <c>.value</c> without <c>.overrideState</c> is a
        /// silent no-op.
        /// </summary>
        private void SuppressParams(Tracked t)
        {
            try
            {
                var vc = t.Comp.TryCast<VolumetricClouds>();
                if (vc != null)
                {
                    vc.enable.overrideState = true;
                    if (vc.enable.value) vc.enable.value = false;
                    return;
                }

                var cl = t.Comp.TryCast<CloudLayer>();
                if (cl != null)
                {
                    cl.opacity.overrideState = true;
                    if (cl.opacity.value != 0f) cl.opacity.value = 0f;
                    return;
                }

                var sf = t.Comp.TryCast<StunlockFogVolumeComponent>();
                if (sf != null)
                {
                    sf.EnableFog.overrideState = true;
                    if (sf.EnableFog.value) sf.EnableFog.value = false;
                    return;
                }

                var fg = t.Comp.TryCast<Fog>();
                if (fg != null)
                {
                    fg.enabled.overrideState = true;
                    if (fg.enabled.value) fg.enabled.value = false;
                    return;
                }

                var bf = t.Comp.TryCast<BatFormFog>();
                if (bf != null)
                {
                    bf.intensity.overrideState = true;
                    if (bf.intensity.value != 0f) bf.intensity.value = 0f;

                    // Then the part that actually works. Setting active/intensity left the
                    // effect drawing in game; destroying the material is what RetroCamera does
                    // and it is the only mechanism with evidence behind it. Back up first, and
                    // do it once - Destroy on an already-destroyed object is pointless churn.
                    if (_cfg.DestroyFogMaterial.Value && !t.MatDestroyed)
                    {
                        var mat = bf.m_Material;
                        if (mat != null)
                        {
                            t.MatBackup = new UnityEngine.Material(mat);
                            UnityEngine.Object.Destroy(mat);
                            t.MatDestroyed = true;
                        }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Hold the effect off. Every frame - see the class comment for why this is not an
        /// interval job.
        /// </summary>
        private void Apply()
        {
            for (int i = 0; i < _fogs.Count; i++)
            {
                var t = _fogs[i];
                if (t.Comp == null) continue;
                try { if (t.Comp.active) t.Comp.active = false; }
                catch { }
                SuppressParams(t);   // active alone was not enough in game
            }

            if (_cfg.ZeroCloudiness.Value) ApplyCloudiness();
            else if (_haveCloudiness) RestoreCloudiness();

            _applied = true;
        }

        /// <summary>
        /// Say what actually happened, including when nothing did.
        ///
        /// The silent-success case is the one that matters: finding no instances is the most
        /// likely real outcome, because the game may not create the effect until you first enter
        /// bat form. An earlier version of this method only logged when the count was above zero,
        /// so pressing the key and getting nothing produced an empty log and no way to tell a
        /// broken plugin from a plugin waiting for you to shapeshift. Throttled to the rescan
        /// interval and to genuine changes, so it cannot spam.
        /// </summary>
        private void Report()
        {
            string state = $"{_fogs.Count}|{_foundVia}|{_cfg.ZeroCloudiness.Value}|{_haveCloudiness}";
            if (!_reportPending && string.Equals(state, _lastReport, StringComparison.Ordinal)) return;
            _reportPending = false;
            _lastReport = state;

            if (_fogs.Count == 0) return;

            var byLabel = new Dictionary<string, int>();
            for (int i = 0; i < _fogs.Count; i++)
            {
                string k = _fogs[i].Label ?? "?";
                byLabel[k] = byLabel.TryGetValue(k, out int n) ? n + 1 : 1;
            }
            var parts = new List<string>();
            foreach (var kv in byLabel) parts.Add($"{kv.Key} x{kv.Value}");

            _log.LogInfo($"Bat form fog off - {string.Join(", ", parts)}"
                       + (_cfg.ZeroCloudiness.Value && _haveCloudiness ? ", cloud cover zeroed" : "")
                       + ".");
        }

        private void ApplyCloudiness()
        {
            if (!_layoutOk || !_queryBuilt) return;
            var em = _client.EntityManager;
            Entity e = SingleCycle(em);
            if (e == Entity.Null) return;

            try
            {
                var c = Read<DayNightCycle>(em, e);
                if (!_haveCloudiness)
                {
                    _originalCloudiness = c.Cloudiness;
                    _haveCloudiness = true;
                }
                // Live value comparison, not a dirty flag: this is what makes the write
                // self-healing if the server re-syncs it or a throw skips a frame.
                if (c.Cloudiness != 0f)
                {
                    c.Cloudiness = 0f;
                    Write(em, e, c);
                }
            }
            catch { }
        }

        private void RestoreCloudiness()
        {
            if (!_haveCloudiness || !_queryBuilt || _client == null || !_client.IsCreated) return;
            try
            {
                var em = _client.EntityManager;
                Entity e = SingleCycle(em);
                if (e != Entity.Null)
                {
                    var c = Read<DayNightCycle>(em, e);
                    c.Cloudiness = _originalCloudiness;
                    Write(em, e, c);
                }
            }
            catch { /* world going away; nothing to restore onto */ }
            _haveCloudiness = false;
        }

        /// <summary>
        /// The DayNightCycle singleton entity, cached.
        ///
        /// This is consumed on the per-frame path, and ToEntityArray allocates a NativeArray on
        /// every call - for a singleton that does not move for the lifetime of the world. Resolve
        /// once, re-resolve only when the cached entity stops existing.
        /// </summary>
        private Entity SingleCycle(EntityManager em)
        {
            if (_cycle != Entity.Null && em.Exists(_cycle)) return _cycle;

            _cycle = Entity.Null;
            var ents = _cycleQuery.ToEntityArray(Allocator.Temp);
            try { if (ents.Length > 0) _cycle = ents[0]; }
            finally { ents.Dispose(); }
            return _cycle;
        }

        private Entity _cycle;

        /// <summary>Put everything back. Called on toggle-off, config-off and shutdown.</summary>
        public void Revert()
        {
            for (int i = 0; i < _fogs.Count; i++)
            {
                try
                {
                    var t = _fogs[i];
                    if (t.Comp == null) continue;
                    RestoreParams(t);
                    t.Comp.active = t.OriginalActive;
                }
                catch { /* instance destroyed with its volume */ }
            }
            _fogs.Clear();

            RestoreCloudiness();
            _applied = false;
            _reportPending = true;
            _nextRescan = 0f;
        }

        public void Reset() => Revert();

        private bool Ensure()
        {
            if (_client != null && _client.IsCreated && _ready) return true;

            float now = Time.unscaledTime;
            if (now < _nextAttachAttempt) return false;
            _nextAttachAttempt = now + AttachRetrySeconds;

            // Entity keys and Unity object references are per-world; carrying either across a
            // reconnect would write the old world's values onto a stranger. Drop them.
            _fogs.Clear();
            _haveCloudiness = false;
            _applied = false;
            _cycle = Entity.Null;

            if (_queryBuilt)
            {
                try { _cycleQuery.Dispose(); } catch { }
                _queryBuilt = false;
            }

            _client = null;
            _ready = false;

            var all = World.s_AllWorlds;
            for (int i = 0; i < all.Count; i++)
            {
                var w = all[i];
                if (w != null && w.IsCreated && w.Name == "Client_0") { _client = w; break; }
            }
            if (_client == null) return false;

            try
            {
                _cycleQuery = _client.EntityManager.CreateEntityQuery(
                    new[] { ComponentType.ReadWrite<DayNightCycle>() });
                _queryBuilt = true;
            }
            catch (Exception e)
            {
                _log.LogError("Could not create the DayNightCycle query (retrying in "
                            + $"{AttachRetrySeconds}s): {e.Message}");
                return false;
            }

            _layoutOk = VerifyCycleLayout();
            _ready = true;
            _reportPending = true;
            _log.LogInfo("Bat form fog attached to client world. "
                       + $"cloudiness={(_layoutOk ? "writable" : "DISABLED (layout mismatch)")}");
            return true;
        }

        /// <summary>
        /// DayNightCycle is written, so it gets the same treatment every written component in
        /// this plugin gets. A mismatch disables only the cloudiness half - the fog switch is a
        /// managed property write and is unaffected. Fails OPEN: a probe that cannot run is not
        /// evidence of a mismatch.
        /// </summary>
        private bool VerifyCycleLayout()
        {
            try
            {
                IntPtr klass = Il2CppClassPointerStore<DayNightCycle>.NativeClassPtr;
                if (klass == IntPtr.Zero) return true;
                uint align = 0;
                int native = (int)IL2CPP.il2cpp_class_value_size(klass, ref align);
                int managed = Marshal.SizeOf<DayNightCycle>();
                if (native == managed) return true;
                _log.LogError($"DayNightCycle layout mismatch: il2cpp says {native} bytes, "
                            + $"Marshal.SizeOf says {managed}. Cloud cover will be left alone - "
                            + "rebuild the plugin against the current interop assemblies.");
                return false;
            }
            catch { return true; }
        }

        /// <summary>ComponentType for T, resolved once. Same cache as the other two files.</summary>
        private static class Ct<T> where T : struct
        {
            internal static readonly ComponentType Value = new ComponentType(Il2CppType.Of<T>());
        }

        private static unsafe T Read<T>(EntityManager em, Entity e) where T : struct
        {
            void* p = em.GetComponentDataRawRO(e, Ct<T>.Value.TypeIndex);
            return Marshal.PtrToStructure<T>(new IntPtr(p));
        }

        private unsafe bool Write<T>(EntityManager em, Entity e, T value) where T : struct
        {
            if (!_layoutOk) return false;
            void* p = em.GetComponentDataRawRW(e, Ct<T>.Value.TypeIndex);
            if (p == null) return false;
            Marshal.StructureToPtr(value, new IntPtr(p), false);
            return true;
        }
    }
}
