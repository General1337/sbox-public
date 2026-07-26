# Fork Patches — `codex/hotload-full-fallback`

This is a **narrowly-scoped fork** of `Facepunch/sbox-public`, maintained for the
[Sandbox++](https://github.com/Softsplit/sandbox-plus-plus) project's AI-agent
development loop. It adds observability seams the published engine does not expose.

- **Branch:** `codex/hotload-full-fallback`
- **Upstream base:** `20534558` (`Facepunch/sbox-public` master, synced 2026-07-25 from `91762136` — 159 commits; prior bases `0344b0ba` 2026-06-04, `7091711f`, `91762136` 2026-07-02)
- **Delta:** 22 commits ahead of `20534558`, **0 behind** — all 11 logical patches present (analyzer-loader spans 3) + 4 merges (`b63d549b`, `29ffbc5a`, `2548927c`, `335b77c9`)
- **Remotes:** `origin` = `General1337/sbox-public`, `upstream` = `Facepunch/sbox-public`
- **Status:** permanently fork-only — the project does not open Facepunch PRs (owner decision 2026-05-20).

> ### Native artifacts: commit the merge BEFORE building (2026-07-25)
> `SboxBuild build` resolves native binaries by walking `git rev-list HEAD` for a
> published artifact manifest. If you build with the merge still uncommitted, HEAD is
> your pre-merge commit, the walk finds the **old base's** manifest, reports
> `Updated 0 file(s)`, and then `build-shaders` dies with
> `igen_engine: interop hash mismatch - managed sent <a>, native was built with <b>` +
> `0xC0000005`. That is not a merge defect — the managed side was regenerated from
> upstream's new `.def` files while the natives still matched the old base. **Commit the
> merge first**, then build; the walk reaches the upstream tip and pulls the right
> `game/bin/win64/*.dll`.
>
> Related: an isolated `dotnet build engine/Sandbox.Engine/Sandbox.Engine.csproj` after a
> big sync reports a spray of CS1061/CS1501/CS1503 errors on native interop types
> (`ISceneLayer`, `IModel`, `CSfxTable`, `CDecalSceneObject`, `CSceneAnimatableObject`).
> Those are **stale generated interop**, not real errors — the changed `.def` files map
> 1:1 onto them and SboxBuild's codegen step clears them. Use `SboxBuild build`, not a
> bare csproj build, as the compile gate.

The full design rationale, the MCP-side leverage built on these patches, and the
agent-facing usage guide live in the game repo:
`sandbox-plus-plus/docs/sbox-reference/engine-fork-guide.md` and the initiative
charter `sandbox-plus-plus/docs/ai/initiatives/engine-fork-elite-leverage/charter.md`.

## Design rule for every patch

**Event patches (1.1 — 1.6 / 2.4)** are **observation-only** and follow one pattern:

```
public static event Action<…> OnXxx;          // in a NEW partial-class file
internal static void RaiseOnXxx(…) { … }       // per-subscriber try/catch
```

The raise helper is invoked from one or two sites in the existing producer. No
behaviour change to the engine — subscribers are passive observers. The consuming
side (the `arenula_mcp` MCP plugin) binds these events by **reflection**, so its
binary is byte-identical whether it runs on this fork or on stock Facepunch engine.

**Functional patches** (currently only the analyzer-support patch below) add new
behaviour that runs unconditionally inside the engine. Each must:

1. Be scoped to ONE extension point (avoid touching multiple subsystems).
2. Default to no-op when no game-side input exists (so Facepunch-stock projects
   running on this fork see zero behaviour delta).
3. Surface its diagnostics through existing channels (e.g. `CompilerOutput.Diagnostics`)
   so consumers binding the existing event patches automatically observe them.

## The patches

| SHA | Phase | Engine file(s) | Public symbol added | Purpose |
|-----|-------|----------------|---------------------|---------|
| `78adb75b` | pre-1.x | `Sandbox.Hotload/ILHotload/ILHotload.cs`, `Sandbox.Engine/Services/Packages/PackageManager/PackageLoader.cs` | `ILHotload.HasConsoleMetadataChanges()` + fast-hotload veto in `TryFastHotload` | Veto fast hotload when `[ConCmd]`/`[ConVar]` metadata changed, so stale console metadata cannot survive a fast hotload. Adds `Sandbox.Compiling.Test` fixtures. |
| `27e634ae` | 1.1 | `Sandbox.Compiling/CompileGroup.cs` | `CompileGroup.OnBuildCompleted` (`Action<CompilerOutput[]>`) | Global compile-completion observability. |
| `e4165169` | 1.2 | `Sandbox.Compiling/CompileGroup.cs` | `CompileGroup.OnCompileFailed` (`Action<CompilerOutput[]>`) | Companion to 1.1 — fires with the failed-output diagnostics 1.1 excludes. |
| `ed7b424d` | 1.3 | `Sandbox.Hotload/Hotload.OnComplete.cs` (+ `UpdateReferences.cs`) | `Hotload.OnComplete` (`Action<HotloadResult>`) | Global hotload-completion observability, including the `NoAction` early-out signal. |
| `fe31467b` | 1.4 | `Sandbox.Engine/Systems/Console/ConVarSystem.OnRegistered.cs`, `Sandbox.Reflection/TypeLibrary/TypeLibrary.OnRegistered.cs` | `TypeLibrary.OnAssemblyRegistered` + `ConVarSystem.OnAssemblyRegistered` | Replaces polling for assembly/type/command registration. |
| `a93e7971` | 1.5 | `Sandbox.Engine/Scene/Scene/Scene.OnMutation.cs` (+ `GameObjectDirectory.cs`) | `Scene.OnGameObjectAdded` / `OnGameObjectRemoved` / `OnComponentRegistered` / `OnComponentUnregistered` | Push scene-mutation stream; replaces snapshot-diff polling. |
| `540c5b8d` | 1.5a | `Sandbox.Engine/Scene/Components/Component.OnPropertyChanged.cs` (+ `Component.cs`, `Component.Dirty.cs`) | `Component.OnPropertyChanged` (`Action<Component>`) | High-frequency property-mutation observability; rides the existing `CallbackBatch`. |
| `51af0407` | 1.6 / 2.4 | `Sandbox.Tools/Assets/AssetSystem.OnError.cs` (+ `NativeAsset.cs`, `AssetThumbnail.cs`) | `AssetSystem.OnAssetCompileFailed` (`Action<Asset, string, Exception>`) | Push asset-compile-failure stream; replaces asset-error log-scraping. |
| _(TBD)_ | 3.1 | `Sandbox.Compiling/Compiler/Compiler.Analyzers.cs` (NEW) + `Compiler.Build.cs` invocation site | **Functional** (not event): `RunProjectAnalyzers(...)` discovers loaded `package.*.editor` assemblies, instantiates every `[DiagnosticAnalyzer]`-attributed type, runs them against the in-engine compilation, appends their diagnostics to `CompilerOutput.Diagnostics`. Runs after generators, before whitelist walker — analyzers cannot smuggle blacklisted API usage. No-op when no game-side analyzers exist. Anchor case: makes Sandbox++ `FrameBasisAnalyzer` (FBA001/FBA002 gravity-hull frame-type enforcement) fire on every in-engine compile, closing the IDE-only enforcement gap. |
| _(2026-07-10)_ | 3.2 | `Sandbox.Engine/Scene/Components/Clutter/ClutterLayer.cs`, `Scatterer.cs`, `TerrainScatterer.cs`, `ClutterGenerationJob.cs`, `Sandbox.Engine/Scene/GameObjectSystems/ClutterGridSystem.cs` | **Functional** + `clutter_stats` ConCmd | REVISES the 8073facb clutter patch: re-converges batch keying to stock per-Model (the per-(tile,model) re-key caused up to 3468 batch scene objects at TileRadius 8 → ~470ms GPU frames + editor crash on Eden arrival); original tile-upload frame spike now solved via dirty-model coalescing (≤2 merged-batch re-uploads/frame under the streaming deadline). Also fixes two STOCK defects kept as isolated upstream-PR-candidate diffs: per-point `scene.GetBounds()` in `TraceGround` hoisted to per-job with tile-height Z clamp; pending-job re-sort on camera tile move + nearest-first trim (was frozen sort + arbitrary first-100). `clutter_stats` logs batches/pending/populated + cumulative traced/completed/trimmed/rebuild counters. Acceptance gate: game-repo `sbep.test_bug_grass_arrival_streaming_fix` (PASS 5/5 @ r4, 2026-07-10). **RECONCILED with upstream 2026-07-25 — see the note below; the patch SHRANK.** |
| `48d42162` | 3.3 | `Sandbox.Engine/Resources/ResourceLibrary.cs` | **Functional** (not event): `ResourceSystem.OnHotload()` now also rehydrates stale `GameResource` instances | Project-defined `GameResource` types get a new CLR identity after a full hotload; strongly-cached instances from the outgoing assembly still occupy their path, so typed lookups can no longer cast them to the incoming type (observed as `InvalidCastException` / "missing" resources on paths that are demonstrably registered). Scans `ResourceIndex` for instances whose runtime type is no longer assignable to the type currently registered for their extension and re-loads only those via `LoadRawGameResource`, preserving `Package`. Deliberately scoped — clearing the whole resource system would needlessly invalidate models, materials, textures and unrelated managed resources. **Committed 2026-07-25, built into `Sandbox.Engine.dll` (symbol `IsStaleGameResourceType` verified present), but NOT yet runtime-certified** — see Verification below. |
| _(uncommitted, 2026-07-26)_ | 11 / fleet-autonomy Phase 6 | `Sandbox.Engine/Services/Packages/PackageManager/PackageLoader.Batching.cs` (NEW) + `PackageLoader.cs`, `Sandbox.Engine/Core/Context/IGameInstanceDll.cs`, `Sandbox.GameInstance/GameInstanceDll.cs`, `GameInstanceDll.Network.cs`, `Sandbox.Tools/Utility/Utility.Projects.cs` | **Functional** (not event): ConVars `hotload_batch_ms`, `hotload_batch_max_ms`, `hotload_hold`, `hotload_hold_max_s`; `PackageLoader.Tick( bool force )` and `FinishLoadingAssemblies( bool force )` | Editor-only hotload batching for the N-agent fleet. Each agent's save rewrites the SAME package DLL, so N saves cost N full `DoSwap`s (20-32s of blocked frames + one hotload generation each). `changedPackageDlls` is already a dedup'd HashSet, so the whole patch is *when* to drain it: `hotload_batch_ms` waits for DLL quiet (passive, no coordination), `hotload_hold` lets one agent hold across a multi-file burst (active). **Defaults are inert** (`hotload_batch_ms=0`, `hotload_hold=false`) — stock behaviour until opted in. **Every deferral is bounded**: `hotload_hold_max_s` force-releases a hold left set by a crashed agent, `hotload_batch_max_ms` stops continuous saves starving the drain. **Safety:** the multiplayer join path must NEVER batch — a client loads streamed assemblies synchronously before reading further network messages, so `FinishLoadingCodeArchives` passes `force: true`, stream-sourced entries (`ap is null`) and non-empty `IncomingThisHotload` are never deferred, and the whole thing is hard-gated to `Application.IsEditor`. Control surface is ConVar *names* over the console, so nothing in the shipped game references it and stock engine simply lacks the commands. **RUNTIME-CERTIFIED 2026-07-26** (4 gates, one clean boot each): (A) at defaults, 1 save → 1 generation, i.e. stock behaviour unchanged; (B) `hotload_hold 1`, 3 saves 45 s apart → **0 generations** (the same row costs 3 unbatched); (C) release → **exactly 1 generation**, 3 hotloads collapsed into 1; (D) hold left set with a 20 s cap → force-released at 20001 ms and the hotload ran (the anti-wedge guarantee). **A trap worth knowing:** the first draft also set `force: true` at `EditorUtility.Projects.WaitForCompiles`, reasoning it was "an explicitly requested load". That is the editor's own compile-then-load path for locally edited game code — forcing it made `hotload_hold` a silent no-op for game code while still batching the menu package, so the hold *looked* like it worked and delivered nothing. Found by an instrumented boot, not by inspection. Fleet driver: `sandbox-plus-plus/Tools/hotload-hold.py` (refcounted, so one agent's release cannot drop another's hold). Evidence: `docs/ai/sessions/2026-07-26-hotload-batch-path-discriminator/`. |
| `b63d549b` | — | (merge) | — | Merge of `origin/master` into the dev branch. |

## Patch 3.2 (clutter) — reconciled with upstream, 2026-07-25

The 2026-07-25 sync put **all 8 of its conflict hunks in this one patch**, because upstream
had independently rewritten the same code. Every other patch auto-merged with zero
conflicts. What changed:

- **One of our two stock-defect fixes was upstreamed by convergence and DELETED here.**
  Upstream `d6a63a1b` ("Optimize scatterers") independently hoisted `scene.GetBounds()`
  out of the per-point `TraceGround` — threading `BBox sceneBounds` where we threaded
  `(float zMin, float zMax)` — and added a `Parallel.For` `BatchTraceGround` on top.
  Upstream's is strictly better, so **ours was removed** (`ResolveTraceZRange` and the
  `(zMin, zMax)` overload are gone, ~60 lines). Two things were deliberately carried
  across rather than lost:
  - the `s_pointsTraced` counter `clutter_stats` reports — incremented **once outside**
    the `Parallel.For`, since a shared static `++` inside it would be a data race;
  - the degenerate-Z fallback, now `Scatterer.ResolveTraceBounds( scene, bounds )`. This
    is load-bearing and upstream has no equivalent: a tile whose bounds are flat in Z
    otherwise traces a zero-length ray, hits nothing, and **silently places no clutter**.
    Upstream never needed it because its own callers pass whole-scene bounds.
- **Batch keying widened, coalescing kept.** Upstream `3c3435fd` changed the key from
  `Model` to `record struct ClutterBatchKey( Model, bool CastShadows )`. Our dirty-batch
  coalescing + `MaxDirtyModelsPerRebuild` budget is **kept and re-keyed** onto it. Still
  bounded by model count, so it does **not** reintroduce the per-`(tile, model)` blow-up
  the `ClutterLayer` header describes (~12–24 batches at Eden radius 8, not ~3.4k).
  Upstream's `_instancesByModel` / `_activeModels` / `_staleModels` were **not** kept —
  our `RebuildModelBatch` rescans `ModelInstancesByTile` and drops empty batches inline,
  so they would have been dead fields.
- **`TerrainScatterer.Generate` taken wholesale from upstream** (`JitteredGridPoints` +
  `BatchTraceGround`; neither existed at base). For `TerrainMaterialScatterer` the
  per-hit body was factored into a new shared `TryCreateInstanceFromTrace`, called from
  **both** upstream's batch `foreach` and our `TryCreateInstance` — the streaming work
  item places points one at a time under a frame deadline and so cannot batch, and
  duplicating the terrain/material/entry logic across both paths would rot.
- **One near-miss worth recording.** In `ClutterGenerationJob` git mis-aligned our
  deadline-chunking hunk against an unrelated upstream hunk. Upstream's volume-bounds
  filter was already correctly present in our refactored `FinishGeneration` (so its copy
  in the conflict was a duplicate), but **`ApplyEntryLocalScale` was new from upstream and
  our refactor never called it** — taking "our side" naively would have left the method
  defined-but-unused and silently dropped per-entry `LocalScale`. The call is re-seated in
  `FinishGeneration`. Also honoured upstream's headless batch skip (`c7b89fee`) in our
  `RebuildAllDirtyModels` flush path, which would otherwise keep building batch scene
  objects on a headless server.

**Still open:** upstream's `d6a63a1b` + `d15dd209` attack the same cost centres this patch
exists for, so it is worth testing whether stock-upstream alone now passes
`sbep.test_bug_grass_arrival_streaming_fix`. If it does, patch 3.2 can be dropped
entirely (~711 insertions across 5 files). Not yet measured — needs the editor.

## Verification

The full hotload test surface (`Sandbox.Hotload.Test` + `Sandbox.Compiling.Test`)
requires Facepunch-private native bindings to build and cannot run in a public
checkout. The patches are verified instead via the game-side Tier-3 regression
ConCmds (`sbep.test_compile_on_build_completed`, `sbep.test_hotload_on_complete`,
`sbep.test_scene_mutation_events`, etc.) run on the Bootstrapped fork editor, and via
the engine-fork CI (`sandbox-plus-plus/.github/workflows/engine-fork-ci.yml`).

**2026-07-25 sync (merge `335b77c9`, base `20534558`) — RUNTIME-CERTIFIED for 10 of 11 patches.**

Build: `SboxBuild build` / `build-shaders` / `build-content` all exit 0 with 0 errors; all patch
symbols present in the freshly built binaries; game project compiles against the merged engine with
**0 errors**; in-engine compile **0 errors**.

Runtime (fresh cold boot, gen 0):
- **`feed action=channels`** → `compile` / `scene` / `assets` all `available: true`.
- **Patch 1.1 `OnBuildCompleted`** — `feed.compile` emitted a real `build_completed`
  (`output_count=1`, `error_count=0`, `warning_count=397`, `diagnostic_count=7575`).
- **Patch 1.2 `OnCompileFailed`** — emitted `compile_failed` (`total_error_count=1`,
  `successful=false`) on an induced CS0103, reverted immediately.
- **Patches 1.3 / 1.4 / 1.5 / 1.5a** — `sbep.test_{hotload_on_complete,typelibrary_on_registered,
  scene_mutation_events,component_property_changed}` all **4/4 gates PASS**.
- **Patches 3.1 / 3.1.1 (the anchor)** — a deliberate `WorldVec3 + ShipLocalVec3` probe fired
  **`FBA002` at severity `error`** on an in-engine compile (with `.analyzers-on` + cold boot);
  FBA001 also fired across real scanned files. The analyzer survived upstream's renewed
  `PackageLoader` assembly-loading rework — this was the highest semantic risk of the sync.
- **Patch 3.2 (clutter)** — functionally certified: `clutter_stats` reports `batches=10` at Eden
  radius 8 (**not** thousands — the per-`(tile, model)` blow-up did not return),
  `EdenGrassClutter[225/225]` tiles populated (proves the `ResolveTraceBounds` degenerate-Z guard),
  plus live `points_traced` / `jobs_trimmed` counters.

**NOT certified — do not claim these:**
- **Patch 3.3** — built and symbol-present, but its cure path was never induced. **Unproven.**
  Kept deliberately (2026-07-25 user decision to ship the sync); certifying it needs a real stale-
  `GameResource` / clone-rot scenario.
- **Patch 1.6/2.4** — **bind-proven with one FAILED emit (downgraded 2026-07-25).** A malformed
  `.vmat` produced a real asset-compile failure (captured by `asset_query get_compile_errors` at
  `17:11:58`) and `feed.assets` emitted **nothing** (`buffer_size: 0`). A second attempt with an
  un-compilable `.shader` was inconclusive — an unreferenced shader is not compiled on
  `asset_manage reload`, so no failure was induced at all. This does not prove the patch is broken
  (the `.vmat` parse path may never route through `OnAssetCompileFailed`), but **do not treat
  `feed.assets` as a working failure alarm.** Next probe must break a shader the loaded scene
  actually references, so the compile is forced.
- **Fast-hotload veto** — symbol present, not exercised. Circumstantial only: the 2026-07-25
  phase-3b compile added a new `[ConCmd]` **and** a new `Component` type, and both were live at
  gen 3 with no editor restart — the outcome the veto exists to guarantee — but `feed.hotload` was
  subscribed after the file-watcher build, so the event itself was not captured.
- **`sbep.test_bug_grass_arrival_streaming_fix` — overall VERDICT: FAIL (4 of 5 gates), and it
  stays FAIL. Attribution resolved 2026-07-25; shipping anyway by user decision.** The streaming
  fix itself passes. The failing sub-metric is post-fill steady state against a 25ms budget. A
  same-position A/B (`sbep.eden_grass_ab`, control drift 0.92ms) splits it: **scene floor 17.0ms
  p95 + grass 14.3ms p95 = 31–33ms.** Neither half alone breaks the budget; the sum does, and grass
  is the larger contributor to the overage. The 2026-07-10 baseline that passed was **at the same
  r8/d4.5 config** with postP95 22.4/21.7ms (commit `f9b75f1d5` — the earlier "@ r4" note was
  wrong), so a real ~10ms regression exists at an unchanged config. **It is NOT attributed to this
  merge** — splitting it from two weeks of scene/content change would need a `presync-2026-07-25`
  engine rebuild, which was declined. Treat the merge as neither cleared nor charged here.

Full detail, method notes, and the remaining checklist:
`sandbox-plus-plus/docs/ai/sessions/2026-07-25-engine-fork-upstream-sync/phase-03.md`.

> Method note: `strings` is not installed in this environment and returns empty output
> **silently**, so a symbol check built on it reads as "MISSING" for everything, including
> symbols that certainly exist. Use `grep -acF "<symbol>" <dll>` and always include a
> control symbol that cannot be absent (e.g. `GameObject`) to prove the instrument works.

**Previously re-certified 2026-07-02** (merge `2548927c`, base `91762136`): Bootstrap compile-clean;
all 6 fork-event regression ConCmds PASS (`compile_on_build_completed`, `compile_error_severity`,
`hotload_on_complete`, `typelibrary_on_registered`, `scene_mutation_events`,
`component_property_changed`); `feed channels` shows compile/scene/assets `available:true`; and the
`FrameBasisAnalyzer` positively re-verified live (a deliberate `WorldVec3 + ShipLocalVec3` probe
fired `FBA002` on an in-engine compile).

## Adding a patch

Land it on this branch. Use the `public static event` + `internal RaiseXxx` pattern
in a new partial-class file. Then update: this file, the game repo's
`docs/sbox-reference/engine-fork-guide.md` §3, the `feed.*` table in the game repo's
`CLAUDE.md`, and `.claude/hooks/banned-fork-symbols.txt`.
