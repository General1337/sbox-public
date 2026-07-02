using Microsoft.CodeAnalysis;
using System.Threading;

namespace Sandbox;

[SkipHotload]
public class CompileGroup : IDisposable
{
	Logger log;

	/// <summary>
	/// Build notifications start of suppressed until after startup proper. That way
	/// we don't get 4 build notification windows popping up on startup.
	/// </summary>
	public static bool SuppressBuildNotifications { get; set; } = true;

	// ── engine-fork-elite-leverage Phase 1.1 (AP-100 cure) ────────────────
	// Sandbox++ engine-fork patch (NOT in Facepunch/sbox-public master).
	// Adds a global observability event so MCP plugins / IDE tooling can
	// subscribe to every CompileGroup.BuildAsync completion without owning
	// a per-instance CompileGroup reference. Companion library subscriber
	// lives at Libraries/arenula_mcp/Editor/Handlers/CompileHandler.cs
	// behind #if FORK_ENGINE. Detection marker for the library's msbuild
	// Exists() probe is written by the static initializer below.
	// Ref: docs/ai/initiatives/engine-fork-elite-leverage/charter.md §"Phase 1 patch table" 1.1
	// Ref: docs/ai/sessions/2026-05-18-engine-fork-phase1-1-compiler-on-build-completed/phase-01-design.md

	/// <summary>
	/// Fired once per <see cref="BuildAsync"/> completion (success or failure).
	/// Payload is the aggregated <see cref="CompilerOutput"/> array from this group's
	/// BuildAsync run — same data reachable via <see cref="BuildResult"/>.Output, but
	/// delivered as a static event so observability tooling (MCP plugins, IDE
	/// extensions) can subscribe globally without owning a CompileGroup reference.
	///
	/// Fires AFTER <see cref="BuildResult"/> is published and BEFORE the per-instance
	/// <see cref="OnCompileFinished"/> Action runs. Handlers MUST NOT throw —
	/// exceptions are caught and logged. Handlers run synchronously on the build's
	/// continuation thread; marshal heavy work to your own thread.
	///
	/// Multi-subscriber safe (standard C# event semantics). On hotload, the engine
	/// assembly persists (this class carries [SkipHotload]), so old library
	/// subscribers remain attached unless the library explicitly -= unsubscribes
	/// in its hotload handler. The library is expected to use a -+=  re-bind
	/// pattern across hotloads.
	/// </summary>
	public static event Action<CompilerOutput[]> OnBuildCompleted;

	internal static void RaiseOnBuildCompleted( CompilerOutput[] outputs )
	{
		var handler = OnBuildCompleted;
		if ( handler == null ) return;

		// Per-subscriber try/catch so one bad handler can't break the chain.
		foreach ( var subscriber in handler.GetInvocationList() )
		{
			try { ((Action<CompilerOutput[]>)subscriber).Invoke( outputs ); }
			catch ( System.Exception e )
			{
				Log.Warning( $"CompileGroup.OnBuildCompleted handler threw: {e.Message}" );
			}
		}
	}

	// ── engine-fork-elite-leverage Phase 1.2 (AP-100 second-pass cure) ────
	// Sandbox++ engine-fork patch (NOT in Facepunch/sbox-public master).
	// Companion to OnBuildCompleted (1.1) — fires immediately after with the
	// FAILED compilers' outputs that 1.1 deliberately excludes from
	// result.Output (see foreach loop below: `if (compiler.Output.Successful)`
	// adds to result.Output; else-branch only emits to Log.Warning).
	//
	// Library subscribers (Arenula MCP) merge these failed outputs into their
	// _lastOutputs cache so `compile.errors --severity=error` correctly
	// returns CS0103/CS0246/etc. that previously only appeared in the
	// warn-level Log.Warning stream as text-prefix "Error |".
	//
	// Ref: docs/ai/initiatives/engine-fork-elite-leverage/charter.md §"Phase 1 patch table" 1.2
	// Ref: docs/ai/sessions/2026-05-18-engine-fork-phase1-2-compile-error-severity-stream/phase-01-design.md

	/// <summary>
	/// Fired once per <see cref="BuildAsync"/> completion immediately AFTER
	/// <see cref="OnBuildCompleted"/>, but ONLY when one or more compilers in
	/// this group failed (compiler.Output.Successful == false). Payload is the
	/// array of failed compilers' CompilerOutputs. If all compilers succeeded,
	/// this event does NOT fire (zero-emission for the happy path — subscribers
	/// can rely on fire-implies-failure).
	///
	/// Companion to <see cref="OnBuildCompleted"/> (1.1), which delivers the
	/// SUCCESSFUL outputs. The split mirrors result.Output (successful only)
	/// and the per-compiler diagnostics emitted via Log.Warning at the failure
	/// branch in <see cref="BuildAsync"/>. Subscribers should treat the failed
	/// CompilerOutput[].Diagnostics as the canonical structured diagnostic
	/// source — same data the engine's Log.Warning iteration reads from.
	///
	/// Handlers MUST NOT throw — exceptions are caught and logged. Handlers
	/// run synchronously on the build's continuation thread; marshal heavy
	/// work to your own thread.
	///
	/// Multi-subscriber safe (standard C# event semantics). On hotload, the
	/// engine assembly persists (this class carries [SkipHotload]), so old
	/// library subscribers remain attached unless the library explicitly -=
	/// unsubscribes in its hotload handler.
	/// </summary>
	public static event Action<CompilerOutput[]> OnCompileFailed;

	internal static void RaiseOnCompileFailed( CompilerOutput[] failedOutputs )
	{
		var handler = OnCompileFailed;
		if ( handler == null ) return;
		if ( failedOutputs == null || failedOutputs.Length == 0 ) return;

		// Per-subscriber try/catch so one bad handler can't break the chain.
		foreach ( var subscriber in handler.GetInvocationList() )
		{
			try { ((Action<CompilerOutput[]>)subscriber).Invoke( failedOutputs ); }
			catch ( System.Exception e )
			{
				Log.Warning( $"CompileGroup.OnCompileFailed handler threw: {e.Message}" );
			}
		}
	}

	// Writes a marker file alongside Sandbox.Compiling.dll so the Sandbox++
	// MCP library's msbuild can detect the fork at compile time via Exists().
	// Marker content carries a schema version so future fork-engine bumps
	// can be detected without breaking older library builds.
	static CompileGroup()
	{
		try
		{
			var asmPath = typeof( CompileGroup ).Assembly.Location;
			if ( string.IsNullOrEmpty( asmPath ) ) return;

			var managedDir = System.IO.Path.GetDirectoryName( asmPath );
			if ( string.IsNullOrEmpty( managedDir ) ) return;

			var markerPath = System.IO.Path.Combine( managedDir, ".fork_marker" );
			if ( System.IO.File.Exists( markerPath ) ) return;

			System.IO.File.WriteAllText( markerPath, "engine-fork-elite-leverage:1\n" );
		}
		catch
		{
			// Marker is advisory — never fail engine load over diagnostics.
		}
	}

	/// <summary>
	/// The compilers within the group
	/// </summary>
	public IEnumerable<Compiler> Compilers => _compilers;

	/// <summary>
	/// The name of this compile group, for debugging/display purposes
	/// </summary>
	public string Name { get; set; } = "Compile Group";

	/// <summary>
	/// Returns true if we have compiles pending
	/// </summary>
	public bool NeedsBuild => _recompileList.Count() > 0;

	/// <summary>
	/// Returns true if we are currently in the process of building
	/// </summary>
	public bool IsBuilding { get; private set; }

	/// <summary>
	/// True if we want to print errors in the console when compiling
	/// </summary>
	public bool PrintErrorsInConsole { get; set; } = true;

	/// <summary>
	/// True if we want to use fast hotloading with this compile group
	/// </summary>
	public bool AllowFastHotload { get; set; } = false;

	/// <summary>
	/// Returns true if build was successful
	/// </summary>
	public Results BuildResult { get; private set; }

	/// <summary>
	/// Called when a compiling starts
	/// </summary>
	public Action OnCompileStarted { get; set; }

	/// <summary>
	/// Called when a compiling ends
	/// </summary>
	public Action OnCompileFinished { get; set; }

	/// <summary>
	/// Called when a compile completes successfully. Can access the result from BuildResult. 
	/// </summary>
	public Action OnCompileSuccess { get; set; }

	/// <summary>
	/// All created compilers.
	/// </summary>
	List<Compiler> _compilers = new List<Compiler>();

	/// <summary>
	/// Compilers waiting for recompile
	/// </summary>
	HashSet<Compiler> _recompileList = new();

	/// <summary>
	/// Allows providing an external way to find references
	/// </summary>
	public ICompileReferenceProvider ReferenceProvider { get; set; }

	/// <summary>
	/// AccessControl instance to use when verifying whitelist. Must be set to enable compile-time access control.
	/// </summary>
	public AccessControl AccessControl { get; set; }

	public CompileGroup( string name )
	{
		log = new Logger( $"CompileGroup/{name}" );
		Name = name;

		log.Trace( "Created" );
	}

	/// <summary>
	/// Shut everything down
	/// </summary>
	public void Dispose()
	{
		_compilers.Clear();
		_recompileList.Clear();

		log.Trace( "Dispose" );
	}

	/// <summary>
	/// Create a new compiler in this group.
	/// </summary>
	public Compiler CreateCompiler( string name, string path, Compiler.Configuration settings )
	{
		if ( FindCompilerByPackageName( name ) != null )
			throw new System.Exception( $"Compiler named {name} already exists" );

		log.Trace( $"CreateCompiler '{name}' ({path})" );

		var compiler = new Compiler( this, name, path, settings );
		compiler.MarkForRecompile();

		lock ( _compilers )
		{
			_compilers.Add( compiler );
		}

		return compiler;
	}

	public Compiler GetOrCreateCompiler( string name )
	{
		var compiler = FindCompilerByPackageName( name );
		if ( compiler is not null ) return compiler;

		log.Trace( $"CreateCompiler '{name}'" );

		compiler = new Compiler( this, name );

		lock ( _compilers )
		{
			_compilers.Add( compiler );
		}

		return compiler;
	}

	/// <summary>
	/// Recompile <paramref name="compiler"/> as soon as is appropriate.
	/// </summary>
	internal void MarkForRecompile( Compiler compiler )
	{
		if ( _recompileList.Add( compiler ) )
		{
			log.Trace( $"MarkForRecompile ({compiler.Name})" );
		}
	}

	/// <summary>
	/// No longer want/need to recompile <paramref name="compiler"/>.
	/// </summary>
	internal void ClearForRecompile( Compiler compiler )
	{
		_recompileList.Remove( compiler );
	}

	/// <summary>
	/// Returns true if this group will compile <paramref name="compiler"/> next build.
	/// </summary>
	internal bool CompilerNeedsBuild( Compiler compiler ) => _recompileList.Contains( compiler );

	/// <summary>
	/// Recompile anything that depends on us too
	/// </summary>
	void MarkDependantsForRecompile( Compiler compiler, HashSet<Compiler> found = null )
	{
		found ??= new();

		if ( !found.Add( compiler ) )
			return;

		// If we're compiling base.dll,
		// then we need to compile sandbox.dll too

		foreach ( var c in _compilers )
		{
			if ( c == compiler ) continue;
			if ( !c.HasReference( compiler.AssemblyName, true ) ) continue;

			MarkForRecompile( c );
			MarkDependantsForRecompile( c, found );

		}
	}

	internal void OnCompilerDisposed( Compiler compiler )
	{
		lock ( _compilers )
		{
			log.Trace( $"Compiler Disposed ({compiler.Name})" );
			_compilers.Remove( compiler );
			_recompileList.Remove( compiler );
		}
	}

	internal Compiler FindCompilerByAssemblyName( string assemblyName )
	{
		lock ( _compilers )
		{
			return _compilers.SingleOrDefault( x => String.Equals( x.AssemblyName, assemblyName, StringComparison.OrdinalIgnoreCase ) );
		}
	}

	internal Compiler FindCompilerByPackageName( string packageName )
	{
		lock ( _compilers )
		{
			return _compilers.SingleOrDefault( x => String.Equals( x.Name, packageName, StringComparison.OrdinalIgnoreCase ) );
		}
	}

	/// <summary>
	/// Build the compilers
	/// </summary>
	public async Task<bool> BuildAsync()
	{
		if ( IsBuilding )
			throw new System.Exception( "Tried to build but a build is already in process" );

		log.Trace( $"BuildAsync Start" );

		if ( !NeedsBuild )
		{
			log.Trace( $"BuildAsync Finish - no build needed" );
			return BuildResult.Success;
		}

		IsBuilding = true;
		BuildResult = default;

		Results result = default;
		result.Failed = true;
		result.Diagnostics = new();
		result.Output = new();

		BuildResult = result;

		if ( !NeedsBuild )
		{
			// give the impression that everything is fine
			result.Failed = false;
			BuildResult = result;

			IsBuilding = false;
			log.Trace( $"BuildAsync Finish - no build needed" );
			return true;
		}

		try
		{
			var timer = Stopwatch.StartNew();

			//
			// Make sure all dependancies of the compilers we're compiling and marked to compile too
			// This will also throw an exception if we have cyclic dependancies
			//
			foreach ( var compiler in _recompileList.ToArray() )
			{
				MarkDependantsForRecompile( compiler );
			}

			//
			// When compiling we want to include the out of date compiler and everything it references.
			// The other compilers that aren't out of date won't be compiled - but they will get returned
			// as part of the output.
			//
			var compileList = _recompileList.SelectMany( x => x.GetReferencedCompilers() ).Distinct().ToList();
			var toCompile = compileList.Where( x => x.NeedsBuild ).ToArray();

			// Clear this now so that if something needs recompiling while we're compiling
			// it'll add to the list and recompile in the next run
			_recompileList.Clear();

			if ( compileList.Count == 0 )
				throw new System.Exception( "Compile list is empty - this should never happen (NeedsBuild check should prevent it)" );

			log.Trace( $"Building {compileList.Count()} compilers" );

			OnCompileStarted.InvokeWithWarning();

			// Reset each compiler so they're ready to reference each other during the build

			foreach ( var compiler in toCompile )
			{
				compiler.PreBuild();
			}

			// Do the actual build, let compilers wait for each other as needed

			await Task.WhenAll( toCompile.Select( x => x.BuildAsync() ) );

			//
			// Accumulate the build result
			//
			bool allSuccess = compileList.All( x => x.BuildSuccess );
			result.Failed = !allSuccess;

			// engine-fork-elite-leverage Phase 1.2 — capture failed compilers' outputs
			// alongside the existing result.Output aggregation so a sibling event
			// (RaiseOnCompileFailed) can deliver them to observability subscribers.
			// result.Output stays successful-only to preserve the 1.1 invariant.
			var failedOutputs = new List<CompilerOutput>();

			foreach ( var compiler in toCompile.OrderBy( x => x.DependencyIndex() ) )
			{
				if ( compiler.Output is null )
					continue;

				result.Diagnostics.AddRange( compiler.Output.Diagnostics );

				if ( compiler.Output.Successful )
				{
					result.Output.Add( compiler.Output );
				}
				else
				{
					failedOutputs.Add( compiler.Output );

					// if we were supressing build notifications then this was probably
					// during startup. So print them so they show in the log file to give
					// us a clue as to what's gone wrong.
					if ( PrintErrorsInConsole || SuppressBuildNotifications )
					{
						Log.Warning( $"Compile of '{compiler.Name}' Failed:" );
						foreach ( var diag in compiler.Output.Diagnostics )
						{
							if ( diag.Severity <= DiagnosticSeverity.Info ) continue;

							Log.Warning( $"{diag.Severity} | {diag.GetMessage()} - {diag.Location.SourceTree?.FilePath}:{diag.Location.GetLineSpan().StartLinePosition}" );
						}
					}
				}

			}

			BuildResult = result;

			// engine-fork-elite-leverage Phase 1.1 — raise the static observability
			// event BEFORE the per-instance OnCompileFinished so subscribers see a
			// consistent state (BuildResult populated, IsBuilding still true).
			RaiseOnBuildCompleted( result.Output?.ToArray() ?? System.Array.Empty<CompilerOutput>() );

			// engine-fork-elite-leverage Phase 1.2 — raise sibling event with failed
			// outputs (zero-emission when all succeeded). Fires AFTER OnBuildCompleted
			// so library subscribers can merge into a coherent _lastOutputs cache.
			RaiseOnCompileFailed( failedOutputs.ToArray() );

			OnCompileFinished.InvokeWithWarning();

			//
			// There was a build error - don't load them!
			//
			if ( !allSuccess )
			{
				return false;
			}

			timer.Restart();

			log.Trace( $"OnCompileSuccess" );
			OnCompileSuccess?.InvokeWithWarning();

			return true;
		}
		finally
		{
			IsBuilding = false;
			log.Trace( $"BuildAsync Finish" );
		}
	}

	/// <summary>
	/// Find a reference for this dll. Throws if a reference is not found / invalid, and returns <see langword="null"/> if
	/// a reference should be silently ignored (like self-referencing).
	/// </summary>
	internal async Task<PortableExecutableReference> FindReferenceAsync( string reference, Compiler fromCompiler )
	{
		// To retain backwards compatibility
		if ( reference == "package.local.base" )
			reference = "package.base";

		var compiler = FindCompilerByAssemblyName( reference );
		if ( compiler != null )
		{
			//
			// Ignore cyclic dependencies to avoid compilers waiting for each other forever
			//

			if ( compiler.GetReferencedCompilers().Contains( fromCompiler ) )
				return null;

			//
			// If we're relying on a compiler to build, and it's building,
			// give it a few seconds to build..
			//

			const double timeoutSeconds = 120d;

			var output = await compiler.GetCompileOutputAsync().WaitAsync( TimeSpan.FromSeconds( timeoutSeconds ) );

			if ( !output.Successful )
				throw new System.Exception( $"Broken Reference: {reference} (the compiler failed)" );

			if ( output.MetadataReference is null )
				throw new System.Exception( $"Broken Reference: {reference} (the metadata is null)" );

			return output.MetadataReference;
		}

		//
		// Search globally if we have that capability
		//
		if ( ReferenceProvider?.Lookup( reference ) is { } providedRef )
		{
			return providedRef;
		}

		//
		// package. references aren't going to be in FrameworkReferences
		// so we'll complain about them here
		//
		if ( reference.StartsWith( "package." ) )
		{
			throw new System.Exception( $"Couldn't find reference {reference}" );
		}

		return FrameworkReferences.FindByName( reference );
	}

	/// <summary>
	/// Reset the compile group. Clear errors and outputs.
	/// </summary>
	public void Reset()
	{
		if ( IsBuilding )
			throw new System.Exception( "Tried to reset CompileGroup while compiling!" );

		BuildResult = default;
	}

	public async Task WaitForCompile( CancellationToken token )
	{
		while ( NeedsBuild || IsBuilding )
		{
			await Task.Delay( 10 );

			if ( token.IsCancellationRequested )
				return;
		}
	}

	public struct Results
	{
		public bool Success => !Failed;

		public bool Failed { get; set; }
		public List<Microsoft.CodeAnalysis.Diagnostic> Diagnostics { get; set; }
		public List<CompilerOutput> Output { get; set; }

		public string BuildDiagnosticsString( DiagnosticSeverity severity = DiagnosticSeverity.Warning )
		{
			if ( Diagnostics == null )
				return "No build result diagnostics found";

			var lines = Diagnostics
					.Where( x => x.Severity >= severity )
							.Select( diag => $"{diag.Severity} | {diag.GetMessage()} - {diag.Location?.SourceTree?.FilePath}:{diag?.Location?.GetLineSpan().StartLinePosition}" );
			return string.Join( "\n", lines );
		}
	}
}

/// <summary>
/// Allows you to look up references for a compiler.
/// </summary>
public interface ICompileReferenceProvider
{
	/// <summary>
	/// Find a reference for this dll
	/// </summary>
	PortableExecutableReference Lookup( string reference );
}
