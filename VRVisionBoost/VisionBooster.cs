using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;
using ProjectM;
using ProjectM.Hybrid;
using ProjectM.Shared;
using ProjectM.Network;

namespace VRVisionBoost
{
    /// <summary>
    /// Keeps units - mobs, VBloods, critters - visible past the game's vision / line-of-sight
    /// cutoff.
    ///
    /// V Rising hides characters client-side. <c>ProjectM.VisibilitySystem_Client</c>
    /// (ProjectM.Presentation.Systems) runs every frame over everything carrying
    /// <c>ProjectM.Hideable</c> and decides what gets drawn, using the local player's
    /// <c>ProjectM.Vision.Range</c> plus a compute-shader line-of-sight pass
    /// (<c>VisualLineOfSightSystem</c>). Entities tagged <c>HideOutsideVision</c> /
    /// <c>HideRendererOutsideVision</c> vanish once they fall outside it.
    ///
    /// This class writes that system's *inputs* rather than fighting its outputs.
    /// <c>Hideable.IgnoreLoS</c> and <c>Hideable.AdditionalHideRangeSq</c> are read by the
    /// system, so setting them makes the game's own code keep the entity visible.
    /// <c>IsHidden</c> / <c>Visibility</c> are outputs; they are forced too, but only as a
    /// belt-and-braces second pass - on their own they would flicker, because the system
    /// recomputes them every frame and would win.
    ///
    /// HARD LIMIT, and it is not a bug: this only affects entities the server has already
    /// streamed to this client. Which entities those are is decided server-side (the
    /// SyncToUser bitmask machinery, <c>ProjectM.Network.PrioritizeSystem</c>,
    /// <c>UserActivityGridSystem</c>, <c>CalculateRelevantAabbs</c>), and on this install the
    /// server is a separate process - VRising_Server\VRisingServer.exe - even for solo play.
    /// A client-side plugin cannot make the server send something it did not send.
    /// <see cref="Dump"/> measures where that wall actually is.
    ///
    /// This is the only file in the plugin that touches game data. Anything that breaks on a
    /// game patch breaks here and nowhere else.
    /// </summary>
    public sealed class VisionBooster
    {
        private const float AttachRetrySeconds = 5f;

        private readonly Settings _cfg;
        private readonly ManualLogSource _log;

        private World _client;
        private EntityQuery _hideableQuery;
        private EntityQuery _localQuery;
        private EntityQuery _disabledCharQuery;
        private bool _ready;
        private float _nextAttachAttempt;

        // Adding a component is a structural change: it moves the entity to a different chunk
        // and invalidates the raw component pointers the scan loop is holding. Collect during
        // the scan, apply after it.
        private readonly List<Entity> _needFlightTag = new List<Entity>();

        // Exactly the entities THIS plugin tagged, keyed by packed (Index, Version) so a
        // recycled entity id cannot be mistaken for one we tagged. Removal is driven from this
        // set and never from a query: a few prefabs ship with VisibleFromFlight of their own
        // (the Manticore variants), and stripping it from those would break the base game.
        private readonly Dictionary<ulong, Entity> _taggedByUs = new Dictionary<ulong, Entity>();
        private ComponentType _flightTagType;
        private bool _flightTagUsable;

        /// <summary>
        /// Hideable.IgnoreLoS / AdditionalHideRangeSq and CheckOnScreen.IgnoreLineOfSight /
        /// MaxDistanceForHudAndFadeOut and Vision.Range are INPUTS to the game's systems: they are
        /// read every frame and never written back. That is exactly why the plugin sets them - and
        /// exactly why they do NOT revert on their own when it stops. Without capturing the
        /// originals here, turning the plugin off would leave line-of-sight permanently disabled
        /// on every entity it ever touched, for the rest of the session.
        /// </summary>
        private struct Original
        {
            public Entity Entity;
            public bool HasHideable; public Hideable Hideable;
            public bool HasCheck;    public CheckOnScreen Check;
            public bool HasVision;   public Vision Vision;
        }

        private const int MaxTrackedOriginals = 8000;
        private readonly Dictionary<ulong, Original> _originals = new Dictionary<ulong, Original>();
        private bool _originalsCapped;

        private float _appliedModelShowRangeSq = -1f;
        private float _originalModelShowRangeSq;
        private bool _modelRangeApplied;
        private bool _writesDisabled;

        // Logged once per enable, so the log says what actually happened, not what we hoped.
        private bool _reportPending;

        /// <summary>
        /// Every config value this plugin pushes into the world. Any change at all triggers a
        /// full restore-then-reapply. Undoing knob-by-knob was tried and leaked three separate
        /// ways - HudDistance -> 0 stranded CheckOnScreen, VisionRange -> 0 stranded your own
        /// Vision.Range, and turning the reveal flag off skipped the ModelShowRange restore -
        /// because each new setting needed its own bespoke unwind and one was always forgotten.
        /// Snapshotting the whole set kills the bug class instead of one instance of it.
        /// </summary>
        private readonly struct Applied : IEquatable<Applied>
        {
            // KeepModelsLoaded is deliberately NOT here: TimeSinceLastSeen is a timer the game
            // advances and this plugin never Captures it, so there is nothing to undo and
            // toggling it should not trigger a teardown.
            public readonly bool RevealAll, SeeThroughWalls, FlightTag;
            public readonly float VisionRange, HudDistance, ModelShowRange;

            public Applied(Settings c)
            {
                RevealAll = c.RevealAllUnits.Value;
                SeeThroughWalls = c.SeeThroughWalls.Value;
                FlightTag = c.VisibleFromFlight.Value;
                VisionRange = c.VisionRange.Value;
                HudDistance = c.HudDistance.Value;
                ModelShowRange = c.ModelShowRange.Value;
            }

            public bool Equals(Applied o) =>
                RevealAll == o.RevealAll && SeeThroughWalls == o.SeeThroughWalls
                && FlightTag == o.FlightTag
                && VisionRange.Equals(o.VisionRange) && HudDistance.Equals(o.HudDistance)
                && ModelShowRange.Equals(o.ModelShowRange);
        }

        private Applied _applied;
        private bool _haveApplied;

        // Read-only, dump-only. Verified separately from VerifyLayouts() because a mismatch here
        // must not disable the plugin's writes - nothing writes BloodConsumeSource.
        private bool _bloodUsable;

        /// <summary>Set by the plugin so the dump can report glow state. Optional; may be null.</summary>
        internal BloodGlow Glow { get; set; }

        /// <summary>Whether the booster is currently writing to the world.</summary>
        public bool Active { get; private set; }

        public VisionBooster(Settings cfg, ManualLogSource log)
        {
            _cfg = cfg;
            _log = log;
            Active = cfg.Enabled.Value;
            _reportPending = Active;
        }

        public bool Toggle()
        {
            Active = !Active;
            _reportPending = Active;

            // The inputs this plugin writes are read by the game every frame and never written
            // back, so they do NOT self-revert - and ModelShowRange is a process-wide static on
            // top of that. Turning the plugin off has to undo them explicitly, otherwise
            // "Vision boost OFF" is simply false.
            if (!Active) Revert();
            return Active;
        }

        /// <summary>Undo the effects that do not expire on their own.</summary>
        public void Revert()
        {
            RestoreModelShowRange();
            _haveApplied = false;   // reset on every exit path below, not just the last one
            if (_originals.Count == 0 && _taggedByUs.Count == 0) return;
            if (_client == null || !_client.IsCreated || !_ready)
            {
                _originals.Clear();
                _taggedByUs.Clear();
                return;
            }
            var em = _client.EntityManager;
            RestoreOriginals(em);
            RemoveFlightTags(em);
        }

        /// <summary>
        /// Take back exactly the tags this plugin added. Driven from <c>_taggedByUs</c> and never
        /// from a query over the component, because some prefabs carry it natively and removing
        /// it from those would change base-game behaviour.
        /// </summary>
        private void RemoveFlightTags(EntityManager em)
        {
            int removed = 0;
            foreach (var kv in _taggedByUs)
            {
                try
                {
                    var e = kv.Value;
                    if (!em.Exists(e)) continue;              // version-checked: recycled ids fail
                    if (!em.HasComponent(e, _flightTagType)) continue;
                    em.RemoveComponent(e, _flightTagType);
                    removed++;
                }
                catch { /* entity went away mid-revert; nothing to undo */ }
            }
            _taggedByUs.Clear();
            if (removed > 0) _log.LogInfo($"Removed VisibleFromFlight from {removed} units.");
        }

        // ---- world access ---------------------------------------------------------
        /// <summary>
        /// Attach to the client world and build the queries. Retries are deliberately throttled:
        /// this runs on every tick while detached, and an unthrottled failure path would re-run
        /// three CreateEntityQuery calls plus the layout check at 1000/IntervalMs Hz and log a
        /// stack trace each time. A plain "already failed" bool cannot work here, because the
        /// world is re-resolved from scratch on every attempt and nothing distinguishes a new
        /// world from the same one - so the throttle is time-based.
        /// </summary>
        private bool Ensure()
        {
            if (_client != null && _client.IsCreated && _ready) return true;

            float now = Time.unscaledTime;
            if (now < _nextAttachAttempt) return false;
            _nextAttachAttempt = now + AttachRetrySeconds;

            // Distinguish "the world went away" from "we are rebuilding queries on the world we
            // already had". _client is assigned below BEFORE the query-building try, so a throw
            // in there leaves us attached-but-not-ready, and the next attempt re-resolves the
            // SAME live world. Discarding the revert bookkeeping in that case would be actively
            // harmful rather than merely lossy: the entities still carry this plugin's values,
            // every re-apply guard is a value comparison that would now read false so nothing
            // re-Captures them, and the next SeeThroughWalls change would then record the
            // plugin's own values as the "original" and make them permanent on revert.
            bool previousWorldAlive = _client != null && _client.IsCreated;

            _client = null;
            _ready = false;

            var all = World.s_AllWorlds;
            for (int i = 0; i < all.Count; i++)
            {
                var w = all[i];
                if (w != null && w.IsCreated && w.Name == "Client_0") { _client = w; break; }
            }
            if (_client == null) return false;

            // Build into locals: if the second or third query throws, the earlier ones must be
            // disposed, or every retry would strand more queries in the world for the session.
            EntityQuery hideable = default, local = default, disabledChars = default;
            bool built = false;
            try
            {
                var em = _client.EntityManager;
                hideable = em.CreateEntityQuery(new[] { ComponentType.ReadOnly<Hideable>() });
                local = em.CreateEntityQuery(new[] {
                    ComponentType.ReadOnly<LocalCharacter>(), ComponentType.ReadOnly<Translation>() });
                // Naming Disabled explicitly is what makes a query match disabled entities at all -
                // every other query here silently skips them. A unit the game has switched off is
                // the case this exists to catch: its blood data is right there, but no plain query
                // will ever select it, so the glow cannot reach it.
                disabledChars = em.CreateEntityQuery(new[] {
                    ComponentType.ReadOnly<Health>(), ComponentType.ReadOnly<Translation>(),
                    ComponentType.ReadOnly<Disabled>() });
                built = true;
            }
            catch (Exception e)
            {
                _log.LogError($"Could not create entity queries (retrying in {AttachRetrySeconds}s): {e.Message}");
            }

            if (!built)
            {
                TryDispose(hideable); TryDispose(local); TryDispose(disabledChars);
                return false;
            }

            _hideableQuery = hideable;
            _localQuery = local;
            _disabledCharQuery = disabledChars;
            _ready = true;
            _log.LogInfo("Attached to client world.");

            // Reaching here means a fresh attach, and the world we were tracking entities in is
            // gone - a disconnect destroys Client_0 and the next one hands out Entity indices
            // from 0 again. Entity is {Index, Version} with no world identity, so a surviving key
            // can collide with an unrelated live entity in the NEW world; em.Exists would pass
            // and RestoreOriginals would stamp the old world's Hideable/Vision/CheckOnScreen onto
            // a stranger. Drop the bookkeeping WITHOUT restoring - those entities do not exist to
            // restore. Nothing leaks: the only effect that outlives a world is the ModelShowRange
            // static, which is process-wide state handled separately.
            if (!previousWorldAlive && (_originals.Count > 0 || _taggedByUs.Count > 0))
            {
                _log.LogInfo($"Discarding revert state from the previous world "
                           + $"({_originals.Count} entities, {_taggedByUs.Count} flight tags) - "
                           + "those entities no longer exist.");
                _originals.Clear();
                _taggedByUs.Clear();
                _originalsCapped = false;
            }
            _haveApplied = false;   // re-apply from scratch against whatever we just bound

            // Outside the try above on purpose: a failure in here is NOT a query-creation
            // failure, and must not be reported as one nor leave _ready set from a lie.
            VerifyLayouts();
            _bloodUsable = VerifyLayout<BloodConsumeSource>();

            // Resolve the flight tag once. Doing it per entity would turn one bad type lookup
            // into an exception on every entity of every scan, killing the whole Tick.
            try
            {
                _flightTagType = new ComponentType(Il2CppType.Of<VisibleFromFlight>());
                _flightTagUsable = true;
            }
            catch (Exception e)
            {
                _flightTagUsable = false;
                _log.LogError("ProjectM.VisibleFromFlight is unavailable, so units will stay "
                            + $"hidden while you fly (everything else still works): {e.Message}");
            }
            return true;
        }

        private void TryDispose(EntityQuery q)
        {
            try { q.Dispose(); } catch { /* never created, or already gone */ }
        }

        /// <summary>
        /// The ComponentType for T, resolved once per closed generic type.
        ///
        /// Il2CppType.Of&lt;T&gt;() is a runtime type lookup, and the helpers below used to call it on
        /// EVERY Has/Read/Write - once per entity per component per tick, a few thousand lookups a
        /// second across a 60-entity world. A static generic field gets one instance per T,
        /// initialised on first use. Unity's TypeIndex is process-global rather than per-World, so
        /// this stays valid across a disconnect and reconnect.
        /// </summary>
        private static class Ct<T> where T : struct
        {
            internal static readonly ComponentType Value = new ComponentType(Il2CppType.Of<T>());
        }

        private static bool Has<T>(EntityManager em, Entity e) where T : struct
            => em.HasComponent(e, Ct<T>.Value);

        /// <summary>Raw component read that side-steps IL2CPP interop generics.</summary>
        private static unsafe T Read<T>(EntityManager em, Entity e) where T : struct
        {
            var ct = Ct<T>.Value;
            void* p = em.GetComponentDataRawRO(e, ct.TypeIndex);
            return Marshal.PtrToStructure<T>(new IntPtr(p));
        }

        /// <summary>
        /// The mirror of <see cref="Read{T}"/>. Marshalling is safe here because Il2CppInterop
        /// emits these component structs as LayoutKind.Explicit with [MarshalAs(U1)] on every
        /// bool, so the marshalled size equals the native size - verified against the pinned
        /// interop set: Hideable = 16 bytes, CheckOnScreen = 24, Vision = 4,
        /// HybridModelUser = 20 - all re-checked at runtime by VerifyLayouts(). If that ever stops
        /// being true this writes past the component and corrupts the chunk, so re-verify after
        /// a BepInEx or Unity bump rather than assuming.
        /// </summary>
        private unsafe bool Write<T>(EntityManager em, Entity e, T value) where T : struct
        {
            if (_writesDisabled) return false;
            var ct = Ct<T>.Value;
            void* p = em.GetComponentDataRawRW(e, ct.TypeIndex);
            if (p == null) return false;
            Marshal.StructureToPtr(value, new IntPtr(p), false);
            return true;
        }

        /// <summary>
        /// The one thing that makes <see cref="Write{T}"/> safe is that Marshal.SizeOf equals the
        /// size ECS actually reserves for the component in a chunk. Everything else about the
        /// marshalling is inference; this is a measurement. StructureToPtr writes the full
        /// Marshal.SizeOf extent - trailing padding included - so if the managed size were ever
        /// the larger of the two we would silently corrupt the next component in the chunk.
        /// Checked once per world, and writes are disabled outright rather than risk that.
        /// </summary>
        private void VerifyLayouts()
        {
            _writesDisabled = false;
            bool ok = true;
            ok &= VerifyLayout<Hideable>();
            ok &= VerifyLayout<Vision>();
            ok &= VerifyLayout<CheckOnScreen>();
            ok &= VerifyLayout<HybridModelUser>();
            if (!ok)
            {
                _writesDisabled = true;
                _log.LogError("Component layouts do not match this build. All writes are disabled; "
                            + "the dump still works. Rebuild the plugin against the current "
                            + "BepInEx/interop assemblies (a game patch regenerates them).");
            }
        }

        /// <summary>
        /// Compare the size we would marshal against the component's TRUE native size, taken from
        /// il2cpp itself. An earlier version asked Unity for TypeManager.GetTypeInfo(..).SizeInChunk;
        /// that method is a `ref TypeInfo` return, which Il2CppInterop cannot marshal, and it
        /// silently produced the same bogus 640 for every type - disabling the whole plugin over a
        /// mismatch that did not exist. Hence two rules here:
        ///   1. ask il2cpp directly (il2cpp_class_value_size is the authority), and
        ///   2. FAIL OPEN. If the probe itself cannot run, warn and carry on. A safety check that
        ///      breaks the feature when the check is what is broken is worse than no check.
        /// Only a probe that definitely worked AND disagrees disables writes.
        /// </summary>
        private bool VerifyLayout<T>() where T : struct
        {
            int managed;
            try { managed = Marshal.SizeOf<T>(); }
            catch (Exception e)
            {
                _log.LogWarning($"Could not size {typeof(T).Name} ({e.Message}); skipping its check.");
                return true;
            }

            int native;
            try
            {
                IntPtr klass = Il2CppClassPointerStore<T>.NativeClassPtr;
                if (klass == IntPtr.Zero)
                {
                    _log.LogWarning($"No il2cpp class for {typeof(T).Name}; skipping its layout check.");
                    return true;
                }
                uint align = 0;
                native = IL2CPP.il2cpp_class_value_size(klass, ref align);
            }
            catch (Exception e)
            {
                _log.LogWarning($"Layout check unavailable for {typeof(T).Name} ({e.Message}); "
                              + "continuing without it.");
                return true;
            }

            if (native <= 0)
            {
                _log.LogWarning($"il2cpp reported a nonsense size ({native}) for {typeof(T).Name}; "
                              + "ignoring the check rather than trusting it.");
                return true;
            }

            if (native == managed) return true;

            _log.LogError($"LAYOUT MISMATCH {typeof(T).Name}: il2cpp says {native} bytes, "
                        + $"Marshal.SizeOf says {managed}.");
            return false;
        }

        private static ulong Key(Entity e) => ((ulong)(uint)e.Index << 32) | (uint)e.Version;

        /// <summary>
        /// Record an entity's pre-modification state before the first write to a given FIELD.
        ///
        /// This merges per-field rather than per-entity, and that distinction is the whole point.
        /// An earlier version returned early on ContainsKey, so whichever field was touched first
        /// won and the rest were never recorded. BoostLocalVision runs before the Hideable loop,
        /// so the local player was captured for Vision and its Hideable original was silently
        /// lost - a live dump showed Vision.Range correctly restored to 40 while
        /// AdditionalHideRangeSq and IgnoreLoS stayed at the plugin's values after revert.
        ///
        /// Past MaxTrackedOriginals this returns without recording while the caller still writes,
        /// so those entities are modified and unrevertable - and worse, a later Capture of one of
        /// them would record THIS PLUGIN'S values as its "original", making that permanent.
        /// Unreachable today (live dumps show 19-24 hideable entities against a cap of 8000);
        /// read this before raising the cap or widening what the hideable query matches.
        /// </summary>
        private void Capture(EntityManager em, Entity e, bool hideable, bool check, bool vision)
        {
            ulong k = Key(e);
            bool existed = _originals.TryGetValue(k, out var o);
            if (!existed)
            {
                if (_originals.Count >= MaxTrackedOriginals)
                {
                    if (!_originalsCapped)
                    {
                        _originalsCapped = true;
                        _log.LogWarning($"Tracking {MaxTrackedOriginals} entities for revert; beyond "
                                      + "that changes cannot be undone. Narrow the reveal scope "
                                      + "if you need a clean revert.");
                    }
                    return;
                }
                o = new Original { Entity = e };
            }

            try
            {
                if (hideable && !o.HasHideable) { o.Hideable = Read<Hideable>(em, e); o.HasHideable = true; }
                if (check && !o.HasCheck) { o.Check = Read<CheckOnScreen>(em, e); o.HasCheck = true; }
                if (vision && !o.HasVision) { o.Vision = Read<Vision>(em, e); o.HasVision = true; }
            }
            catch
            {
                if (!existed) return;   // nothing captured at all - do not pretend we can restore
            }

            _originals[k] = o;          // write back: a struct in a dictionary is a copy
        }

        private void RestoreOriginals(EntityManager em)
        {
            int restored = 0;
            foreach (var kv in _originals)
            {
                var o = kv.Value;
                try
                {
                    if (!em.Exists(o.Entity)) continue;
                    if (o.HasHideable && Has<Hideable>(em, o.Entity)) { Write(em, o.Entity, o.Hideable); restored++; }
                    if (o.HasCheck && Has<CheckOnScreen>(em, o.Entity)) Write(em, o.Entity, o.Check);
                    if (o.HasVision && Has<Vision>(em, o.Entity)) Write(em, o.Entity, o.Vision);
                }
                catch { /* entity gone; nothing to restore */ }
            }
            _originals.Clear();
            _originalsCapped = false;
            if (restored > 0) _log.LogInfo($"Restored original visibility state on {restored} entities.");
        }

        private static Vector3 ToVec(Unity.Mathematics.float3 f) => new Vector3(f.x, f.y, f.z);

        /// <summary>
        /// Print every component name on an entity. The archetype is the ground truth for "what
        /// IS this thing" - distance, health and category have each misled this investigation at
        /// least once. Driven by DumpComponentsRange, for whatever you are standing next to.
        /// </summary>
        private void LogComponents(EntityManager em, Entity e)
        {
            NativeArray<ComponentType> types;
            try { types = em.GetComponentTypes(e, Allocator.Temp); }
            catch { return; }
            try
            {
                var sb2 = new StringBuilder("    components: ");
                for (int t = 0; t < types.Length; t++)
                {
                    string n;
                    try { n = TypeManager.GetType(types[t].TypeIndex)?.FullName; }
                    catch { n = null; }
                    if (string.IsNullOrEmpty(n)) { try { n = types[t].ToString(); } catch { n = "?"; } }
                    sb2.Append(n).Append(t + 1 < types.Length ? ", " : "");
                }
                _log.LogInfo(sb2.ToString());
            }
            finally { types.Dispose(); }
        }

        /// <summary>
        /// Blood quality for the dump, as a RAW value. Deliberately not scaled to a percentage:
        /// nothing here establishes whether the game stores 0..1 or 0..100, and guessing would
        /// bake that guess into every later decision. Read one dump, then calibrate.
        /// Reported read-only; a layout mismatch here suppresses the field rather than
        /// disabling the plugin's writes, because nothing writes this component.
        /// </summary>
        /// <summary>
        /// What the entity actually IS. The CHAR list is built from "anything with Health", which
        /// sweeps in structures - prison cells, coffins - alongside characters, and they are easy
        /// to mistake for units that are failing to glow. EntityCategory separates them, and
        /// "static" (no ProjectM.Movement) is a second, independent tell.
        /// </summary>
        private string TypeInfo(EntityManager em, Entity e)
        {
            try
            {
                if (!Has<EntityCategory>(em, e)) return " cat=none";
                var c = Read<EntityCategory>(em, e);
                return $" cat={c.MainCategoryInt._Value}/{c.UnitCategoryInt._Value}"
                     + $"/{c.StructureCategoryInt._Value}"
                     + (Has<Movement>(em, e) ? " mobile" : " STATIC");
            }
            catch { return " cat=<unreadable>"; }
        }

        /// <summary>
        /// Why a unit is or is not glowing. glow=yes means the plugin is driving a tint at it this
        /// frame; model= is the hybrid model entity, and "none" there means there is nothing for a
        /// tint to land on no matter how correctly it is being sent.
        /// </summary>
        private string GlowInfo(EntityManager em, Entity e)
        {
            if (Glow == null) return "";
            string s = Glow.IsTarget(e) ? " glow=yes" : " glow=no";
            try
            {
                if (Has<HybridModelUser>(em, e))
                {
                    var hm = Read<HybridModelUser>(em, e);
                    s += hm.HybridEntity == Entity.Null
                        ? " model=none"
                        : $" model={hm.HybridEntity.Index}:{hm.HybridEntity.Version}"
                          + $" lastSeen={hm.TimeSinceLastSeen:0.0}s";
                }
                else s += " model=n/a";
            }
            catch { s += " model=<unreadable>"; }
            return s;
        }

        private string BloodInfo(EntityManager em, Entity e)
        {
            if (!_bloodUsable) return "";
            string s = "";
            try
            {
                if (Has<BloodConsumeSource>(em, e))
                {
                    var b = Read<BloodConsumeSource>(em, e);
                    s += $" bloodQ={b.BloodQuality:0.###} canConsume={b.CanBeConsumed}";
                }
            }
            catch { s += " bloodQ=<unreadable>"; }

            // Separate component, separate field, and some units carry only this one: servants
            // show a quality in the UI while having no BloodConsumeSource at all, which is why a
            // query over that component alone could never select them.
            try
            {
                if (Has<Blood>(em, e))
                {
                    var b2 = Read<Blood>(em, e);
                    s += $" bloodCompQ={b2.Quality:0.###}";
                }
            }
            catch { s += " bloodCompQ=<unreadable>"; }
            return s;
        }

        // ---- the per-tick boost ---------------------------------------------------
        public void Tick()
        {
            if (!Active || !Ensure()) return;
            var em = _client.EntityManager;

            // A config edit (reload key) that changes ANY applied value puts everything back
            // first, then re-applies from scratch below in this same tick - no flicker, and no
            // per-setting unwind to forget. Turning a knob off is just the case where the
            // re-apply declines to write it again.
            var want = new Applied(_cfg);
            if (_haveApplied && !want.Equals(_applied))
            {
                RestoreOriginals(em);
                RemoveFlightTags(em);
                // Only when its own value moved: ApplyModelShowRange below would just set it
                // straight back, and bouncing a process-wide static off and on - with a log line
                // each way - for an unrelated setting is noise, not safety.
                if (!want.ModelShowRange.Equals(_applied.ModelShowRange)) RestoreModelShowRange();
            }
            _applied = want;
            _haveApplied = true;

            bool revealAll = want.RevealAll;

            float range = want.VisionRange;
            if (range > 0f) BoostLocalVision(em, range);

            // Deliberately ahead of the early return below: SHOW_INSIDE_RANGE_SQ is a
            // process-wide static that outlives every entity, so switching the reveal flag off
            // must not be able to strand it.
            ApplyModelShowRange();

            if (!revealAll) { _reportPending = false; return; }

            // AdditionalHideRangeSq is a squared distance, so square the range. Floor it well
            // past any sane draw distance - the point is "never hide this".
            float keep = Mathf.Max(range, 200f);
            float keepSq = keep * keep;
            float hud = want.HudDistance;
            bool keepModels = _cfg.KeepModelsLoaded.Value;   // not snapshotted; nothing to undo
            bool los = want.SeeThroughWalls;

            // No separate "VisibleFromFlight was switched off" unwind here: the snapshot check
            // above already removed the tags before we got this far.
            bool flightTag = want.FlightTag && _flightTagUsable;

            int seen = 0, revealed = 0;
            _needFlightTag.Clear();
            var ents = _hideableQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < ents.Length; i++)
                {
                    var e = ents[i];
                    // Units only. Characters carrying PlayerCharacter are skipped outright - the
                    // plugin has nothing to do with them, and the local character's own vision is
                    // handled separately by BoostLocalVision.
                    if (Has<PlayerCharacter>(em, e)) continue;
                    seen++;

                    // Only AdditionalHideRangeSq is needed to see further - it is the distance
                    // input to the hide check. IgnoreLoS is a separate thing entirely: it drops
                    // the line-of-sight test, so it is opt-in and off by default. With it off the
                    // game still owns IsHidden/Visibility, and
                    // forcing those outputs anyway would just flicker against it every tick.
                    var h = Read<Hideable>(em, e);
                    if (h.AdditionalHideRangeSq < keepSq || h.IgnoreLoS != los
                        || (los && (h.IsHidden || h.Visibility < 1f)))
                    {
                        Capture(em, e, true, false, false);
                        h.AdditionalHideRangeSq = keepSq;    // input: widen the hide distance
                        h.IgnoreLoS = los;                   // input: skip the line-of-sight test
                        if (los)
                        {
                            h.IsHidden = false;              // output, forced
                            h.Visibility = 1f;               // output, forced (fade alpha)
                        }
                        if (Write(em, e, h)) revealed++;
                    }

                    // While you fly, VisibilitySystem_Client keeps only entities carrying this tag
                    // visible - measured, a unit 7m below with no tag was still hidden. Queued,
                    // not added here: a structural change mid-scan would invalidate the raw
                    // component pointers this loop is using.
                    if (flightTag && !em.HasComponent(e, _flightTagType)) _needFlightTag.Add(e);

                    // A revealed unit still renders as nothing if its model was unloaded.
                    if (keepModels && Has<HybridModelUser>(em, e))
                    {
                        var hm = Read<HybridModelUser>(em, e);
                        if (hm.TimeSinceLastSeen > 0f)
                        {
                            hm.TimeSinceLastSeen = 0f;
                            Write(em, e, hm);
                        }
                    }

                    if (hud > 0f && Has<CheckOnScreen>(em, e))
                    {
                        var c = Read<CheckOnScreen>(em, e);
                        if (c.IgnoreLineOfSight != los || c.MaxDistanceForHudAndFadeOut < hud)
                        {
                            Capture(em, e, false, true, false);
                            c.IgnoreLineOfSight = los;
                            c.MaxDistanceForHudAndFadeOut = hud;
                            Write(em, e, c);
                        }
                    }
                }
            }
            finally { ents.Dispose(); }

            int tagged = AddFlightTags(em);

            if (_reportPending)
            {
                _reportPending = false;
                _log.LogInfo($"Boosting {seen} hideable units in the client world; {revealed} were "
                           + $"hidden and have been revealed. VisionRange={range}m keepRange={keep}m"
                           + (flightTag ? $" flightTagsAdded={tagged}" : " flightTag=OFF (units will"
                                                                       + " stay hidden while flying)")
                           + (hud > 0f ? $" hud={hud}m" : ""));
                if (_writesDisabled)
                    _log.LogError("...but writes are DISABLED, so nothing above was actually "
                                + "applied to Hideable/Vision/CheckOnScreen. Only the flight tag "
                                + "(which writes no struct data) took effect.");
                if (seen == 0)
                    _log.LogInfo("Nothing to reveal - no units are being streamed to you right "
                               + "now. That is the server's call, not this plugin's. Press the "
                               + "dump key to confirm.");
            }
        }

        /// <summary>
        /// Apply the queued VisibleFromFlight tags. Structural changes happen in one pass after
        /// the scan, and only for entities that lack the tag, so steady state costs nothing.
        ///
        /// Deliberately NOT gated on <c>_writesDisabled</c>. That flag guards struct marshalling,
        /// and the risk it protects against is writing the wrong number of bytes into chunk
        /// memory. VisibleFromFlight is a zero-field tag: adding it marshals nothing, so a size
        /// disagreement cannot make it unsafe. <see cref="Revert"/> still removes it cleanly.
        /// </summary>
        private int AddFlightTags(EntityManager em)
        {
            if (_needFlightTag.Count == 0) return 0;
            int added = 0;
            bool reported = false;
            for (int i = 0; i < _needFlightTag.Count; i++)
            {
                var e = _needFlightTag[i];
                try
                {
                    if (!em.Exists(e)) continue;   // version-checked; covers death during the scan
                    em.AddComponent(e, _flightTagType);
                    _taggedByUs[Key(e)] = e;       // remember, so Revert() undoes exactly this
                    added++;
                }
                catch (Exception ex)
                {
                    if (!reported)
                    {
                        reported = true;           // one line per tick, not one per entity
                        _log.LogError($"Could not add VisibleFromFlight: {ex.Message}");
                    }
                }
            }
            _needFlightTag.Clear();
            return added;
        }

        /// <summary>
        /// HybridModelSystem only instantiates a unit's model inside SHOW_INSIDE_RANGE_SQ, so a
        /// unit can be un-hidden and still have nothing to draw. This is a process-wide static,
        /// so the original is captured before the first write and put back by
        /// <see cref="RestoreModelShowRange"/>.
        /// </summary>
        private void ApplyModelShowRange()
        {
            float range = _cfg.ModelShowRange.Value;
            if (range <= 0f) { RestoreModelShowRange(); return; }   // 0 means "leave alone" - so restore

            float target = range * range;
            if (_modelRangeApplied && Mathf.Approximately(target, _appliedModelShowRangeSq)) return;
            try
            {
                if (!_modelRangeApplied) _originalModelShowRangeSq = HybridModelSystem.SHOW_INSIDE_RANGE_SQ;
                HybridModelSystem.SHOW_INSIDE_RANGE_SQ = target;
                _modelRangeApplied = true;
                _appliedModelShowRangeSq = target;
                _log.LogInfo($"Model show range {range}m (SHOW_INSIDE_RANGE_SQ {_originalModelShowRangeSq:0} -> {target:0}).");
            }
            catch (Exception e)
            {
                _appliedModelShowRangeSq = target;   // do not retry every tick
                _modelRangeApplied = false;
                _log.LogError($"Could not set model show range: {e.Message}");
            }
        }

        private void RestoreModelShowRange()
        {
            if (!_modelRangeApplied) return;
            try
            {
                HybridModelSystem.SHOW_INSIDE_RANGE_SQ = _originalModelShowRangeSq;
                _log.LogInfo($"Model show range restored to {_originalModelShowRangeSq:0} (squared).");
            }
            catch (Exception e) { _log.LogError($"Could not restore model show range: {e.Message}"); }
            _modelRangeApplied = false;
            _appliedModelShowRangeSq = -1f;
        }

        /// <summary>
        /// Widen your own vision radius. This is what the fog-of-war / line-of-sight reveal is
        /// driven from. Rewritten every tick rather than once, because the game applies buffs
        /// that overwrite it and a one-shot write would be silently undone by any of them.
        /// </summary>
        private void BoostLocalVision(EntityManager em, float range)
        {
            var locals = _localQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < locals.Length; i++)
                {
                    var e = locals[i];
                    if (!Has<Vision>(em, e)) continue;
                    var v = Read<Vision>(em, e);
                    if (v.Range._Value >= range) continue;
                    Capture(em, e, false, false, true);
                    v.Range._Value = range;
                    Write(em, e, v);
                }
            }
            finally { locals.Dispose(); }
        }

        // ---- diagnostics ----------------------------------------------------------
        /// <summary>
        /// Dump every unit the client currently knows about, with distance, hide state, blood
        /// quality and whether the glow selected it. The farthest entry is the measured edge of
        /// the server's streaming radius - the number that decides whether a unit is missing
        /// because the plugin failed or because it was never sent.
        /// </summary>
        public void Dump()
        {
            // Bypass the attach backoff. It exists to rate-limit the automatic retry in Tick();
            // applying it to a key the user just pressed would make the dump report "not ready"
            // without looking - and this dump is the first thing anyone reaches for when the
            // plugin seems to do nothing. A diagnostic that lies about why is worse than none.
            _nextAttachAttempt = 0f;
            if (!Ensure()) { _log.LogWarning("Client world not ready."); return; }
            var em = _client.EntityManager;

            Vector3 me = Vector3.zero;
            bool haveMe = false;
            var locals = _localQuery.ToEntityArray(Allocator.Temp);
            try
            {
                if (locals.Length > 0)
                {
                    haveMe = true;
                    var l = locals[0];
                    me = ToVec(Read<Translation>(em, l).Value);
                    var sb = new StringBuilder();
                    sb.Append($"local character {l.Index}:{l.Version} at {me}");
                    if (Has<Vision>(em, l)) sb.Append($" Vision.Range={Read<Vision>(em, l).Range._Value:0.#}");
                    _log.LogInfo(sb.ToString());
                }
                else _log.LogWarning("No LocalCharacter found - are you in a world?");
            }
            finally { locals.Dispose(); }

            float farthest = 0f;

            // Every hideable entity that has Health, i.e. anything character-shaped.
            int chars = 0;
            var all = _hideableQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < all.Length; i++)
                {
                    var e = all[i];
                    if (!Has<Health>(em, e) || !Has<Translation>(em, e)) continue;
                    var p = ToVec(Read<Translation>(em, e).Value);
                    float d = haveMe ? Vector3.Distance(p, me) : -1f;
                    float flat = haveMe ? new Vector2(p.x - me.x, p.z - me.z).magnitude : -1f;
                    float dy = haveMe ? p.y - me.y : 0f;
                    chars++;
                    if (d > farthest) farthest = d;
                    var hp = Read<Health>(em, e);
                    _log.LogInfo($"  CHAR [{e.Index}:{e.Version}] dist={d:0.0}m flat={flat:0.0}m "
                               + $"dy={dy:+0.0;-0.0;0.0}m hp={hp.Value:0}/{hp.MaxHealth._Value:0}"
                               + (Has<Hideable>(em, e) ? $" hidden={Read<Hideable>(em, e).IsHidden}" : "")
                               // Whether this unit can be seen while you fly. A dump full of
                               // hidden=True NO-FLIGHT-TAG taken airborne is the signature of
                               // VisibleFromFlight being off, not of a distance problem.
                               + (_flightTagUsable
                                  ? (em.HasComponent(e, _flightTagType) ? " +flightTag" : " NO-FLIGHT-TAG")
                                  : "")
                               + BloodInfo(em, e) + GlowInfo(em, e) + TypeInfo(em, e));

                    // Stand next to something and press the dump key to find out what it is,
                    // and - the reason the probe exists - why it is not the colour it should be.
                    float compRange = _cfg.DumpComponentsRange.Value;
                    if (compRange > 0f && haveMe && d >= 0f && d <= compRange)
                    {
                        LogComponents(em, e);
                        if (Glow != null) Glow.Probe(e);
                    }
                }
            }
            finally { all.Dispose(); }
            _log.LogInfo($"==== {chars} character-shaped entities (anything with Health) in the client world ====");

            // Disabled entities match no other query in this plugin, so a unit the game has
            // switched off would be invisible to every count above and look identical to
            // "never streamed".
            int disabled = 0;
            var dis = _disabledCharQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < dis.Length; i++)
                {
                    var e = dis[i];
                    var p = ToVec(Read<Translation>(em, e).Value);
                    float d = haveMe ? Vector3.Distance(p, me) : -1f;
                    disabled++;
                    _log.LogInfo($"  DISABLED-CHAR [{e.Index}:{e.Version}] dist={d:0.0}m "
                               + $"pos=({p.x:0.00},{p.y:0.00},{p.z:0.00})"
                               + BloodInfo(em, e) + TypeInfo(em, e)
                               + (Has<ImprisonedBuff>(em, e) ? " IMPRISONED" : "")
                               + " - ECS-disabled. A plain EntityQuery skips disabled entities, so "
                               + "the glow cannot select one even when its blood data is right here.");
                }
            }
            finally { dis.Dispose(); }
            if (disabled > 0)
                _log.LogInfo($"==== {disabled} DISABLED character entities. This plugin cannot reveal "
                           + "a disabled entity, and neither can the renderer - that is the game "
                           + "disabling it, not hiding it. ====");

            int hideable = _hideableQuery.CalculateEntityCount();
            _log.LogInfo($"==== {chars} units streamed to this client, farthest {farthest:0.0}m; "
                       + $"{hideable} hideable entities total ====");
            _log.LogInfo("'farthest' is the server's streaming radius, and nothing client-side can "
                       + "exceed it - units arrive with the world chunks and stay available to "
                       + "roughly 90m. If something you expected is not on this list, the server "
                       + "never sent it and no setting here will conjure it.");
        }
    }
}
