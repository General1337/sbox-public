// [HANDOFF-TRUST: feature-build additive raise-call insertion, no hypothesis from inherited handoffs applies; Phase 2.4 closes Phase 2 charter exit criterion]
using System;

namespace Editor;

// ── engine-fork-elite-leverage Phase 1.6 / 2.4 (asset-compile-error stream) ──
// Sandbox++ engine-fork patch (NOT in Facepunch/sbox-public master).
// Adds one global observability event so MCP plugins / IDE tooling can
// subscribe to asset compile/import failures without scraping the
// Log.Warning stream (which is ring-buffered and warn-prefix-encoded as
// "Error |", making structured consumption brittle).
//
// Companion library subscriber lives at
// Libraries/arenula_mcp/Editor/Handlers/AssetSystemHandler.cs in the
// downstream Sandbox++ repo, bound via reflection so the same .dll runs
// against fork and stable Facepunch engines.
//
// Architectural rationale: mirrors the established 1.1–1.5 fork-patch
// pattern — instrument the producer (here: the Log.Warning sites in
// NativeAsset.cs:312/328/334 + AssetThumbnail.cs:175) rather than the
// consumers. Additive only — no behavior change, no visibility promotion,
// existing Log.Warning lines preserved. Upstream-PR-eligible.
//
// Payload (Asset, string error, Exception?) carries:
//   • Asset — typed reference; subscribers can read asset.Path / .AbsolutePath
//     / .AssetType etc. at handler-time. May be null IFF the error fires
//     pre-Asset-creation (e.g. source-file resolution in
//     ResourceCompileContextImp); current 1.6 emission sites all carry a
//     real Asset reference (NativeAsset : Asset, so `this`).
//   • string error — human-readable message matching the corresponding
//     Log.Warning text (the canonical engine-side error description).
//   • Exception? exception — non-null only when an exception was caught
//     at the failure site (today: only AssetThumbnail.cs:175 thumbnail
//     compile catch). Null for the IsCompileFailed / not-compiled / timeout
//     class.
//
// Asset-error events are intrinsically low-frequency (per-asset, not per-
// tick); fire synchronously from the canonical Log.Warning sites.
//
// Cures the editor.get_log filter:"compile" log-scrape workaround (the
// MCP plugin currently scrapes warn-level log entries to surface asset
// errors; this collapses it into passive subscription). Unblocks downstream
// Phase 2.4 (feed.assets.subscribe) — which is the same session this patch
// lands in (hybrid engine-fork-phase1-6 + phase2-4).
//
// Ref: docs/ai/initiatives/engine-fork-elite-leverage/charter.md §"Phase 1 patch table" 1.6 / §"Phase 2" 2.4
// Ref: docs/ai/sessions/2026-05-19-engine-fork-phase2-4-feed-assets-subscribe/plan.md
// Sibling commits: 27e634ae CompileGroup.OnBuildCompleted, e4165169 CompileGroup.OnCompileFailed,
//   ed7b424d Hotload.OnComplete, fe31467b TypeLibrary.OnAssemblyRegistered + ConVarSystem.OnAssemblyRegistered,
//   a93e7971 Scene.OnGameObject{Added,Removed} + Scene.OnComponent{Registered,Unregistered}

public static partial class AssetSystem
{
	// [HANDOFF-TRUST: additive feature-build cleanup of CS1570 errors; independent of any inherited handoff hypothesis — Phase 2.4 wrapper work]
	/// <summary>
	/// Fired when an asset fails to compile or import. Payload is
	/// <c>(Asset asset, string errorMessage, Exception exception)</c>:
	/// <list type="bullet">
	///   <item><c>asset</c> — typed reference; subscribers read <c>asset.Path</c>,
	///     <c>asset.AbsolutePath</c>, <c>asset.AssetType</c> etc. at handler-time.</item>
	///   <item><c>errorMessage</c> — human-readable description matching the
	///     corresponding <c>Log.Warning</c> text.</item>
	///   <item><c>exception</c> — non-null only when an exception was caught
	///     at the failure site; null for the not-compiled / compile-failed /
	///     timeout classes that don't have an inner exception.</item>
	/// </list>
	/// Handlers MUST NOT throw — exceptions are caught and logged.
	/// Multi-subscriber safe (standard C# event semantics).
	/// </summary>
	/// <remarks>
	/// Fires synchronously from the canonical <c>Log.Warning</c>
	/// sites in <c>NativeAsset.CompileIfNeededAsync</c> (not-compiled / compile-
	/// failed / timeout) and <c>AssetThumbnail</c> (thumbnail-compile exception).
	/// Observability-only — do NOT mutate the asset or trigger another compile
	/// from a handler; the emission sites are not reentrancy-protected.
	/// </remarks>
	public static event Action<Asset, string, Exception> OnAssetCompileFailed;

	// ── Raise helper ─────────────────────────────────────────────────────
	// Internal static — called from NativeAsset.cs + AssetThumbnail.cs.
	// Per-subscriber try/catch ensures one bad handler does not break the
	// asset-compile path. Following the established 1.1–1.5 raise-helper
	// pattern.

	internal static void RaiseOnAssetCompileFailed( Asset asset, string errorMessage, Exception exception )
	{
		var handlers = OnAssetCompileFailed;
		if ( handlers is null ) return;

		foreach ( var d in handlers.GetInvocationList() )
		{
			try { ((Action<Asset, string, Exception>)d)( asset, errorMessage, exception ); }
			catch ( Exception e )
			{
				Log.Warning( e, $"AssetSystem.OnAssetCompileFailed handler threw: {e.Message}" );
			}
		}
	}
}
