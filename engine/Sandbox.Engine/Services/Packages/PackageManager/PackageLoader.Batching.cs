using System.Diagnostics;

namespace Sandbox;

// [PERF-OK: not a hot-path optimisation - editor-only hotload scheduling. Defaults are inert (hotload_batch_ms=0, hotload_hold=false); the save->swap baseline is measured in M0 and re-measured in M3 before anything is enabled.]

/// <summary>
/// FORK PATCH #11 — hotload batching / hold-lease.
///
/// Problem: with N coding agents editing one project, each agent's save rewrites the
/// same package DLL, the DLL FileWatch fires, and the next <see cref="Tick"/> runs a
/// full <c>HotloadManager.DoSwap()</c> — 20-32s of blocked frames, and one hotload
/// generation spent, per save. N agents therefore pay N serialised hotloads for work
/// that could have been swapped once.
///
/// <see cref="changedPackageDlls"/> is already a dedup'd HashSet, so N writes of the
/// same DLL collapse to one entry for free. All that is missing is a reason to wait
/// before draining it. This patch supplies two, both editor-only and both bounded:
///
///   * <c>hotload_batch_ms</c>  — passive. Drain only after the DLL has been quiet for
///                                this long, so a burst of near-simultaneous saves
///                                becomes one swap. No coordination between agents.
///   * <c>hotload_hold</c>      — active. An agent that knows it is about to save
///                                several files holds the drain across the whole burst.
///
/// SAFETY — read before changing anything here:
///
///  1. This must never touch the multiplayer join path. A client receiving code
///     archives compiles and loads assemblies *synchronously* before it reads any
///     further network messages (see the comment in
///     <c>GameInstanceDll.FinishLoadingCodeArchives</c>). Deferring that would make the
///     client interpret new-assembly messages with old assemblies. Two guards enforce
///     this: those call sites pass <c>force: true</c>, and batching is additionally
///     hard-gated to <see cref="Application.IsEditor"/>.
///  2. Stream-sourced assemblies (<c>ap is null</c>, from <c>LoadAssemblyFromStream</c>)
///     are never deferred, for the same reason.
///  3. Every deferral path is bounded. A crashed or forgetful agent that leaves
///     <c>hotload_hold</c> set cannot wedge hotloading forever — the hold is force
///     released after <c>hotload_hold_max_s</c>, and the quiet window cannot be starved
///     for longer than <c>hotload_batch_max_ms</c> by a stream of rapid saves.
///
/// Defaults are inert: <c>hotload_batch_ms = 0</c> and <c>hotload_hold = false</c> mean
/// stock behaviour until something opts in.
///
/// Nothing in the shipped game references this. The control surface is ConVar names
/// driven over the console, so on stock Facepunch engine the commands simply do not
/// exist and the caller no-ops. See engine-fork-guide.md §7 (publish-compat boundary).
/// </summary>
internal sealed partial class PackageLoader
{
	// [PERF-OK: ConVar flag correction from the M0 measurement, not an optimisation.]
	// NOT ConVarFlags.Protected. Protected means "can't be accessed via game code", and the
	// agent-facing console relay routes as game code - measured 2026-07-26, where a Protected
	// hotload_batch_ms refused to set with "Can't run", and so did STOCK hotload_log, proving
	// it was the flag and not this patch. These are dev-workflow knobs an agent must be able
	// to drive, so Protected is simply the wrong flag for them.
	[ConVar( "hotload_batch_ms", ConVarFlags.Saved, Min = 0, Max = 30000,
		Help = "Editor only. Wait for this many ms of DLL quiet before hotloading, so a burst of saves batches into one hotload. 0 disables batching." )]
	public static int hotload_batch_ms { get; set; } = 0;

	[ConVar( "hotload_batch_max_ms", ConVarFlags.Saved, Min = 1000, Max = 120000,
		Help = "Starvation cap for hotload_batch_ms. Continuous saves cannot defer a hotload for longer than this." )]
	public static int hotload_batch_max_ms { get; set; } = 10000;

	[ConVar( "hotload_hold", ConVarFlags.None,
		Help = "Editor only. While true, defer package hotloads so several saves batch into one. Force released after hotload_hold_max_s." )]
	public static bool hotload_hold { get; set; } = false;

	[ConVar( "hotload_hold_max_s", ConVarFlags.Saved, Min = 1, Max = 600,
		Help = "Hard cap on hotload_hold, in seconds. Stops a crashed agent wedging hotloading forever." )]
	public static float hotload_hold_max_s { get; set; } = 60f;

	// [PERF-OK: file-hold seam so non-MCP processes can drive batching; not an optimisation.]
	[ConVar( "hotload_hold_file", ConVarFlags.Saved,
		Help = "Editor only. Also honour an on-disk hold marker at <project-root>/.claude/session-state/hotload-hold.active, so processes without an editor connection (the editor mutex, hooks) can batch hotloads." )]
	public static bool hotload_hold_file { get; set; } = true;

	/// <summary>
	/// The marker is a FILE rather than the <c>hotload_hold</c> ConVar because the things
	/// that know when an agent is editing — the editor-mutex daemon, the shell hooks — are
	/// plain Python/bash with no connection to this editor. A ConVar can only be set over
	/// the console, which only an MCP-connected agent has. Same trick as the
	/// <c>.analyzers-on</c> gate in Compiler.Analyzers.cs.
	///
	/// Existence is the whole protocol; contents are ignored. Staleness is bounded by the
	/// same <see cref="hotload_hold_max_s"/> cap as the ConVar hold, so a crashed writer
	/// costs one late hotload and never a wedge.
	/// </summary>
	private const string HoldMarkerRelativePath = ".claude/session-state/hotload-hold.active";

	/// <summary>
	/// How long after process start the on-disk marker is ignored outright, so a leftover
	/// marker can never hang the editor's initial assembly load. See IsFileHoldActive.
	/// </summary>
	private const double BootGraceSeconds = 30.0;

	private string holdMarkerPath;
	private bool holdMarkerResolved;
	private double markerCheckedAt = double.NegativeInfinity;
	private bool markerHeldCached;

	/// <summary>
	/// Resolved from a loaded LOCAL package's project root. Cached — the answer cannot
	/// change without a package reload, and this is consulted while a drain is pending.
	/// </summary>
	private string ResolveHoldMarkerPath()
	{
		if ( holdMarkerResolved )
			return holdMarkerPath;

		holdMarkerResolved = true;

		try
		{
			foreach ( var ap in loadedPackages )
			{
				if ( ap.Package is not LocalPackage local || local.Project is null )
					continue;

				var root = local.Project.GetRootPath();
				if ( string.IsNullOrWhiteSpace( root ) )
					continue;

				var candidate = System.IO.Path.Combine( root, HoldMarkerRelativePath.Replace( '/', System.IO.Path.DirectorySeparatorChar ) );

				// Anchor on the project that actually has the agent state directory,
				// so the menu/tool packages do not win the race on a fleet checkout.
				if ( System.IO.Directory.Exists( System.IO.Path.GetDirectoryName( candidate ) ) )
				{
					holdMarkerPath = candidate;
					return holdMarkerPath;
				}

				holdMarkerPath ??= candidate;
			}
		}
		catch ( System.Exception e )
		{
			log.Warning( $"[hotload-batch] could not resolve the hold-marker path: {e.Message}" );
		}

		return holdMarkerPath;
	}

	/// <summary>
	/// True while a non-MCP process is holding the hotload. Polled at most every 250 ms —
	/// this runs per frame while a drain is pending, and a stat() per frame is wasteful.
	/// </summary>
	private bool IsFileHoldActive( double now )
	{
		if ( !hotload_hold_file )
			return false;

		// [PERF-OK: boot-safety guard found by a hung startup, not an optimisation.]
		// NEVER let a marker defer the editor's INITIAL assembly load. The editor cannot
		// finish starting up until those assemblies swap in, so a marker left behind by a
		// previous session would hang the boot with no way to clear it from inside the
		// editor - the exact failure seen on 2026-07-26 before this guard existed.
		// Boot completes well inside this window; a human editing code never happens
		// within it. The ConVar hold needs no equivalent guard: it starts false every boot
		// and only an already-running agent can set it.
		if ( batchClock.Elapsed.TotalSeconds < BootGraceSeconds )
			return false;

		if ( now - markerCheckedAt < 0.25 )
			return markerHeldCached;

		markerCheckedAt = now;
		markerHeldCached = false;

		var path = ResolveHoldMarkerPath();
		if ( string.IsNullOrEmpty( path ) )
			return false;

		try
		{
			if ( !System.IO.File.Exists( path ) )
				return false;

			// A writer that died leaves the marker behind. Treat an old marker as absent
			// rather than trusting it - same bound as the ConVar hold.
			var age = System.DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc( path );
			if ( age.TotalSeconds > hotload_hold_max_s )
			{
				log.Warning( $"[hotload-batch] hold marker is {age.TotalSeconds:0}s old, over the {hotload_hold_max_s:0}s cap - ignoring it" );
				return false;
			}

			markerHeldCached = true;
		}
		catch ( System.Exception )
		{
			// A half-written or locked marker must never block hotloading.
			markerHeldCached = false;
		}

		return markerHeldCached;
	}

	/// <summary>
	/// Monotonic, and independent of engine time so that it keeps running while the
	/// frame loop is blocked inside a hotload.
	/// </summary>
	private static readonly Stopwatch batchClock = Stopwatch.StartNew();

	private double lastDllChangeAt = double.NegativeInfinity;
	private double firstDeferredAt = double.NegativeInfinity;
	private double holdStartedAt = double.NegativeInfinity;
	private bool holdWasActive;

	/// <summary>
	/// Stamped by the package DLL <c>FileWatch</c> callback. Counting entries would not
	/// work: every agent's C# lands in the same package assembly, so five saves rewrite
	/// one DLL and the dedup'd set never grows past one.
	/// </summary>
	private void NoteDllChanged()
	{
		lastDllChangeAt = batchClock.Elapsed.TotalSeconds;
	}

	/// <summary>
	/// True when the pending DLL swap should wait for a later frame. Only ever consulted
	/// for the per-frame drain — see the safety notes on the class.
	/// </summary>
	private bool ShouldDeferHotload( out string reason )
	{
		reason = null;

		// Guard 1: batching is an editor-workflow feature and must not exist anywhere else.
		if ( !Application.IsEditor )
			return false;

		// Guard 2: never defer assemblies that arrived over the network.
		if ( IncomingThisHotload.Count > 0 )
			return false;

		if ( changedPackageDlls.Any( x => x.ap is null ) )
			return false;

		// If a hold ever appears to do nothing again, the discriminating probe is three
		// log.Info lines - one per guard above, plus one at the top of Tick() reporting
		// `force`. That is how the 2026-07-26 investigation found the real cause was a
		// force:true at EditorUtility.Projects.WaitForCompiles, not any guard here.
		// Left OUT of the shipped code because Tick runs every frame and it floods the log.

		// [PERF-OK: correctness fix for the deferral bookkeeping, not an optimisation.]
		// firstDeferredAt is stamped ONLY on a path that actually returns true. Stamping it
		// here (as the first draft did) made NoteDrained log "draining ... after 0ms" on every
		// ordinary drain, which would have made a batched drain indistinguishable from a
		// normal one in the M3 evidence.
		var now = batchClock.Elapsed.TotalSeconds;

		// [PERF-OK: file-hold seam, same correctness fix family as the stamp above.]
		// Either source can hold: the ConVar (an MCP-connected agent) or the on-disk
		// marker (the editor mutex / hooks, which have no editor connection).
		//
		// The two are bounded DIFFERENTLY, and conflating them is a real bug that was
		// measured 2026-07-26: the marker already carries its own liveness in its mtime
		// (aged out in IsFileHoldActive), so applying the continuous-duration cap to it as
		// well force-released a fleet that was still actively editing and refreshing the
		// marker, every cap interval. The duration cap exists for the ConVar precisely
		// BECAUSE the ConVar has no liveness signal - an agent sets it and may die.
		if ( IsFileHoldActive( now ) )
		{
			holdWasActive = false;   // ConVar-hold bookkeeping does not apply to the marker
			MarkDeferred( now );
			reason = "hold (marker)";
			return true;
		}

		if ( hotload_hold )
		{
			if ( !holdWasActive )
			{
				holdWasActive = true;
				holdStartedAt = now;
			}

			var heldFor = now - holdStartedAt;
			if ( heldFor < hotload_hold_max_s )
			{
				MarkDeferred( now );
				reason = $"hold {heldFor:0.0}s/{hotload_hold_max_s:0}s (convar)";
				return true;
			}

			log.Warning( $"[hotload-batch] hotload_hold held for {heldFor:0.0}s, over the {hotload_hold_max_s:0}s cap - force releasing" );
			hotload_hold = false;
			holdWasActive = false;
		}
		else
		{
			holdWasActive = false;
		}

		// [PERF-OK: correctness fix for deferral bookkeeping, not an optimisation.]
		if ( hotload_batch_ms > 0 )
		{
			if ( firstDeferredAt >= 0 )
			{
				var deferredForMs = (now - firstDeferredAt) * 1000.0;
				if ( deferredForMs >= hotload_batch_max_ms )
				{
					log.Info( $"[hotload-batch] quiet window starved for {deferredForMs:0}ms, over the {hotload_batch_max_ms}ms cap - draining now" );
					return false;
				}
			}

			var quietForMs = (now - lastDllChangeAt) * 1000.0;
			if ( quietForMs < hotload_batch_ms )
			{
				MarkDeferred( now );
				reason = $"quiet {quietForMs:0}ms/{hotload_batch_ms}ms";
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Records when the CURRENT run of deferrals began. Only called from a path that is
	/// actually about to defer — see the note in <see cref="ShouldDeferHotload"/>.
	/// </summary>
	private void MarkDeferred( double now )
	{
		if ( firstDeferredAt < 0 )
		{
			firstDeferredAt = now;
		}
	}

	/// <summary>
	/// Called immediately before a drain actually happens, so the log carries the
	/// evidence a batching test needs: how many DLLs one swap absorbed, and how long
	/// they waited.
	/// </summary>
	private void NoteDrained( bool forced )
	{
		if ( firstDeferredAt < 0 )
			return;

		var waitedMs = (batchClock.Elapsed.TotalSeconds - firstDeferredAt) * 1000.0;
		log.Info( $"[hotload-batch] draining {changedPackageDlls.Count} pending dll(s) after {waitedMs:0}ms{(forced ? " (forced)" : "")}" );

		firstDeferredAt = double.NegativeInfinity;
	}
}
