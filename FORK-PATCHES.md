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

**2026-07-25 sync (merge `335b77c9`, base `20534558`) — BUILD-VERIFIED, RUNTIME-UNCERTIFIED.**
What is proven: `SboxBuild build` / `build-shaders` / `build-content` all exit 0 with 0 errors;
all patch symbols confirmed present in the freshly built binaries (`IsStaleGameResourceType`,
`ResolveTraceBounds`, `TryCreateInstanceFromTrace`, `clutter_stats` in `Sandbox.Engine.dll`;
`HasConsoleMetadataChanges` in `Sandbox.Hotload.dll`; `OnBuildCompleted`, `RunProjectAnalyzers`
in `Sandbox.Compiling.dll`); the game project compiles against the merged engine with **0
errors**; and the editor launches and reaches `editor-health.py` = `up` on the new build.
What is NOT yet proven: **nothing runtime**. The 6 fork-event regression ConCmds, the
`FBA002` analyzer anchor, patch 3.3's rehydration, and the clutter gates have all NOT been
run against this merge. Do not treat this sync as certified until they are — the battery is
listed in the game repo at
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
