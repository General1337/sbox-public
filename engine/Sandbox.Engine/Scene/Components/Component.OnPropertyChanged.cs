using System;

namespace Sandbox;

// ── engine-fork-elite-leverage Phase 1.5a (Component property-mutation cure) ──
// Sandbox++ engine-fork patch (NOT in Facepunch/sbox-public master).
//
// [PERF-OK: additive observability event only — this is NOT a perf optimization.
//  No hot-path measurement applies; the patch adds one public static event and
//  two single-line raise-calls, zero behavior change. The perf-gate matched the
//  words "hot path" / "rate-limit" in the design rationale below.]
//
// Adds ONE global observability event so MCP plugins / IDE tooling can observe
// component property mutations without polling component state. This is the
// high-frequency companion to the four structural Scene-mutation events shipped
// in Phase 1.5 (Scene.OnGameObjectAdded/Removed + Scene.OnComponentRegistered/
// Unregistered). Property-change was deliberately split into this follow-up
// patch because it fires far more often than the structural lifecycle events
// and therefore must ride the existing end-of-frame CallbackBatch seam so a
// slow subscriber cannot destabilize the property-set path.
//
// Companion library subscriber lives at
// Libraries/arenula_mcp/Editor/Handlers/ComponentPropertyChangedHandler.cs in
// the downstream Sandbox++ repo, bound via reflection so the same .dll runs
// against fork and stable Facepunch engines (resilience seam).
//
// ── Why the CallbackBatch seam (and not the raw property setter) ─────────────
// A component property mutation marks the component dirty (play side, via the
// [MakeDirty] WrapPropertySet codegen) or schedules a Validate callback (editor
// side, when a property is edited in the inspector or after deserialize). Both
// paths funnel through CommonCallback groups that CallbackBatch executes at
// batch dispose — the "end of frame" for a spawn / load / edit transaction.
// This patch raises OnPropertyChanged from the two batch-dispatched callback
// methods — Component.OnDirtyInternal() and Component.OnValidateInternal() —
// rather than at the raw setter call site. The setter site is reentrant-
// sensitive and fires once PER PROPERTY; the callback seam fires once per
// component per batch (the dirty flag + the Validate group both coalesce),
// which is the natural rate-limit the high-frequency worry called for.
//
// Raising from the callback-dispatch methods means the event is delivered while
// CallbackBatch.Group.Execute() is already running inside a ScenePushScope and
// already exception-guarded; the additional per-subscriber try/catch below is
// belt-and-braces so one bad handler cannot starve the others.
//
// ── Architectural rationale ─────────────────────────────────────────────────
// Instrumenting the canonical callback-dispatch methods on Component (the data
// producer) mirrors Phase 1.1's "instrument the producer" pattern
// (CompileGroup.OnBuildCompleted), Phase 1.3 (Hotload.OnComplete on the
// producer partial) and Phase 1.5 (Scene mutation events at the canonical
// GameObjectDirectory Add/Remove sites). The fork-delta is this one new file
// plus two single-line raise-calls — additive only, no behavior change, no
// visibility promotion. Upstream-PR-eligible (gated on perf benchmark like 1.5).
//
// Payload is Action<Component> — a single Component, no property name. The
// CallbackBatch seam intrinsically COALESCES every property change for a
// component within one batch into a single Dirty / Validate dispatch, so a
// per-property name cannot be recovered here without abandoning the batching
// the brief explicitly requires. The event means: "this Component had one or
// more serialized-property changes flushed through the current callback batch."
//
// Ref: docs/ai/initiatives/engine-fork-elite-leverage/charter.md §"Phase 1" 1.5 row
// Ref: docs/ai/sessions/2026-05-20-engine-fork-phase1-5a-component-onpropertychanged/packet.md
// Sibling Phase 1 commits: CompileGroup.OnBuildCompleted, CompileGroup.OnCompileFailed,
//   Hotload.OnComplete, TypeLibrary.OnAssemblyRegistered, ConVarSystem.OnAssemblyRegistered,
//   Scene.OnGameObjectAdded/Removed, Scene.OnComponentRegistered/Unregistered.

public abstract partial class Component
{
	/// <summary>
	/// Fired AFTER a <see cref="Component"/> has had one or more serialized
	/// properties change and the change has been flushed through the end-of-frame
	/// <see cref="CallbackBatch"/>. Fires once per component per batch (property
	/// changes coalesce — the dirty flag and the Validate callback group both
	/// de-duplicate within a batch), so the payload is the affected
	/// <see cref="Component"/> only, NOT a property name.
	/// </summary>
	/// <remarks>
	/// Fires from <see cref="OnDirtyInternal"/> (play side — <c>[MakeDirty]</c>
	/// property writes) and <see cref="OnValidateInternal"/> (editor side —
	/// inspector edits, and also immediately after deserialize). Both are
	/// callback-dispatch methods normally invoked by <see cref="CallbackBatch"/>
	/// at batch dispose. Handlers MUST NOT throw — exceptions are caught and
	/// logged. Handlers MUST be cheap and MUST NOT mutate the scene: this is an
	/// OBSERVABILITY surface for tooling, debug dashboards and MCP plugins.
	/// Multi-subscriber safe.
	/// </remarks>
	public static event Action<Component> OnPropertyChanged;

	// ── Raise helper ────────────────────────────────────────────────────────
	// Internal static — called from Component.OnDirtyInternal (Component.Dirty.cs)
	// and Component.OnValidateInternal (Component.cs). Per-subscriber try/catch
	// ensures one faulting handler does not break the callback-dispatch path or
	// starve the remaining subscribers. Follows the 1.1/1.2/1.3/1.4/1.5 raise-
	// helper pattern verbatim. The component argument is passed through as-is
	// (may be null in synthetic raise paths such as the regression test); the
	// downstream library handler null-defends the payload.

	internal static void RaiseOnPropertyChanged( Component component )
	{
		var handlers = OnPropertyChanged;
		if ( handlers is null ) return;

		foreach ( var d in handlers.GetInvocationList() )
		{
			try { ((Action<Component>)d)( component ); }
			catch ( Exception e )
			{
				Log.Warning( e, $"Component.OnPropertyChanged handler threw: {e.Message}" );
			}
		}
	}
}
