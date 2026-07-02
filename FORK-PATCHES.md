# Fork Patches — `codex/hotload-full-fallback`

This is a **narrowly-scoped fork** of `Facepunch/sbox-public`, maintained for the
[Sandbox++](https://github.com/Softsplit/sandbox-plus-plus) project's AI-agent
development loop. It adds observability seams the published engine does not expose.

- **Branch:** `codex/hotload-full-fallback`
- **Upstream base:** `91762136` (`Facepunch/sbox-public` master, synced 2026-07-02 from `7091711f`; prior bases `0344b0ba` 2026-06-04, then `7091711f`)
- **Delta:** 15 commits ahead of `91762136` — all 9 logical patches (analyzer-loader spans 3) present + 3 merges (`b63d549b`, `29ffbc5a`, `2548927c`)
- **Remotes:** `origin` = `General1337/sbox-public`, `upstream` = `Facepunch/sbox-public`
- **Status:** permanently fork-only — the project does not open Facepunch PRs (owner decision 2026-05-20).

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
| `b63d549b` | — | (merge) | — | Merge of `origin/master` into the dev branch. |

## Verification

The full hotload test surface (`Sandbox.Hotload.Test` + `Sandbox.Compiling.Test`)
requires Facepunch-private native bindings to build and cannot run in a public
checkout. The patches are verified instead via the game-side Tier-3 regression
ConCmds (`sbep.test_compile_on_build_completed`, `sbep.test_hotload_on_complete`,
`sbep.test_scene_mutation_events`, etc.) run on the Bootstrapped fork editor, and via
the engine-fork CI (`sandbox-plus-plus/.github/workflows/engine-fork-ci.yml`).

**Last re-certified 2026-07-02** (merge `2548927c`, base `91762136`): Bootstrap compile-clean;
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
