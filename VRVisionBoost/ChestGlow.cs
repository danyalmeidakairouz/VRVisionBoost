using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using ProjectM;
using ProjectM.Hybrid;
using ProjectM.Network;
using ProjectM.Sequencer;

namespace VRVisionBoost
{
    /// <summary>
    /// Tints world chests by prefab name so a golden chest is findable from the air.
    ///
    /// The identification is name-based, not GUID-based, and that is deliberate. Every entity the
    /// game instantiates carries <c>Stunlock.Core.PrefabGUID</c> (one int), and
    /// <c>PrefabLookupMap</c> turns that int back into the prefab's authoring name. So the plugin
    /// never ships a table of magic numbers that a patch can renumber underneath it - it asks the
    /// running game what each thing is called and matches a substring.
    ///
    /// The names, found in the game's baked entity scenes under
    /// <c>VRising_Data/StreamingAssets/EntityScenes</c>:
    ///
    ///   TM_WorldChest_Epic_01_Full / _Empty                  &lt;- the gold one
    ///   TM_WorldChest_Iron_01_Full / _Empty
    ///   TM_WorldChest_Simple_01_Full / _Empty
    ///   TM_WorldChest_Simple_GloomRot_01_Full / _Empty
    ///   TM_WorldChest_Simple_SludgePools_01_Full / _Empty
    ///
    /// Two things fall out of that naming which are worth more than the tint itself. "Epic" is the
    /// gold-trimmed chest - the art set behind it is EpicChest01_Lock / metal_EpicChest_mat - so
    /// "golden chest" is expressible as a name match. And <c>_Full</c> vs <c>_Empty</c> is in the
    /// prefab name, so the plugin can glow only what is still unlooted, and a chest visibly stops
    /// glowing once you empty it.
    ///
    /// THREE tint routes, because containers come in three rendering flavours and each route
    /// reaches exactly one of them. <see cref="ApplyStaticChildren"/> is the one that matters for
    /// world chests; the other two are inherited from BloodGlow and serve castle furniture.
    ///
    ///   static tile model | world chests      | write ShaderProperty_BlinkColor on each of the
    ///                     |                   | entity's StaticHierarchyBuffer render children
    ///   GameObject        | some castle props | write MaterialPropertyBlocks on its renderers
    ///   Rukhanka          | GPU-skinned props | hand a change to MaterialPropertySystem_Dots
    ///
    /// A world chest's gameplay entity carries no rendering components at all, so the two
    /// inherited routes are structurally incapable of touching it - which is why the static route
    /// exists and why "some containers tint and others do not" is expected rather than a bug.
    /// ReportDrops counts each route separately per scan; F10 breaks it down per chest.
    ///
    /// This file deliberately does NOT touch visibility. World chests carry no ProjectM.Hideable
    /// at all - they are not hidden by the fog of war in the first place - and any container that
    /// DOES carry it is already revealed and flight-tagged by VisionBooster's scan, which queries
    /// Hideable and skips only PlayerCharacter. A second set of Hideable writes here would be a
    /// second source of truth for the same fields.
    ///
    /// Revert semantics differ per route and that is not an inconsistency. The sequencer route
    /// self-reverts - ceasing to emit IS the revert, see BloodGlow. The other two WRITE state
    /// that persists, so both capture the original before the first write and put it back on
    /// rescan, on toggle-off and on shutdown.
    /// </summary>
    internal sealed class ChestGlow
    {
        private const float AttachRetrySeconds = 5f;

        /// <summary>How close an entity with an UNRESOLVABLE prefab name has to be before the
        /// dump lists it anyway, and how many such lines it will print. Bounds the escape hatch
        /// so it stays a diagnostic rather than a wall of unnamed scenery.</summary>
        private const float UnnamedDumpRange = 15f;
        private const int MaxUnnamedLines = 25;

        /// <summary>Most render children one static tile model is walked for. A world chest has
        /// eight; the headroom is for larger props, and the cap stops a malformed buffer from
        /// turning one scan into an unbounded walk.</summary>
        private const int MaxTintChildren = 64;

        private readonly Settings _cfg;
        private readonly ManualLogSource _log;

        private World _client;
        private MaterialPropertySystem_Hybrid _matProp;
        private MaterialPropertySystem_Dots _matPropDots;
        private HybridModelSystem _hybridModels;
        private PrefabLookupMap _lookup;
        private bool _lookupOk;

        private EntityQuery _prefabQuery;
        private EntityQuery _localQuery;
        private bool _queryBuilt;
        private bool _ready;
        private bool _layoutOk;
        private float _nextAttachAttempt;
        private float _nextScan;
        private bool _reportPending = true;

        // Renderers written by the renderer route, with the block each had BEFORE we touched it.
        // A MaterialPropertyBlock persists until something overwrites it, so the project's usual
        // rule applies in full here: assume nothing self-reverts, capture, put it back.
        private readonly List<UnityEngine.Renderer> _touched = new List<UnityEngine.Renderer>();
        private readonly List<MaterialPropertyBlock> _touchedOriginal = new List<MaterialPropertyBlock>();
        private MaterialPropertyBlock _scratch;

        private readonly List<Entity> _targets = new List<Entity>();
        private readonly List<float4> _colors = new List<float4>();

        /// <summary>
        /// Per target, whether the SEQUENCER route can reach it - i.e. whether it has a hybrid
        /// model at all. A world chest has none, so every per-frame AddChange aimed at one is
        /// work that cannot produce a pixel: roughly five interop calls per chest per frame, at
        /// whatever framerate the game is running. Decided on the scan so the per-frame loop only
        /// has a bool to check.
        /// </summary>
        private readonly List<bool> _sequencerReachable = new List<bool>();

        private enum Verdict : byte { Unknown = 0, No = 1, Full = 2, Empty = 3 }

        /// <summary>
        /// Verdict per prefab GUID, so a name is resolved at most once per prefab for the life of
        /// the attach rather than once per entity per scan. A castle full of containers is
        /// hundreds of entities across a handful of distinct prefabs, so this is the difference
        /// between a string lookup per entity and a dictionary probe per entity.
        /// </summary>
        private readonly Dictionary<int, Verdict> _verdict = new Dictionary<int, Verdict>();
        private readonly Dictionary<int, string> _names = new Dictionary<int, string>();

        // Never let a counter report suppressed work. Every one of these is the difference
        // between "8 chests glowing" meaning eight chests glowing and it meaning every single
        // write failed silently. Same rule as BloodGlow's _drop*/_emit* counters.
        private int _nameOk, _nameFailed;
        private int _dropNoGameObject, _dropNoRenderers, _dropApplyThrew, _dropSharedModel;
        private int _dropRukhanka, _noShaderProp;
        private int _staticChildrenPainted, _dropNoHierarchy, _dropNoTintableChild;
        private int _dropBufferUnreadable;
        private int _dropChildThrew, _dropChildWriteFailed;
        private bool _staticRouteOk;
        private bool _warnedNoLocal;
        private int _dupThisTarget;
        private int _paintedRenderers, _viaRendererComp, _viaPlainRenderers;
        private int _emitOk, _emitDead, _emitThrewHybrid, _emitThrewDots, _emitUnreachable;
        private string _lastDrops = "";

        public ChestGlow(Settings cfg, ManualLogSource log) { _cfg = cfg; _log = log; }

        /// <summary>
        /// Drop every tint. The static and renderer routes wrote state that persists, so both put
        /// their captured originals back; the sequencer route self-reverts, because ceasing to
        /// emit IS the revert. See the class comment.
        /// </summary>
        public void Reset()
        {
            RestoreRenderers();
            RestoreStaticChildren();
            _targets.Clear();
            _colors.Clear();
            _sequencerReachable.Clear();
            _reportPending = true;
        }

        public void Update()
        {
            if (!_cfg.ChestGlowEnabled.Value)
            {
                // Every kind of state this feature can leave behind, not just the target list:
                // switching off while only static children were tinted must still put them back.
                if (_targets.Count > 0 || _touched.Count > 0 || _childOriginals.Count > 0) Reset();
                return;
            }
            if (!Ensure()) return;

            float now = Time.unscaledTime;
            if (now >= _nextScan)
            {
                _nextScan = now + Mathf.Max(_cfg.ChestScanMs.Value, 100f) / 1000f;
                Rescan();
            }
            Emit();
        }

        // ---------------------------------------------------------------- scanning

        /// <summary>Rebuild the target list. Interval work, not per-frame work.</summary>
        private void Rescan()
        {
            // Tear down before rebuilding: a chest that has dropped out of the set, or that has
            // just been looted and so swapped to its _Empty prefab, must get its original block
            // back or the tint outlives its reason for existing.
            RestoreRenderers();
            RestoreStaticChildren();
            _targets.Clear();
            _colors.Clear();
            _sequencerReachable.Clear();
            if (!_layoutOk)
            {
                // Say it once per attach. Returning here skips ReportDrops and the "Chest glow
                // on:" line, so without this the feature goes completely silent and the only
                // evidence is a single VerifyChestLayout error that has long scrolled away.
                if (_reportPending)
                {
                    _reportPending = false;
                    _log.LogError("Chest glow is doing nothing: the PrefabGUID layout check "
                                + "failed at attach. Rebuild the plugin against the current "
                                + "interop assemblies.");
                }
                return;
            }

            // Game data can finish loading after the attach, so keep asking until it answers.
            // Cheap: one call per scan, and only while it has not yet succeeded.
            if (!_lookupOk && TryAcquireLookup())
                _log.LogInfo("PrefabLookupMap is available now - chest names can be resolved.");

            float4 fullColor = ParseColor(_cfg.ChestColor.Value, new float4(1f, 0.84f, 0f, 1f));
            float4 emptyColor = ParseColor(_cfg.ChestEmptyColor.Value, new float4(0.35f, 0.3f, 0.15f, 1f));
            float intensity = Mathf.Max(_cfg.ChestGlowIntensity.Value, 0f);
            bool unlootedOnly = _cfg.ChestUnlootedOnly.Value;
            float maxDist = Mathf.Max(_cfg.ChestGlowRange.Value, 0f);

            var em = _client.EntityManager;

            Vector3 me = Vector3.zero;
            bool haveMe = false;
            if (maxDist > 0f)
            {
                haveMe = TryGetLocalPosition(em, out me);
                // Without a position there is nothing to measure from, so the range limit below
                // evaporates and EVERY chest gets tinted. That is the expensive direction of the
                // very thing ChestGlowRange was set to avoid, so it does not get to happen
                // quietly. Not fatal and not worth failing closed - it resolves itself as soon as
                // the local character streams in, which is what this normally means.
                if (!haveMe && !_warnedNoLocal)
                {
                    _warnedNoLocal = true;
                    _log.LogWarning($"ChestGlowRange is set to {maxDist}m but your character's "
                                  + "position is not available yet, so the range limit is being "
                                  + "ignored and every known chest is tinted. This should sort "
                                  + "itself out within a scan or two of finishing loading.");
                }
                else if (haveMe) _warnedNoLocal = false;
            }

            WarnIfModeUnrecognised();

            _dropNoGameObject = _dropNoRenderers = _dropApplyThrew = _dropSharedModel = 0;
            _dropBufferUnreadable = 0;
            _dropRukhanka = _noShaderProp = 0;
            _staticChildrenPainted = _dropNoHierarchy = _dropNoTintableChild = 0;
            _dropChildThrew = _dropChildWriteFailed = 0;
            _paintedRenderers = _viaRendererComp = _viaPlainRenderers = 0;

            int scanned = 0;
            var ents = _prefabQuery.ToEntityArray(Allocator.Temp);
            try
            {
                scanned = ents.Length;
                for (int i = 0; i < ents.Length; i++)
                {
                    var e = ents[i];

                    int guid;
                    if (!TryReadPrefabGuid(em, e, out guid)) continue;

                    var v = Classify(guid);
                    if (v != Verdict.Full && v != Verdict.Empty) continue;
                    if (unlootedOnly && v == Verdict.Empty) continue;

                    // A tint lands on the MODEL. An entity whose model has not been instantiated
                    // has nothing to paint and nothing for the sequencer to reach, so emitting at
                    // it every frame is work that cannot produce a pixel. Checked here, on the
                    // scan interval, rather than per frame.
                    // Two ways to be tintable, and a world chest only has the second: its
                    // gameplay entity carries no rendering components, and its visuals live on
                    // StaticHierarchyBuffer children. Testing HybridModelUser alone selects
                    // nothing at all.
                    bool hasModel = HasModel(em, e);
                    if (!hasModel && !Has<ProjectM.StaticHierarchyBuffer>(em, e)) continue;

                    if (maxDist > 0f && haveMe)
                    {
                        Vector3 p;
                        if (!TryGetPosition(em, e, out p)) continue;
                        if (Vector3.Distance(p, me) > maxDist) continue;
                    }

                    var c = v == Verdict.Full ? fullColor : emptyColor;
                    c.xyz *= intensity;
                    _targets.Add(e);
                    _colors.Add(c);
                    // Deliberately the COMPONENT test, not HasModel(). HasModel also requires
                    // HybridEntity to be non-null, which is transient - it flips as soon as a
                    // model streams in - so latching it here would leave a prop untinted until
                    // the next scan. Whether the entity has HybridModelUser at all is structural:
                    // a world chest has none and never will, which is the case worth skipping.
                    _sequencerReachable.Add(Has<HybridModelUser>(em, e));
                }
            }
            finally { ents.Dispose(); }

            ApplyRenderers();
            ApplyStaticChildren();
            ReportDrops();

            if (_reportPending)
            {
                _reportPending = false;
                _log.LogInfo($"Chest glow on: {_targets.Count} of {scanned} prefab-carrying "
                           + $"entities match '{_cfg.ChestNameContains.Value}'"
                           + (unlootedOnly ? ", are still unlooted (_Full)," : "")
                           + " AND are tintable (a hybrid model, or static render children). Prefab names resolved: "
                           + $"{_nameOk} ok, {_nameFailed} unresolvable. Rescanning every "
                           + $"{_cfg.ChestScanMs.Value}ms.");
                if (_nameOk == 0 && _nameFailed > 0)
                    _log.LogError("Not one prefab name could be resolved, so nothing can ever "
                                + "match. PrefabLookupMap is not answering on this build - press "
                                + "the dump key, which prints the raw guid= of every entity near "
                                + "you, and match on those numbers instead.");
            }
        }

        /// <summary>
        /// Prefab GUID to verdict, resolved once per prefab. A miss resolves the name through
        /// PrefabLookupMap and caches whatever came back - including a failure, so a prefab whose
        /// name cannot be resolved is not re-asked on every scan forever.
        /// </summary>
        private Verdict Classify(int guid)
        {
            // Self-invalidate against the live config value. The verdict is DERIVED from
            // ChestNameContains, so a cache that is never compared back against the live .Value
            // is exactly the failure CLAUDE.md warns about: it looks like the CurrentProperty()
            // pattern and behaves like neither of the two things allowed, so the reload key would
            // appear to do nothing to this filter. The NAME cache survives a filter change -
            // a prefab's name does not depend on what we are searching for.
            string filter = _cfg.ChestNameContains.Value;
            if (!string.Equals(filter, _verdictFilter, StringComparison.Ordinal))
            {
                _verdictFilter = filter;
                _verdict.Clear();
            }

            Verdict cached;
            if (_verdict.TryGetValue(guid, out cached)) return cached;

            string name;
            if (!_names.TryGetValue(guid, out name) && !_unnamed.Contains(guid))
            {
                name = ResolveName(guid);
                if (string.IsNullOrEmpty(name))
                {
                    // Remember the failure so it is not re-asked once per entity per scan - but
                    // remember it SEPARATELY from the verdict, because the answer can still
                    // change: TryAcquireLookup clears this set the moment the lookup map first
                    // becomes available. Only counted when the map was actually asked; while it
                    // is unavailable every entity "fails", and letting that inflate the counter
                    // would both mislead and, since the count appears in the per-scan report
                    // line, defeat the change detection that keeps that line quiet.
                    if (_lookupOk) { _unnamed.Add(guid); _nameFailed++; }
                }
                else { _nameOk++; _names[guid] = name; }
            }

            Verdict v = Selects(guid, name, filter)
                      ? (IsEmptyName(name) ? Verdict.Empty : Verdict.Full)
                      : Verdict.No;

            // Do not remember an answer reached while the lookup map was unavailable: it can
            // still change once game data finishes loading, and caching No there would pin every
            // prefab in the world to "not a chest" for the rest of the attach, leaving a silently
            // dead feature with no way back. With the map available the answer is definitive -
            // either the name resolved, or the GUID is recorded as having no name on this build.
            if (_lookupOk) _verdict[guid] = v;
            return v;
        }

        /// <summary>
        /// Prefab GUIDs the lookup map was asked about and had no name for. Kept apart from
        /// <see cref="_verdict"/> so that a filter change does not force them all to be re-asked,
        /// and cleared when the lookup map first becomes available so a name that only exists
        /// after game data loads is not written off permanently.
        /// </summary>
        private readonly HashSet<int> _unnamed = new HashSet<int>();

        private string _verdictFilter = null;

        /// <summary>
        /// A looted chest swaps to the <c>_Empty</c> prefab, so "still worth walking to" is
        /// readable straight off the name. Anything not explicitly _Empty counts as full, so a
        /// container family that does not use the suffix still glows rather than silently never
        /// matching.
        /// </summary>
        private static bool IsEmptyName(string name)
            => !string.IsNullOrEmpty(name)
            && name.EndsWith("_Empty", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Does this entity match a filter? Comma-separated and case-insensitive. A token that
        /// parses as an integer is compared against the raw prefab GUID; every other token is
        /// matched as a substring of the prefab NAME.
        ///
        /// The GUID half exists because the name half can fail. When PrefabLookupMap cannot
        /// answer, a name-only filter leaves no way to select anything at all - and the log told
        /// people to "match on those numbers instead" when no code path existed that could do it.
        /// Now there is one: read <c>guid=</c> off the dump and paste it into the filter.
        ///
        /// A blank filter matches NOTHING, which is the safe direction - the other reading would
        /// tint the entire world the moment someone cleared the setting to see what it did.
        /// </summary>
        internal static bool Selects(int guid, string name, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return false;
            string[] parts = filter.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length == 0) continue;

                int wanted;
                if (int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out wanted))
                {
                    // A numeric token is a GUID and never a substring. Prefab names do contain
                    // digits, so treating "01" as both would quietly match half the world.
                    if (wanted == guid) return true;
                    continue;
                }
                if (!string.IsNullOrEmpty(name)
                    && name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>
        /// Bind the prefab name lookup, returning whether it is usable.
        ///
        /// Deliberately the STATIC GetPrefabLookupMap(World), which returns the struct BY VALUE -
        /// not the instance PrefabLookupMap property, which is declared <c>ref PrefabLookupMap</c>
        /// and is therefore exactly the shape of return Il2CppInterop cannot marshal and does not
        /// throw about. That distinction is what made TypeManager.GetTypeInfo().SizeInChunk
        /// silently hand back 640 for four different structs and disable every write in the
        /// plugin. Do not "simplify" this to the property.
        ///
        /// Retried from <see cref="Rescan"/> while it has not succeeded, because game data can
        /// finish loading after the attach and a one-shot attempt would leave the feature dead
        /// for the session with nothing in the log to say why.
        /// </summary>
        private bool TryAcquireLookup()
        {
            bool wasOk = _lookupOk;
            try
            {
                _lookup = PrefabCollectionSystem.GetPrefabLookupMap(_client);
                _lookupOk = _lookup.IsCreated;
                // Anything written off as nameless was written off without the map. Give those
                // prefabs one more chance now that there is something to ask.
                if (_lookupOk && !wasOk) { _unnamed.Clear(); _verdict.Clear(); }
            }
            catch (Exception e)
            {
                if (_lookupOk || _lookupError == null || !string.Equals(_lookupError, e.Message, StringComparison.Ordinal))
                {
                    _lookupError = e.Message;   // once per distinct failure, not once per scan
                    _log.LogError("PrefabLookupMap could not be resolved, so prefab names cannot "
                                + $"be read and no chest can be identified: {e.Message}");
                }
                _lookupOk = false;
            }
            return _lookupOk;
        }

        private string _lookupError;

        /// <summary>
        /// Prefab GUID to authoring name. Returns null when the lookup cannot answer, which is a
        /// real and expected outcome - a prefab the client has not converted has no entry - and
        /// must never be confused with "this is not a chest".
        /// </summary>
        private string ResolveName(int guid)
        {
            if (!_lookupOk) return null;
            try
            {
                string name;
                if (_lookup.TryGetName(PrefabGUID.CreateUnsafe(guid), out name)
                    && !string.IsNullOrEmpty(name))
                    return name;
            }
            catch { /* lookup map torn down, or this build marshals it differently */ }
            return null;
        }

        // ---------------------------------------------------------------- rendering

        /// <summary>
        /// Write the tint onto the model's renderers.
        ///
        /// This differs from BloodGlow in one load-bearing way. BloodGlow reaches a character's
        /// renderers through HybridModelRendererComponent, which is CHARACTER machinery - it is
        /// what the game hangs customization, transmog and dissolve off. A world chest is a tile
        /// model and has no reason to carry it, so going through that component alone would drop
        /// every chest at noRendererComp and look exactly like "chests cannot be tinted".
        ///
        /// So: prefer the component when it is there, since it knows which renderers are the real
        /// visual ones, and fall back to the GameObject's own child renderers when it is not. The
        /// two are counted separately so the log says which path actually reached the chest rather
        /// than leaving it to be guessed.
        ///
        /// GetPropertyBlock BEFORE writing is not optional: SetPropertyBlock replaces a renderer's
        /// block wholesale, so a block containing only our colour would destroy whatever else the
        /// game had put there. Copy what is there, add one key, put it back.
        /// </summary>
        private void ApplyRenderers()
        {
            if (!RendererMode || _hybridModels == null || _targets.Count == 0) return;
            if (_scratch == null) _scratch = new MaterialPropertyBlock();

            int propId = string.Equals(_cfg.ChestRendererProperty.Value, "BaseColor",
                                       StringComparison.OrdinalIgnoreCase)
                       ? HybridModelRendererComponent.BaseColor
                       : HybridModelRendererComponent.EmissiveColor;

            var em = _client.EntityManager;
            var viaComponent = new Il2CppSystem.Collections.Generic.List<UnityEngine.Renderer>();

            for (int i = 0; i < _targets.Count; i++)
            {
                GameObject go = ResolveModel(em, _targets[i]);
                if (go == null)
                {
                    _dropNoGameObject++;
                    // Separate "no model yet" from "GPU-skinned through DOTS, and the renderer
                    // route can never touch it". The first is transient and resolves itself; the
                    // second is permanent and means Sequencer mode is the only option. Collapsing
                    // them leaves noGameObject=8 unreadable, which is the first question anyone
                    // has after turning this on.
                    if (ModelTypeOf(em, _targets[i]) == HybridModelType.Rukhanka) _dropRukhanka++;
                    continue;
                }

                var c = _colors[i];
                var colour = new Color(c.x, c.y, c.z, 1f);
                _dupThisTarget = 0;

                try
                {
                    HybridModelRendererComponent hr = null;
                    try { hr = go.GetComponentInChildren<HybridModelRendererComponent>(); }
                    catch { }

                    int painted = 0;
                    if (hr != null)
                    {
                        viaComponent.Clear();
                        hr.GetAllRenderers(viaComponent);
                        painted = PaintList(viaComponent, propId, colour);
                        if (painted > 0) _viaRendererComp++;
                    }
                    // Fall back whenever the component route produced nothing, not merely when
                    // the component is absent. The whole argument for having a fallback - a chest
                    // is a tile model and has no reason to carry character machinery - applies
                    // just as well to a component that is present but hands back an empty list.
                    if (painted == 0 && _dupThisTarget == 0)
                    {
                        painted = PaintArray(go.GetComponentsInChildren<UnityEngine.Renderer>(),
                                             propId, colour);
                        if (painted > 0) _viaPlainRenderers++;
                    }

                    // "Produced no pixel" has two causes and they mean opposite things. No
                    // renderers at all is a real problem worth chasing; every renderer already
                    // painted this scan means this target shares a model with one already done,
                    // which is correct behaviour and must not be reported as a failure.
                    if (painted == 0) { if (_dupThisTarget > 0) _dropSharedModel++; else _dropNoRenderers++; }
                }
                catch { _dropApplyThrew++; /* model torn down mid-walk; next scan retries */ }
            }
        }

        private int PaintList(Il2CppSystem.Collections.Generic.List<UnityEngine.Renderer> list,
                              int propId, Color colour)
        {
            int painted = 0;
            for (int r = 0; r < list.Count; r++) if (Paint(list[r], propId, colour)) painted++;
            return painted;
        }

        private int PaintArray(Il2CppArrayBase<UnityEngine.Renderer> arr, int propId, Color colour)
        {
            if (arr == null) return 0;
            int painted = 0;
            for (int r = 0; r < arr.Length; r++) if (Paint(arr[r], propId, colour)) painted++;
            return painted;
        }

        private bool Paint(UnityEngine.Renderer rend, int propId, Color colour)
        {
            if (rend == null) return false;

            // Paint any given renderer at most once per scan. Two target entities CAN resolve to
            // one GameObject - ResolveModel falls back to the shared model entity - and without
            // this the second visit would call GetPropertyBlock on a renderer we had already
            // written, store OUR colour as its "original", and then RestoreRenderers would put
            // that back and make the tint permanent. The set is keyed by instance id rather than
            // by scanning _touched, so a model with many renderers stays O(n) rather than O(n^2).
            int id;
            try { id = rend.GetInstanceID(); }
            catch { return false; }
            if (!_paintedThisScan.Add(id)) { _dupThisTarget++; return false; }

            // Writing a property the shader does not declare is SILENT: Unity stores it in the
            // block and no pixel changes. That matters more here than in BloodGlow, because the
            // property IDs are HybridModelRendererComponent.BaseColor/.EmissiveColor - baked by
            // the game for CHARACTER shaders - while a chest is a tile model. Without this, a run
            // in which every chest shader ignored the write would log painted=37 and read exactly
            // like a run that worked, which is the failure this project has a rule about.
            //
            // Counted, never used to skip the write: HasProperty can disagree with a material
            // variant, and refusing to paint on its say-so would turn a diagnostic into a new
            // way to fail.
            try
            {
                var mat = rend.sharedMaterial;
                if (mat != null && !mat.HasProperty(propId)) _noShaderProp++;
            }
            catch { }

            var original = new MaterialPropertyBlock();
            rend.GetPropertyBlock(original);   // whatever the game last set
            _touched.Add(rend);
            _touchedOriginal.Add(original);

            // Clear before reuse. GetPropertyBlock is documented to overwrite the destination,
            // but _scratch is shared across every renderer in a scan, so one line here removes
            // any question of a key leaking from renderer N-1 into renderer N.
            _scratch.Clear();
            rend.GetPropertyBlock(_scratch);
            _scratch.SetColor(propId, colour);
            rend.SetPropertyBlock(_scratch);
            _paintedRenderers++;
            return true;
        }

        /// <summary>
        /// Tint a static tile model by writing the game's own per-entity shader-property
        /// override on each of its render children. This is the only route that reaches a world
        /// chest, and the reason is structural rather than incidental.
        ///
        /// A world chest's gameplay entity carries NO rendering components at all - no
        /// RenderBounds, no MaterialMeshInfo, no HybridModelUser. The other two routes both
        /// resolve through HybridModelUser, so neither can ever touch one. The visuals live on
        /// the entity's <c>StaticHierarchyBuffer</c> children, each a real Entities Graphics
        /// renderable carrying <c>ProjectM.Presentation.ShaderProperty_BlinkColor</c> - the
        /// game's own override for the same channel BloodGlow drives through the sequencer. The
        /// geometry is NOT merged into a combined static batch, so each piece is addressable.
        ///
        /// This route WRITES a component, so unlike the sequencer route it does not self-revert.
        /// The project's standing rule applies in full: capture the original before the first
        /// write and put it back. <see cref="Rescan"/> tears down and re-applies every scan, so
        /// a child is written twice per scan in steady state - cheap at ChestScanMs=1000, and it
        /// is precisely what makes a mode, filter or colour change revert within one scan with no
        /// per-setting unwind to forget.
        /// </summary>
        private void ApplyStaticChildren()
        {
            if (!StaticMode || !_staticRouteOk || _targets.Count == 0) return;
            var em = _client.EntityManager;

            for (int i = 0; i < _targets.Count; i++)
            {
                var c = _colors[i];
                var want = new float4(c.x, c.y, c.z, 1f);

                string why;
                if (!TryReadBuffer(em, _targets[i], MaxTintChildren, ref _kidScratch, out why)
                    || _kidScratch == null)
                {
                    // "empty" is the ordinary case - this entity simply has no static hierarchy.
                    // Anything else means the buffer could not be READ, which looks identical in
                    // the counters but means something entirely different: a stride mismatch
                    // after a game patch, or a throw. Booking both as noHierarchy sends people
                    // hunting through ChestNameContains for a problem that is not there.
                    if (string.Equals(why, "empty", StringComparison.Ordinal)) _dropNoHierarchy++;
                    else
                    {
                        _dropBufferUnreadable++;
                        if (!string.Equals(why, _lastBufferWhy, StringComparison.Ordinal))
                        {
                            _lastBufferWhy = why;
                            _log.LogError("Could not read StaticHierarchyBuffer, so no world chest "
                                        + $"can be tinted: {why}");
                        }
                    }
                    continue;
                }

                var kids = _kidScratch;
                int painted = 0;
                int shared = 0;
                for (int k = 0; k < kids.Length; k++)
                {
                    var child = kids[k].Entity;
                    if (child == Entity.Null) continue;

                    bool alive;
                    try { alive = em.Exists(child); } catch { continue; }
                    if (!alive) continue;

                    // Not every child of a tile model is a renderable - colliders and anchors
                    // ride the same buffer. No property, nothing to tint, not a failure.
                    if (!Has<ProjectM.Presentation.ShaderProperty_BlinkColor>(em, child)) continue;

                    // One write per child per scan, and never twice: a second visit would capture
                    // OUR colour as the "original" and make the tint permanent on revert. Same
                    // hazard the renderer route guards with _paintedThisScan.
                    ulong key = Key(child);
                    if (!_childThisScan.Add(key)) { shared++; continue; }

                    ProjectM.Presentation.ShaderProperty_BlinkColor cur;
                    try { cur = Read<ProjectM.Presentation.ShaderProperty_BlinkColor>(em, child); }
                    catch { _dropChildThrew++; continue; }

                    // Captured unconditionally, and that is only safe because Rescan()
                    // restored and cleared _childOriginals before this ran - so cur holds the
                    // game's own value, never ours - and _childThisScan already blocks a second
                    // visit within the scan. If the teardown at the top of Rescan is ever
                    // removed, this needs a first-write-only guard again, or it will record the
                    // plugin's colour as the original and make the tint permanent.
                    _childOriginals[key] = new ChildOriginal { Entity = child, Value = cur.Value };

                    // Rare: the child's own colour already equals the tint, so writing would be a
                    // no-op. It still counts, because staticChildren= reports how many children
                    // ARE holding the tint - the state worth knowing - and a failed write is
                    // booked separately below, so the number can never claim work that did not
                    // happen.
                    if (cur.Value.Equals(want)) { painted++; continue; }

                    cur.Value = want;
                    if (Write(em, child, cur)) painted++;
                    else _dropChildWriteFailed++;
                }

                // Same asymmetry the renderer route already books as sharedModel: a target
                // whose every child was painted by an earlier target IS tinted, and reporting it
                // as an untintable failure would be a counter claiming a problem that is not one.
                if (painted > 0) _staticChildrenPainted += painted;
                else if (shared > 0) _dropSharedModel++;
                else _dropNoTintableChild++;
            }
        }

        /// <summary>
        /// Put every shader-property override back exactly as it was found.
        ///
        /// Iterated over a dictionary keyed by packed (Index, Version), so a recycled entity id
        /// cannot be mistaken for one we wrote, and <c>em.Exists</c> version-checks it again
        /// before the write. Same discipline as VisionBooster's RestoreOriginals.
        /// </summary>
        private void RestoreStaticChildren()
        {
            if (_childOriginals.Count > 0 && _client != null && _client.IsCreated)
            {
                var em = _client.EntityManager;
                foreach (var kv in _childOriginals)
                {
                    try
                    {
                        var e = kv.Value.Entity;
                        if (!em.Exists(e)) continue;
                        if (!Has<ProjectM.Presentation.ShaderProperty_BlinkColor>(em, e)) continue;
                        var v = Read<ProjectM.Presentation.ShaderProperty_BlinkColor>(em, e);
                        v.Value = kv.Value.Value;
                        Write(em, e, v);
                    }
                    catch { /* entity went away with its chunk; nothing to restore onto */ }
                }
            }
            _childOriginals.Clear();
            _childThisScan.Clear();
        }

        private struct ChildOriginal
        {
            public Entity Entity;
            public float4 Value;
        }

        private readonly Dictionary<ulong, ChildOriginal> _childOriginals =
            new Dictionary<ulong, ChildOriginal>();
        private readonly HashSet<ulong> _childThisScan = new HashSet<ulong>();

        /// <summary>Reused child buffer for the tint walk - see TryReadBuffer's items param.</summary>
        private ProjectM.StaticHierarchyBuffer[] _kidScratch;

        private static ulong Key(Entity e) => ((ulong)(uint)e.Index << 32) | (uint)e.Version;

        /// <summary>
        /// Write a component. Gated on <see cref="_staticRouteOk"/> and returns whether the write
        /// actually happened, so a counter can never report suppressed work - the rule this
        /// project learned the hard way when a disabled run still logged "2 revealed".
        /// </summary>
        private unsafe bool Write<T>(EntityManager em, Entity e, T value) where T : struct
        {
            if (!_staticRouteOk) return false;
            try
            {
                var ct = Ct<T>.Value;
                void* p = em.GetComponentDataRawRW(e, ct.TypeIndex);
                if (p == null) return false;
                Marshal.StructureToPtr(value, new IntPtr(p), false);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Put every renderer we wrote back exactly as we found it.</summary>
        private void RestoreRenderers()
        {
            for (int i = 0; i < _touched.Count; i++)
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
            _paintedThisScan.Clear();
        }

        private readonly HashSet<int> _paintedThisScan = new HashSet<int>();

        private bool RendererMode =>
            _cfg.ChestGlowMode.Value.IndexOf("Renderer", StringComparison.OrdinalIgnoreCase) >= 0
            || string.Equals(_cfg.ChestGlowMode.Value, "Both", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The static-tile-model route: write ShaderProperty_BlinkColor on each render child.
        /// This is the ONLY route that reaches a world chest - measured - so "Both" includes it.
        /// </summary>
        /// <summary>
        /// Whether the configured mode names any route at all. A typo makes all three predicates
        /// false and the feature does nothing while the log prints three zeroed route counters -
        /// which reads as three broken routes rather than one bad string. ChestGlowProperty
        /// already warns for exactly this; the mode did not.
        /// </summary>
        private void WarnIfModeUnrecognised()
        {
            string mode = _cfg.ChestGlowMode.Value;
            if (string.Equals(mode, _lastModeWarned, StringComparison.Ordinal)) return;
            _lastModeWarned = mode;
            if (StaticMode || RendererMode || SequencerMode) return;
            _log.LogWarning($"ChestGlowMode '{mode}' names none of Static, Renderer, Sequencer or "
                          + "Both, so the chest glow will select targets and then tint nothing. "
                          + "Set it to Both unless you have a reason not to.");
        }

        private string _lastModeWarned = null;
        private string _lastBufferWhy = null;

        private bool StaticMode =>
            _cfg.ChestGlowMode.Value.IndexOf("Static", StringComparison.OrdinalIgnoreCase) >= 0
            || string.Equals(_cfg.ChestGlowMode.Value, "Both", StringComparison.OrdinalIgnoreCase);

        private bool SequencerMode =>
            _cfg.ChestGlowMode.Value.IndexOf("Sequencer", StringComparison.OrdinalIgnoreCase) >= 0
            || string.Equals(_cfg.ChestGlowMode.Value, "Both", StringComparison.OrdinalIgnoreCase);

        /// <summary>Hold the tint. Every frame - the game drains its change list per frame.</summary>
        private void Emit()
        {
            if (!SequencerMode || _targets.Count == 0) return;

            int importance = _cfg.ChestGlowImportance.Value;
            var prop = CurrentProperty();      // cached; re-parsed only when the setting changes
            bool isColour = _propertyIsColour;
            var em = _client.EntityManager;

            for (int i = 0; i < _targets.Count; i++)
            {
                // Both AddChange calls below resolve through a hybrid model. A world chest has
                // none - it is drawn by its static render children - so emitting at one every
                // frame cannot produce a pixel and costs about four interop calls per frame for
                // nothing. Decided on the scan; this is just a bool.
                //
                // COUNTED, not silently skipped. The premise - that AddChange cannot serve an
                // entity with no HybridModelUser - is an assertion about IL2CPP stub internals
                // that cannot be established statically, exactly like the BatFog `active` flag.
                // If it is wrong for some archetype, the symptom is "props that used to glow
                // stopped", and without this counter there would be no number in the log
                // pointing at the line that did it.
                if (i < _sequencerReachable.Count && !_sequencerReachable[i])
                {
                    _emitUnreachable++;
                    continue;
                }

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
                catch { _emitThrewHybrid++; }

                if (_matPropDots != null)
                {
                    // Aim the DOTS call at the MODEL entity when there is one, for the reason
                    // BloodGlow documents: _matProp resolves through HybridModelSystem, which is
                    // GameObject-only, so anything drawn through DOTS is only reachable here.
                    var model = ModelEntityOf(em, e);
                    var dotsTarget = model != Entity.Null && em.Exists(model) ? model : e;
                    var dotsChange = change;   // AddChange takes it by ref and may write back
                    try { _matPropDots.AddChange(dotsTarget, importance, ref dotsChange); }
                    catch { _emitThrewDots++; }
                }
            }
        }

        /// <summary>One line, only when the picture changes, saying what became of the targets.</summary>
        private void ReportDrops()
        {
            // Emit runs per frame, so raw emit counts are targets x frames-since-last-scan and
            // would defeat the change detection below, printing a near-identical line forever.
            // Collapse them to the state they are evidence of; a count that only moves when
            // something breaks stays a count.
            string emit = _emitOk > 0 && _emitThrewHybrid == 0 ? "ok"
                        : _emitOk > 0 ? "partial"
                        : _emitThrewHybrid > 0 ? "ALL-THREW"
                        : "idle";
            string s = $"targets={_targets.Count} staticChildren={_staticChildrenPainted} "
                     + $"noHierarchy={_dropNoHierarchy} noTintableChild={_dropNoTintableChild} "
                     + (_dropBufferUnreadable > 0 ? $"bufferUnreadable={_dropBufferUnreadable} " : "")
                     + (_dropChildThrew > 0 || _dropChildWriteFailed > 0
                        ? $"childThrew={_dropChildThrew} childWriteFailed={_dropChildWriteFailed} " : "")
                     + $"painted={_paintedRenderers} "
                     + $"viaRendererComp={_viaRendererComp} viaPlainRenderers={_viaPlainRenderers} "
                     + $"noGameObject={_dropNoGameObject} (rukhanka={_dropRukhanka}) "
                     + $"noRenderers={_dropNoRenderers} noShaderProp={_noShaderProp} "
                     + $"applyThrew={_dropApplyThrew} sharedModel={_dropSharedModel} "
                     // Only when there is something wrong. _nameOk climbs by one every time a
                     // new prefab streams in, so including it unconditionally made this string
                     // change on nearly every scan and defeated the change detection below -
                     // measured in game as a fresh log line every second saying nothing new.
                     // A count that only moves when something breaks stays a count.
                     + (_nameFailed > 0 ? $"names={_nameOk}ok/{_nameFailed}fail " : "")
                     + $"emit={emit}"
                     + (_emitUnreachable > 0 ? " emitSkippedNoHybrid=some" : "")
                     + (_emitDead > 0 ? " emitDead=some" : "")
                     + (_emitThrewDots > 0 ? " dotsThrew=some" : "");
            _emitOk = _emitDead = _emitThrewHybrid = _emitThrewDots = _emitUnreachable = 0;
            if (string.Equals(s, _lastDrops, StringComparison.Ordinal)) return;
            _lastDrops = s;
            _log.LogInfo($"Chest glow [{_cfg.ChestGlowMode.Value}]: {s}");
        }

        // ---------------------------------------------------------------- the dump

        /// <summary>
        /// The measurement this feature sits on top of, and the thing to read before believing
        /// anything about why a chest is or is not glowing.
        ///
        /// It answers, per chest, the four questions that static analysis of the interop
        /// assemblies genuinely cannot: is the entity reaching this client at all and at what
        /// distance; does it carry Hideable, and therefore is VisionBooster already revealing and
        /// flight-tagging it; does it carry HybridModelUser and with which ModelType, which gates
        /// the renderer and sequencer routes; and how many static render children the static
        /// route can tint, reported as children=x/y.
        ///
        /// It lists by a SEPARATE, wider filter than the one driving the tint, so the real prefab
        /// names on this build can be discovered without having guessed them first. The raw guid=
        /// is always printed, so the dump stays usable even when name resolution is what broke.
        /// </summary>
        public void DumpChests(EntityManager em, Vector3 me, bool haveMe,
                               ComponentType flightTag, bool flightTagUsable)
        {
            // Never let the retry throttle put words in the dump's mouth - same reasoning as
            // VisionBooster.Dump clearing its own _nextAttachAttempt.
            _nextAttachAttempt = 0f;
            if (!Ensure())
            {
                _log.LogInfo("==== chest scan unavailable: not attached to the client world ====");
                return;
            }
            if (!_layoutOk)
            {
                _log.LogInfo("==== chest scan disabled: the PrefabGUID layout check failed ====");
                return;
            }

            string discover = _cfg.ChestDumpFilter.Value;
            string tintFilter = _cfg.ChestNameContains.Value;
            int listed = 0, hideableCount = 0, modelled = 0;
            // Counted here rather than read off _nameOk/_nameFailed. Those are only touched by
            // Classify(), which the dump never calls, and Ensure() zeroes them - so with the
            // feature switched off (the default) the footer used to claim "0 resolved, 0
            // unresolvable" after successfully resolving forty names, and printed that same
            // string when nothing could resolve at all. One line meaning both total success and
            // total failure is the thing this project has a rule against.
            int dumpNameOk = 0, dumpNameFail = 0;
            int unnamedShown = 0, unnamedHidden = 0;
            float farthest = 0f;


            var ents = _prefabQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < ents.Length; i++)
                {
                    var e = ents[i];
                    int guid;
                    if (!TryReadPrefabGuid(em, e, out guid)) continue;

                    string name;
                    if (!_names.TryGetValue(guid, out name))
                    {
                        name = ResolveName(guid);
                        if (!string.IsNullOrEmpty(name)) _names[guid] = name;
                    }
                    if (string.IsNullOrEmpty(name)) dumpNameFail++; else dumpNameOk++;

                    Vector3 p;
                    bool havePos = TryGetPosition(em, e, out p);
                    float d = haveMe && havePos ? Vector3.Distance(p, me) : -1f;
                    float dy = haveMe && havePos ? p.y - me.y : 0f;

                    // An entity whose name will not resolve can never match a name filter, so
                    // filtering it out is what left the dump printing NOTHING in precisely the
                    // state it exists to diagnose - and then blaming the filter, which no amount
                    // of widening could fix. Print those too, so the raw guid= is there to paste
                    // into ChestNameContains. Close by and capped, or a world full of unnamed
                    // prefabs would bury the chests this is meant to find.
                    if (!Selects(guid, name, discover))
                    {
                        if (!string.IsNullOrEmpty(name)) continue;
                        if (d < 0f || d > UnnamedDumpRange || unnamedShown >= MaxUnnamedLines)
                        { unnamedHidden++; continue; }
                        unnamedShown++;
                    }

                    listed++;
                    if (d > farthest) farthest = d;

                    bool hasHideable = Has<Hideable>(em, e);
                    if (hasHideable) hideableCount++;

                    var sb = new StringBuilder();
                    sb.Append($"  CHEST [{e.Index}:{e.Version}] guid={guid} name={name ?? "?"}");
                    sb.Append(d >= 0f ? $" dist={d:0.0}m dy={dy:+0.0;-0.0;0.0}m" : " dist=?");

                    sb.Append(Selects(guid, name, tintFilter) ? " MATCHES-FILTER" : " (not matched)");
                    if (!string.IsNullOrEmpty(name))
                        sb.Append(IsEmptyName(name) ? " LOOTED" : " unlooted");

                    // Whether the vision half owns this entity's visibility. Its scan queries
                    // Hideable and skips only PlayerCharacter, so a chest carrying Hideable is
                    // already revealed and flight-tagged there. World chests carry none, so there
                    // is nothing for it to reveal and nothing to undo.
                    if (hasHideable)
                    {
                        var h = Read<Hideable>(em, e);
                        sb.Append($" hideable=yes hidden={h.IsHidden} vis={h.Visibility:0.##} "
                                + $"addHideSq={h.AdditionalHideRangeSq:0} ignoreLoS={h.IgnoreLoS}");
                    }
                    else sb.Append(" hideable=no");

                    if (flightTagUsable)
                    {
                        bool tagged = false;
                        try { tagged = em.HasComponent(e, flightTag); } catch { }
                        sb.Append(tagged ? " +flightTag" : " NO-FLIGHT-TAG");
                    }

                    // Which tint route can reach it.
                    if (Has<HybridModelUser>(em, e))
                    {
                        var hm = Read<HybridModelUser>(em, e);
                        modelled++;
                        sb.Append($" model={hm.ModelType} hasModelEntity={hm.HybridEntity != Entity.Null}");
                        var go = ResolveModel(em, e);
                        if (go == null) sb.Append(" gameObject=NO");
                        else
                        {
                            int n = 0;
                            try
                            {
                                var arr = go.GetComponentsInChildren<UnityEngine.Renderer>();
                                n = arr == null ? 0 : arr.Length;
                            }
                            catch { }
                            bool hasComp = false;
                            try { hasComp = go.GetComponentInChildren<HybridModelRendererComponent>() != null; }
                            catch { }
                            sb.Append($" gameObject=yes renderers={n} "
                                    + $"rendererComp={(hasComp ? "yes" : "no")}");
                        }
                    }
                    else sb.Append(" model=none");

                    // The static-tile route, which is what a world chest is actually tinted
                    // through. "children=8/8" means eight render children, all of them carrying
                    // the shader property - i.e. fully tintable.
                    int total, tintable;
                    if (CountTintableChildren(em, e, out total, out tintable))
                        sb.Append($" children={tintable}/{total}");

                    _log.LogInfo(sb.ToString());
                }
            }
            finally { ents.Dispose(); }

            _log.LogInfo($"==== {listed} entities match the chest dump filter '{discover}'; "
                       + $"{hideableCount} carry Hideable, {modelled} carry HybridModelUser, "
                       + $"farthest {farthest:0.0}m. Prefab names this dump: {dumpNameOk} "
                       + $"resolved, {dumpNameFail} unresolvable"
                       + (_lookupOk ? "" : " (PrefabLookupMap UNAVAILABLE - no name can resolve)")
                       + (unnamedShown > 0 ? $"; {unnamedShown} unnamed entities within "
                                           + $"{UnnamedDumpRange}m listed anyway" : "")
                       + (unnamedHidden > 0 ? $"; {unnamedHidden} further unnamed entities not "
                                            + "listed" : "")
                       + " ====");

            if (dumpNameFail > 0 && dumpNameOk == 0)
                _log.LogInfo("No prefab name resolved at all. Match on the numbers instead: take "
                           + "a guid= from a CHEST line above, put it in ChestNameContains (it "
                           + "accepts raw GUIDs as well as name substrings, comma separated) and "
                           + "press the reload key.");
            else if (listed == 0)
                _log.LogInfo("Nothing matched. Either no container is streamed to you right now, "
                           + "or the prefab names on this build differ from the filter - widen "
                           + "ChestDumpFilter (try 'TM_' or 'Chest'), press the reload key, and "
                           + "dump again standing next to a chest you can actually see.");
        }

        // ---------------------------------------------------------------- attach

        private bool Ensure()
        {
            if (_client != null && _client.IsCreated && _ready) return true;

            float now = Time.unscaledTime;
            if (now < _nextAttachAttempt) return false;
            _nextAttachAttempt = now + AttachRetrySeconds;

            // Entity keys are per-world and these renderers belong to the old world's models, so
            // they are dropped across a reattach rather than carried over. Same hazard and same
            // answer as BloodGlow.Ensure and VisionBooster.Ensure.
            RestoreRenderers();
            RestoreStaticChildren();
            _targets.Clear();
            _colors.Clear();
            _sequencerReachable.Clear();

            // The name caches are keyed by prefab GUID, which is game data rather than world
            // state - but the lookup map they were answered from belongs to the world being
            // replaced, so they are rebuilt rather than trusted. It costs one resolve per distinct
            // prefab, once, and removes a whole class of "stale after reconnect" question.
            _verdict.Clear();
            _names.Clear();
            _unnamed.Clear();
            _nameOk = _nameFailed = 0;

            if (_queryBuilt)
            {
                try { _prefabQuery.Dispose(); } catch { }
                try { _localQuery.Dispose(); } catch { }
                _queryBuilt = false;
            }

            _client = null;
            _ready = false;
            _matProp = null;
            _lookupOk = false;

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
                _log.LogError("MaterialPropertySystem_Hybrid could not be resolved, so chest glow "
                            + $"is off (everything else still works): {e.Message}");
                return false;
            }
            if (_matProp == null)
            {
                _log.LogError("MaterialPropertySystem_Hybrid is not present in the client world, "
                            + "so chest glow cannot run.");
                return false;
            }

            // The name lookup. Deliberately the STATIC GetPrefabLookupMap(World), which returns
            // the struct BY VALUE - not the instance PrefabLookupMap property, which is declared
            // `ref PrefabLookupMap` and is therefore exactly the shape of return Il2CppInterop
            // cannot marshal and does not throw about. That distinction is what made
            // TypeManager.GetTypeInfo().SizeInChunk silently hand back 640 for four different
            // structs and disable every write in the plugin. Do not "simplify" this to the
            // property.
            if (!TryAcquireLookup())
                _log.LogWarning("PrefabLookupMap is not available yet, so no prefab name can be "
                              + "resolved and no chest identified. This is normal before game "
                              + "data has finished loading - it is retried on every scan, so the "
                              + "feature comes back on its own rather than needing a reattach.");

            try
            {
                var em = _client.EntityManager;
                _prefabQuery = em.CreateEntityQuery(new[] {
                    ComponentType.ReadOnly<PrefabGUID>(), ComponentType.ReadOnly<Translation>() });
                _localQuery = em.CreateEntityQuery(new[] {
                    ComponentType.ReadOnly<LocalCharacter>(), ComponentType.ReadOnly<Translation>() });
                _queryBuilt = true;
            }
            catch (Exception e)
            {
                _log.LogError("Could not create the chest query (retrying in "
                            + $"{AttachRetrySeconds}s): {e.Message}");
                return false;
            }

            _layoutOk = VerifyChestLayout();
            _ready = true;
            _reportPending = true;
            _log.LogInfo($"Chest glow attached to client world. Mode={_cfg.ChestGlowMode.Value} "
                       + $"filter='{_cfg.ChestNameContains.Value}' "
                       + $"prefabNames={(_lookupOk ? "available" : "UNAVAILABLE")} "
                       + $"dots={(_matPropDots != null ? "yes" : "no")} "
                       + $"hybridModelSystem={(_hybridModels != null ? "yes" : "no")}");
            return true;
        }

        /// <summary>
        /// PrefabGUID is read and never written, so a layout mismatch suppresses this feature
        /// rather than disabling the plugin's writes - and rather than reading a garbage int and
        /// matching it against prefab names, which would look like working code identifying the
        /// wrong things. Fails OPEN: only a probe that definitely ran and definitely disagreed may
        /// switch the feature off.
        /// </summary>
        private bool VerifyChestLayout()
        {
            // FIRST, and independently of everything below. This is the one component the
            // feature writes, so its check is the only one guarding chunk memory. It used to sit
            // after the PrefabGUID probe, where two early returns skipped the assignment
            // entirely - leaving the static route switched off, _layoutOk still true, and NOTHING
            // in the log to say why the only route that reaches a world chest had died. A check
            // that silently disables a working feature when the CHECK is what went wrong is
            // exactly what this project has a rule against. VerifySized fails open on its own.
            _staticRouteOk = VerifySized<ProjectM.Presentation.ShaderProperty_BlinkColor>(
                "ProjectM.Presentation.ShaderProperty_BlinkColor (the chest tint is written "
                + "through this, so that route is disabled)");

            // PrefabGUID is read and never written, so a mismatch suppresses the feature rather
            // than corrupting anything - and must not be able to reach back and affect the write
            // route above, which was verified on its own terms.
            try
            {
                IntPtr klass = Il2CppClassPointerStore<PrefabGUID>.NativeClassPtr;
                if (klass == IntPtr.Zero) return true;
                uint align = 0;
                int native = (int)IL2CPP.il2cpp_class_value_size(klass, ref align);
                int managed = Marshal.SizeOf<PrefabGUID>();
                if (native == managed) return true;
                _log.LogError($"Stunlock.Core.PrefabGUID layout mismatch: il2cpp says {native} "
                            + $"bytes, Marshal.SizeOf says {managed}. Chest glow disabled - "
                            + "rebuild the plugin against the current interop assemblies.");
                return false;
            }
            catch { return true; }
        }

        /// <summary>
        /// Compare Marshal.SizeOf against il2cpp's true size for one sized component. FAILS OPEN,
        /// for the reason CLAUDE.md gives: a check that breaks the feature when the check itself
        /// is what is broken is worse than no check. Only a probe that definitely ran and
        /// definitely disagreed returns false.
        /// </summary>
        private bool VerifySized<T>(string label) where T : struct
        {
            try
            {
                IntPtr klass = Il2CppClassPointerStore<T>.NativeClassPtr;
                if (klass == IntPtr.Zero)
                {
                    _log.LogWarning($"No il2cpp class for {label}; skipping its layout check.");
                    return true;
                }
                uint align = 0;
                int native = (int)IL2CPP.il2cpp_class_value_size(klass, ref align);
                int managed = Marshal.SizeOf<T>();
                if (native <= 0)
                {
                    _log.LogWarning($"il2cpp reported a nonsense size ({native}) for {label}; "
                                  + "ignoring the check rather than trusting it.");
                    return true;
                }
                if (native == managed) return true;
                _log.LogError($"LAYOUT MISMATCH {label}: il2cpp says {native} bytes, "
                            + $"Marshal.SizeOf says {managed}. Rebuild the plugin against the "
                            + "current interop assemblies.");
                return false;
            }
            catch (Exception e)
            {
                _log.LogWarning($"Layout check unavailable for {label} ({e.Message}); "
                              + "continuing without it.");
                return true;
            }
        }

        // ---------------------------------------------------------------- helpers
        /// <summary>
        /// Read up to <paramref name="max"/> elements of a DynamicBuffer.
        ///
        /// Uses <c>GetBufferLength</c> / <c>GetBufferRawRO</c> - the NON-generic pair - for the
        /// same reason <see cref="Read{T}"/> uses GetComponentDataRawRO: the generic
        /// <c>GetBuffer&lt;T&gt;</c> goes through Il2CppInterop generic marshalling, which this
        /// project has been bitten by before.
        ///
        /// The stride is <c>Marshal.SizeOf&lt;T&gt;()</c>, so the equality the rest of the plugin
        /// depends on has to hold for the ELEMENT type - checked here rather than assumed,
        /// because reading at the wrong stride walks into the next entity's data.
        /// </summary>
        /// <param name="items">Reused when it is already the right length, so a scan over many
        /// chests does not allocate one array per chest. Callers must not read it when this
        /// returns false - it may still hold the previous entity's children.</param>
        private unsafe bool TryReadBuffer<T>(EntityManager em, Entity e, int max,
                                             ref T[] items, out string why)
            where T : struct
        {
            why = null;
            int stride;
            try { stride = Marshal.SizeOf<T>(); }
            catch (Exception ex) { why = $"cannot size {typeof(T).Name}: {ex.Message}"; return false; }

            try
            {
                IntPtr klass = Il2CppClassPointerStore<T>.NativeClassPtr;
                if (klass != IntPtr.Zero)
                {
                    uint align = 0;
                    int native = (int)IL2CPP.il2cpp_class_value_size(klass, ref align);
                    if (native > 0 && native != stride)
                    {
                        why = $"{typeof(T).Name} stride mismatch: il2cpp {native}, Marshal {stride}";
                        return false;
                    }
                }
            }
            catch { /* fail open on the probe, as every other layout check here does */ }

            try
            {
                var ct = Ct<T>.Value;
                int len = em.GetBufferLength(e, ct.TypeIndex);
                if (len <= 0) { why = "empty"; return false; }
                void* p = em.GetBufferRawRO(e, ct.TypeIndex);
                if (p == null) { why = "null buffer pointer"; return false; }

                int take = Math.Min(len, max);
                if (items == null || items.Length != take) items = new T[take];
                for (int i = 0; i < take; i++)
                    items[i] = Marshal.PtrToStructure<T>(new IntPtr((byte*)p + (long)i * stride));
                why = len > take ? $"{len} entries, first {take} read" : $"{len} entries";
                return true;
            }
            catch (Exception ex) { why = ex.Message; return false; }
        }

        /// <summary>
        /// How many of a static tile model's render children carry the shader property the tint
        /// is written through. Dump-only: <c>children=8/8</c> says the whole chest is tintable,
        /// <c>0/8</c> says the hierarchy is there but nothing on it takes a colour.
        /// </summary>
        private bool CountTintableChildren(EntityManager em, Entity e, out int total, out int tintable)
        {
            total = 0;
            tintable = 0;
            ProjectM.StaticHierarchyBuffer[] kids = null;
            string why;
            if (!TryReadBuffer(em, e, MaxTintChildren, ref kids, out why) || kids == null) return false;

            for (int i = 0; i < kids.Length; i++)
            {
                var c = kids[i].Entity;
                if (c == Entity.Null) continue;
                try { if (!em.Exists(c)) continue; } catch { continue; }
                total++;
                if (Has<ProjectM.Presentation.ShaderProperty_BlinkColor>(em, c)) tintable++;
            }
            return total > 0;
        }

        private GameObject ResolveModel(EntityManager em, Entity e)
        {
            if (_hybridModels == null) return null;
            Il2CppSystem.Collections.Generic.Dictionary<Entity, GameObject> map;
            try { map = _hybridModels.GetEntityToGameObjectMap(); } catch { return null; }
            if (map == null) return null;

            GameObject go = null;
            try { if (map.TryGetValue(e, out go) && go != null) return go; }
            catch { }

            Entity model = ModelEntityOf(em, e);
            if (model == Entity.Null) return null;
            try { if (map.TryGetValue(model, out go) && go != null) return go; }
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

        private static bool HasModel(EntityManager em, Entity e)
        {
            try
            {
                if (!Has<HybridModelUser>(em, e)) return false;
                return Read<HybridModelUser>(em, e).HybridEntity != Entity.Null;
            }
            catch { return false; }
        }

        private static bool TryGetPosition(EntityManager em, Entity e, out Vector3 p)
        {
            p = Vector3.zero;
            try
            {
                if (!Has<Translation>(em, e)) return false;
                var t = Read<Translation>(em, e).Value;
                p = new Vector3(t.x, t.y, t.z);
                return true;
            }
            catch { return false; }
        }

        private bool TryGetLocalPosition(EntityManager em, out Vector3 me)
        {
            me = Vector3.zero;
            NativeArray<Entity> locals;
            try { locals = _localQuery.ToEntityArray(Allocator.Temp); }
            catch { return false; }
            try
            {
                if (locals.Length == 0) return false;
                return TryGetPosition(em, locals[0], out me);
            }
            finally { locals.Dispose(); }
        }

        private string _propertyRaw = null;
        private SupportedDotsProperty _property = SupportedDotsProperty._BlinkColor;
        private bool _propertyIsColour = true;

        /// <summary>
        /// The configured sequencer channel, re-parsed only when the config string actually moves.
        ///
        /// Same reasoning as BloodGlow.CurrentProperty: this is consumed in a per-frame loop, and
        /// parsing it there calls Enum.GetValues - an array allocation plus boxing, several
        /// allocations per frame - to resolve a value that only changes when someone presses the
        /// reload key. Keeping the last string alongside the parsed result and comparing is what
        /// makes the reload key apply to it; caching WITHOUT that comparison would look like this
        /// and silently never update.
        /// </summary>
        private SupportedDotsProperty CurrentProperty()
        {
            string raw = _cfg.ChestGlowProperty.Value;
            if (!string.Equals(raw, _propertyRaw, StringComparison.Ordinal))
            {
                _propertyRaw = raw;
                _property = ParseProperty(raw);
                _propertyIsColour = _property == SupportedDotsProperty._BlinkColor
                                 || _property == SupportedDotsProperty._DissolveColor;
            }
            return _property;
        }

        private SupportedDotsProperty ParseProperty(string name)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                string want = name.Trim();
                var values = (SupportedDotsProperty[])Enum.GetValues(typeof(SupportedDotsProperty));
                for (int i = 0; i < values.Length; i++)
                    if (string.Equals(values[i].ToString(), want, StringComparison.OrdinalIgnoreCase))
                        return values[i];
                _log.LogWarning($"ChestGlowProperty '{name}' is not a SupportedDotsProperty - "
                              + "using _BlinkColor.");
            }
            return SupportedDotsProperty._BlinkColor;
        }

        private static float4 ParseColor(string s, float4 fallback)
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            string[] parts = s.Split(',');
            if (parts.Length < 3) return fallback;
            float r, g, b;
            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out r)
             || !float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out g)
             || !float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out b))
                return fallback;
            return new float4(r, g, b, 1f);
        }

        /// <summary>
        /// ComponentType for T, resolved once. Same reasoning as the identical caches in
        /// VisionBooster and BloodGlow: the per-call Il2CppType.Of&lt;T&gt;() lookup is the most
        /// repeated piece of work in the scan and never changes for a given T.
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

        /// <summary>
        /// Read a PrefabGUID without going through <see cref="Read{T}"/>.
        ///
        /// This is the hottest line in the plugin: the scan query matches every entity carrying
        /// PrefabGUID + Translation, which measured 6,700-7,100 in a normal world, and it runs
        /// for all of them once per ChestScanMs. Marshal.PtrToStructure is the general
        /// marshalling path and is far more expensive than this needs to be.
        ///
        /// PrefabGUID is ExplicitLayout with a single <c>int _Value</c> at offset 0, so reading
        /// four bytes there is the same value the marshaller would have produced, without the
        /// marshaller. Two honest caveats: <see cref="VerifyChestLayout"/> size-checks the type
        /// only when its probe can run - it FAILS OPEN on a null class pointer or a throw - and a
        /// size check would not establish the offset anyway. The offset-0 assumption is this
        /// shortcut's own premise, supported by PrefabGUID._Value being read directly elsewhere
        /// in this file and by PrefabGUID.CreateUnsafe(int) round-tripping it. What the shortcut
        /// does guarantee is direction: it reads strictly FEWER bytes than PtrToStructure did.
        ///
        /// Do NOT generalise this to <see cref="Read{T}"/>. The safety argument for every other
        /// component in this plugin rests on Marshal.SizeOf semantics for multi-field structs;
        /// this holds only because the type is one int.
        ///
        /// The caller must already know the entity carries PrefabGUID. Both call sites iterate
        /// _prefabQuery, which requires it. There is no Has&lt;T&gt; guard here and the null check
        /// below is not a reliable backstop - with collections checks compiled out,
        /// GetComponentDataRawRO for a missing component is not guaranteed to return null or
        /// throw.
        /// </summary>
        private static unsafe bool TryReadPrefabGuid(EntityManager em, Entity e, out int guid)
        {
            guid = 0;
            try
            {
                void* ptr = em.GetComponentDataRawRO(e, Ct<PrefabGUID>.Value.TypeIndex);
                if (ptr == null) return false;
                guid = *(int*)ptr;
                return true;
            }
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
