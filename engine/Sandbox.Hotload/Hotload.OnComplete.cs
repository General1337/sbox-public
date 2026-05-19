using System;

namespace Sandbox
{
	// ── engine-fork-elite-leverage Phase 1.3 (AP-069 cure) ────────────────
	// Sandbox++ engine-fork patch (NOT in Facepunch/sbox-public master).
	// Adds a global observability event so MCP plugins / IDE tooling can
	// subscribe to every Hotload.UpdateReferences() completion without owning
	// a per-instance Hotload reference. Companion library subscriber lives at
	// Libraries/arenula_mcp/Editor/Handlers/FeedHandler.cs in the downstream
	// Sandbox++ repo, bound via reflection so the same .dll runs against fork
	// and stable Facepunch engines.
	//
	// Architectural rationale: instrumenting Hotload (the data producer) rather
	// than HotloadManager (the editor-side wrapper) mirrors Phase 1.1's pattern
	// (CompileGroup.OnBuildCompleted at the data producer) and avoids promoting
	// HotloadManager's `internal` visibility. The fork-delta is one new file
	// plus two single-line raise-calls in UpdateReferences.cs — additive only,
	// no behavior change, no visibility promotion. Upstream-PR-eligible.
	//
	// Cures AP-069 by replacing the MCP plugin's polling-based hotload
	// state classifier (FeedHandler [EditorEvent.Hotload] handler +
	// CompileHandler.DescribeHotloadState heuristic) with a direct engine
	// signal carrying the full HotloadResult (Success, HasErrors, HasWarnings,
	// Entries, TypeTimings, NoAction, etc.). The polling path is kept as a
	// fallback for stable-engine consumers; under fork both fire.
	//
	// Ref: docs/ai/initiatives/engine-fork-elite-leverage/charter.md §"Phase 1 patch table" 1.3
	// Ref: docs/ai/sessions/2026-05-18-engine-fork-phase1-3-hotload-on-complete/plan.md
	// Sibling commit (1.1): engine/Sandbox.Compiling/CompileGroup.cs RaiseOnBuildCompleted

	public partial class Hotload
	{
		/// <summary>
		/// Fired once per <see cref="UpdateReferences"/> completion. Payload is the
		/// produced <see cref="HotloadResult"/> — same data the return value carries,
		/// delivered as a static event so observability tooling (MCP plugins, IDE
		/// extensions) can subscribe globally without owning a Hotload reference.
		///
		/// Fires AFTER <see cref="HotloadResult.ProcessingTime"/> is populated (or
		/// immediately for the NoAction early-out path) and BEFORE
		/// <see cref="UpdateReferences"/> returns to the caller. Handlers MUST NOT
		/// throw — exceptions are caught and logged. Handlers run synchronously on
		/// the hotload thread; marshal heavy work to your own thread.
		///
		/// Multi-subscriber safe (standard C# event semantics). The Sandbox.Hotload
		/// assembly is added to its own IgnoredAssemblies set at construction (see
		/// <see cref="Hotload(bool, Sandbox.Diagnostics.Logger)"/>) — meaning this
		/// static event field persists in-place across the engine's own hotload of
		/// downstream library assemblies. Library subscribers using a -+= re-bind
		/// pattern (via <c>[EditorEvent.Hotload]</c>) keep their subscription fresh
		/// across their own library reloads.
		///
		/// NoAction hotloads ALSO fire this event (the caller can filter via
		/// <see cref="HotloadResult.NoAction"/>). This is intentional: the
		/// "engine considered a hotload, decided nothing to do" signal is
		/// diagnostically relevant — it cures the AP-069 stalled_event_missing
		/// false-positive class where the agent cannot distinguish "no hotload
		/// happened" from "hotload happened with no work to do."
		/// </summary>
		public static event Action<HotloadResult> OnComplete;

		internal static void RaiseOnComplete( HotloadResult result )
		{
			var handler = OnComplete;
			if ( handler == null ) return;

			// Per-subscriber try/catch so one bad handler can't break the
			// invocation chain. Logging falls back to System.Console.WriteLine
			// because (1) Sandbox.Hotload only references Sandbox.System, NOT
			// Sandbox.Engine where the Sandbox.Diagnostics.Log static class
			// lives, and (2) the Hotload class itself has an instance method
			// Log(HotloadEntryType, ...) at UpdateReferences.cs:932 that would
			// shadow any imported Log static class inside class scope. The
			// engine wires Console.Out into its editor log stream, so this
			// surfaces in the same place Log.Warning would.
			foreach ( var subscriber in handler.GetInvocationList() )
			{
				try { ((Action<HotloadResult>)subscriber).Invoke( result ); }
				catch ( Exception e )
				{
					System.Console.WriteLine( $"[Sandbox.Hotload.OnComplete] subscriber threw: {e.Message}" );
				}
			}
		}
	}
}
