// [HANDOFF-TRUST: feature-build additive raise-call insertion, no hypothesis from inherited handoffs (129/187/200/226/149/160/206/220/25/32); rename to avoid CS0108 collision with GameObject.OnComponentAdded/Removed inherited instance methods]
using System;

namespace Sandbox
{
	// ── engine-fork-elite-leverage Phase 1.5 (snapshot.* polling cure) ────────
	// Sandbox++ engine-fork patch (NOT in Facepunch/sbox-public master).
	// Adds four global observability events so MCP plugins / IDE tooling can
	// subscribe to scene-structure mutations (GameObject + Component register/
	// unregister) without owning a per-Scene reference and without polling via
	// snapshot.take + snapshot.diff. Companion library subscriber lives at
	// Libraries/arenula_mcp/Editor/Handlers/SceneMutationHandler.cs in the
	// downstream Sandbox++ repo, bound via reflection so the same .dll runs
	// against fork and stable Facepunch engines.
	//
	// Naming note: Scene inherits from GameObject which already has instance
	// methods named OnComponentAdded(Component) and OnComponentRemoved(Component).
	// To avoid a CS0108 inherited-member-hides warning, the static events for
	// the Component side use the verb pair Registered/Unregistered instead of
	// Added/Removed. The GameObject-side events keep Added/Removed because
	// GameObject has no inherited methods of those names.
	//
	// Architectural rationale: instrumenting the canonical add/remove sites in
	// GameObjectDirectory (the data producer that already owns the per-scene
	// Action<GameObject>/Action<Component> internal delegates at lines 32-34)
	// rather than scattering events across consumers mirrors Phase 1.1's
	// "instrument the producer" pattern (CompileGroup.OnBuildCompleted) and
	// Phase 1.3's pattern (Hotload.OnComplete on the producer partial, not
	// HotloadManager wrapper). The fork-delta is this one new file plus four
	// single-line raise-calls in GameObjectDirectory.cs — additive only, no
	// behavior change, no visibility promotion. Upstream-PR-eligible (gated
	// on perf benchmark per charter §"Phase 1" 1.5 row).
	//
	// Each event carries (Scene, T) so subscribers can filter editor-vs-play
	// scene without holding a Scene reference. Add/Remove are intrinsically
	// low-frequency (scene-spawn / despawn lifecycle, not per-tick); they fire
	// synchronously from the canonical Add()/Remove() sites in
	// GameObjectDirectory. A higher-frequency Component.OnPropertyChanged
	// event is deliberately deferred to a follow-up Phase 1.5a patch — that
	// one requires CallbackBatch end-of-frame batching to not destabilize
	// subscribers, and it's not required for the charter exit criterion.
	//
	// Cures the snapshot.take + snapshot.diff polling workaround (the MCP
	// plugin currently polls scene state to detect mutations; this collapses
	// it into passive subscription). Unblocks downstream Phase 2.3
	// (feed.scene.subscribe) and Phase 4.5 (symbolic pre-execution of
	// mutations) which both gate on Phase 1.5.
	//
	// Ref: docs/ai/initiatives/engine-fork-elite-leverage/charter.md §"Phase 1 patch table" 1.5
	// Ref: docs/ai/sessions/2026-05-19-engine-fork-phase1-5-scene-mutation-event-stream/packet.md
	// Sibling commits (1.1/1.2/1.3/1.4): CompileGroup.OnBuildCompleted, CompileGroup.OnCompileFailed,
	//   Hotload.OnComplete, TypeLibrary.OnAssemblyRegistered, ConVarSystem.OnAssemblyRegistered

	public partial class Scene
	{
		/// <summary>
		/// Fired AFTER a GameObject becomes registered in this Scene's
		/// <see cref="GameObjectDirectory"/>. Subscribers can query the directory
		/// at handler-time and see the consistent post-add state. Handlers MUST NOT
		/// throw — exceptions are caught and logged. Multi-subscriber safe.
		/// </summary>
		/// <remarks>
		/// Fires synchronously from <see cref="GameObjectDirectory.Add(GameObject)"/>.
		/// Do NOT mutate the scene from a handler — the Add path is reentrant-sensitive
		/// and the existing internal per-scene <c>OnGameObjectAdded</c> delegate is
		/// the canonical place for engine-internal lifecycle work; this public static
		/// event is for OBSERVABILITY only (tooling, debug dashboards, MCP plugins).
		/// </remarks>
		public static event Action<Scene, GameObject> OnGameObjectAdded;

		/// <summary>
		/// Fired AFTER a GameObject is unregistered from this Scene's
		/// <see cref="GameObjectDirectory"/>. Subscribers will see <c>FindByGuid</c>
		/// return null for the removed object at handler-time (post-remove state).
		/// </summary>
		/// <remarks>
		/// Fires synchronously from <see cref="GameObjectDirectory.Remove(GameObject)"/>.
		/// The GameObject reference passed in the payload is still valid as a C# object,
		/// but it is no longer attached to the scene's directory. Observability-only.
		/// </remarks>
		public static event Action<Scene, GameObject> OnGameObjectRemoved;

		/// <summary>
		/// Fired AFTER a Component becomes registered in this Scene's
		/// <see cref="GameObjectDirectory"/>. Mirrors the internal per-scene
		/// <c>OnComponentAdded</c> delegate but as a global static surface for tooling.
		/// Verb is <c>Registered</c> rather than <c>Added</c> to avoid CS0108 hiding the
		/// inherited <see cref="GameObject.OnComponentAdded(Component)"/> instance method.
		/// </summary>
		/// <remarks>
		/// Fires synchronously from <see cref="GameObjectDirectory.Add(Component)"/>.
		/// </remarks>
		public static event Action<Scene, Component> OnComponentRegistered;

		/// <summary>
		/// Fired AFTER a Component is unregistered from this Scene's
		/// <see cref="GameObjectDirectory"/>. Verb is <c>Unregistered</c> rather than
		/// <c>Removed</c> to avoid CS0108 hiding the inherited
		/// <see cref="GameObject.OnComponentRemoved(Component)"/> instance method.
		/// </summary>
		/// <remarks>
		/// Fires synchronously from <see cref="GameObjectDirectory.Remove(Component)"/>.
		/// </remarks>
		public static event Action<Scene, Component> OnComponentUnregistered;

		// ── Raise helpers ────────────────────────────────────────────────────
		// Internal static — called from GameObjectDirectory.Add/Remove. Per-
		// subscriber try/catch ensures one bad handler does not break the
		// directory mutation path. Following the established 1.1/1.2/1.3/1.4
		// raise-helper pattern.

		internal static void RaiseOnGameObjectAdded( Scene scene, GameObject go )
		{
			var handlers = OnGameObjectAdded;
			if ( handlers is null ) return;

			foreach ( var d in handlers.GetInvocationList() )
			{
				try { ((Action<Scene, GameObject>)d)( scene, go ); }
				catch ( Exception e )
				{
					Log.Warning( e, $"Scene.OnGameObjectAdded handler threw: {e.Message}" );
				}
			}
		}

		internal static void RaiseOnGameObjectRemoved( Scene scene, GameObject go )
		{
			var handlers = OnGameObjectRemoved;
			if ( handlers is null ) return;

			foreach ( var d in handlers.GetInvocationList() )
			{
				try { ((Action<Scene, GameObject>)d)( scene, go ); }
				catch ( Exception e )
				{
					Log.Warning( e, $"Scene.OnGameObjectRemoved handler threw: {e.Message}" );
				}
			}
		}

		internal static void RaiseOnComponentRegistered( Scene scene, Component component )
		{
			var handlers = OnComponentRegistered;
			if ( handlers is null ) return;

			foreach ( var d in handlers.GetInvocationList() )
			{
				try { ((Action<Scene, Component>)d)( scene, component ); }
				catch ( Exception e )
				{
					Log.Warning( e, $"Scene.OnComponentRegistered handler threw: {e.Message}" );
				}
			}
		}

		internal static void RaiseOnComponentUnregistered( Scene scene, Component component )
		{
			var handlers = OnComponentUnregistered;
			if ( handlers is null ) return;

			foreach ( var d in handlers.GetInvocationList() )
			{
				try { ((Action<Scene, Component>)d)( scene, component ); }
				catch ( Exception e )
				{
					Log.Warning( e, $"Scene.OnComponentUnregistered handler threw: {e.Message}" );
				}
			}
		}
	}
}
