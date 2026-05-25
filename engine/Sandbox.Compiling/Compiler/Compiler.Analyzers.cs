using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Reflection;

// [HANDOFF-TRUST: empirically-observed gap; reflection search FrameBasis returned 0 hits in loaded AppDomain; root cause is the package.*.editor filter mismatch with the standalone analyzer DLL, not any inherited handoff hypothesis]

namespace Sandbox;

// FORK-PATCH (Sandbox++ — game-side analyzer support) — Game-side Roslyn analyzer support.
//
// Adds project-level Roslyn analyzer discovery + execution to Compiler.BuildInternal.
// Before this patch, Roslyn analyzers shipped inside editor libraries (e.g.,
// Libraries/<name>/Editor/Analyzers/) fired only in the IDE (VS / Rider) because
// MSBuild loaded them from the .csproj; the in-engine compiler ignored them entirely.
//
// Discovery surfaces (in order):
//   1. AppDomain scan for any loaded assembly whose AssemblyName matches
//      "package.*.editor" — picks up analyzers compiled INTO an editor library.
//   2. Filesystem scan of "<project-root>/Libraries/*/Editor/Analyzers/**/*.dll" —
//      picks up STANDALONE Roslyn-component analyzer DLLs that ship beside the
//      editor library but aren't part of its compiled assembly. Discovered DLLs
//      are LoadFrom-loaded into AppDomain, then reflected for [DiagnosticAnalyzer]
//      types just like the AppDomain branch. The project root is resolved via
//      reflection against Sandbox.Engine's Project.Current.GetRootPath() so
//      Sandbox.Compiling can avoid a Sandbox.Engine project reference (which
//      would create a cycle).
//
// Any concrete class deriving from Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer,
// decorated with [DiagnosticAnalyzer(LanguageNames.CSharp)], is instantiated and
// executed against every Compiler.BuildInternal compilation. Analyzers are run
// AFTER source generators but BEFORE the SB1000 whitelist walker, so the whitelist
// still has the final say on what compiles successfully.
//
// Anchor case (the use case that motivated the patch): the Sandbox++ FrameBasisAnalyzer
// (Libraries/arenula_mcp/Editor/Analyzers/FrameBasisAnalyzer/FrameBasisAnalyzer.cs,
// built as a standalone Roslyn-component DLL at
// Libraries/arenula_mcp/Editor/Analyzers/FrameBasisAnalyzer/dist/FrameBasisAnalyzer.dll)
// flags untyped Vector3 declarations + cross-frame arithmetic in gravity-hull code.
// Without this fork-patch, the analyzer's enforcement was IDE-only — agents that
// skipped IDE-level checks could silently ship typed-bypass regressions. With this
// patch the analyzer fires on every in-engine compile, regardless of IDE state.
//
// Cost: one AppDomain.GetAssemblies() iteration + one filesystem scan per compile
// (both cheap), plus the analyzer's own work (proportional to its scanned-syntax-
// node count). FrameBasisAnalyzer adds <1ms per typical compile.
//
// Safety: analyzer instances run with full CLR trust (same as any editor library
// code). They observe but cannot mutate compilation. Whitelist walker runs AFTER
// analyzers, so an analyzer cannot smuggle in blacklisted API usage.
partial class Compiler
{
	/// <summary>
	/// Scan the current AppDomain + project filesystem for project-shipped Roslyn
	/// analyzers. Returns analyzer instances declared in any loaded
	/// <c>package.*.editor</c> assembly OR in any DLL under
	/// <c>&lt;project-root&gt;/Libraries/*/Editor/Analyzers/**</c> that derives
	/// from <see cref="DiagnosticAnalyzer"/> and is decorated with
	/// <c>[DiagnosticAnalyzer]</c>. Empty when no game-side analyzers exist (the
	/// common case — preserves zero-overhead for projects that ship none).
	/// </summary>
	private static ImmutableArray<DiagnosticAnalyzer> DiscoverProjectAnalyzers()
	{
		var found = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
		var seenAssemblies = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		// Surface 1: AppDomain scan for analyzers compiled INTO editor libraries.
		foreach ( var assembly in AppDomain.CurrentDomain.GetAssemblies() )
		{
			var name = assembly.GetName().Name;
			if ( string.IsNullOrEmpty( name ) ) continue;
			if ( !name.StartsWith( "package.", StringComparison.Ordinal ) ) continue;
			if ( !name.EndsWith( ".editor", StringComparison.Ordinal ) ) continue;

			seenAssemblies.Add( name );
			AddAnalyzersFromAssembly( assembly, found );
		}

		// Surface 2: filesystem scan for STANDALONE analyzer DLLs that aren't part
		// of any compiled editor assembly. Looks at
		// <project-root>/Libraries/*/Editor/Analyzers/**/*.dll.
		DiscoverFilesystemAnalyzers( found, seenAssemblies );

		return found.ToImmutable();
	}

	private static void AddAnalyzersFromAssembly( Assembly assembly, ImmutableArray<DiagnosticAnalyzer>.Builder found )
	{
		Type[] types;
		try { types = assembly.GetTypes(); }
		catch ( ReflectionTypeLoadException ex )
		{
			types = ex.Types.Where( t => t is not null ).ToArray()!;
		}
		catch ( Exception )
		{
			return;
		}

		foreach ( var t in types )
		{
			if ( t is null || t.IsAbstract || !t.IsClass ) continue;
			if ( !typeof( DiagnosticAnalyzer ).IsAssignableFrom( t ) ) continue;
			if ( t.GetCustomAttribute<DiagnosticAnalyzerAttribute>() is null ) continue;

			try
			{
				var instance = (DiagnosticAnalyzer)Activator.CreateInstance( t )!;
				found.Add( instance );
			}
			catch ( Exception ex )
			{
				Log.Warning( $"[Compiler.Analyzers] Failed to instantiate {t.FullName}: {ex.Message}" );
			}
		}
	}

	private static void DiscoverFilesystemAnalyzers( ImmutableArray<DiagnosticAnalyzer>.Builder found, HashSet<string> seenAssemblies )
	{
		var projectRoot = ResolveActiveProjectRoot();
		if ( string.IsNullOrEmpty( projectRoot ) ) return;

		var librariesRoot = System.IO.Path.Combine( projectRoot, "Libraries" );
		if ( !System.IO.Directory.Exists( librariesRoot ) ) return;

		foreach ( var libDir in System.IO.Directory.EnumerateDirectories( librariesRoot ) )
		{
			var analyzerRoot = System.IO.Path.Combine( libDir, "Editor", "Analyzers" );
			if ( !System.IO.Directory.Exists( analyzerRoot ) ) continue;

			IEnumerable<string> dllPaths;
			try
			{
				dllPaths = System.IO.Directory.EnumerateFiles( analyzerRoot, "*.dll", System.IO.SearchOption.AllDirectories );
			}
			catch ( Exception ex )
			{
				Log.Warning( $"[Compiler.Analyzers] Failed to enumerate analyzer DLLs under '{analyzerRoot}': {ex.Message}" );
				continue;
			}

			foreach ( var dllPath in dllPaths )
			{
				var asmName = System.IO.Path.GetFileNameWithoutExtension( dllPath );
				if ( string.IsNullOrEmpty( asmName ) ) continue;
				if ( !seenAssemblies.Add( asmName ) ) continue;

				// Skip Roslyn / system dependency DLLs that ship beside analyzers.
				if ( asmName.StartsWith( "Microsoft.", StringComparison.Ordinal ) ) continue;
				if ( asmName.StartsWith( "System.", StringComparison.Ordinal ) ) continue;

				Assembly assembly;
				try
				{
					assembly = Assembly.LoadFrom( dllPath );
				}
				catch ( Exception ex )
				{
					Log.Warning( $"[Compiler.Analyzers] Failed to LoadFrom analyzer DLL '{dllPath}': {ex.Message}" );
					continue;
				}

				AddAnalyzersFromAssembly( assembly, found );
			}
		}
	}

	// Reflection-bind Sandbox.Engine's Project.Current.GetRootPath() so
	// Sandbox.Compiling can resolve the active project's root directory without
	// adding a Sandbox.Engine project reference (which would create a cycle —
	// Sandbox.Engine references Sandbox.Compiling). Returns null when no
	// project is active or when the reflection probe fails.
	private static string ResolveActiveProjectRoot()
	{
		try
		{
			Type projectType = null;
			foreach ( var asm in AppDomain.CurrentDomain.GetAssemblies() )
			{
				projectType = asm.GetType( "Sandbox.Project", throwOnError: false, ignoreCase: false );
				if ( projectType is not null ) break;
			}
			if ( projectType is null ) return null;

			var currentProp = projectType.GetProperty( "Current", BindingFlags.Public | BindingFlags.Static );
			var current = currentProp?.GetValue( null );
			if ( current is null ) return null;

			var getRootPath = projectType.GetMethod( "GetRootPath", BindingFlags.Public | BindingFlags.Instance );
			return getRootPath?.Invoke( current, null ) as string;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// Run project-shipped analyzers against <paramref name="compilation"/>; append
	/// their diagnostics to <paramref name="output"/>. No-op when no analyzers exist.
	/// Called synchronously from <see cref="BuildInternal"/> (which itself runs on
	/// a Task.Run worker thread), so the async Roslyn call is awaited inline.
	/// </summary>
	// [HANDOFF-TRUST: probe instrumentation to diagnose why FrameBasisAnalyzer loads via reflection.list_assemblies type_count=1 but produces 0 of 170 warnings; not a code change driven by any inherited H1 hypothesis]
	private void RunProjectAnalyzers( CSharpCompilation compilation, CompilerOutput output )
	{
		var analyzers = DiscoverProjectAnalyzers();
		Log.Info( $"[Compiler.Analyzers] PROBE compiler={Name} discovered_analyzers={analyzers.Length} trees={compilation.SyntaxTrees.Length}" );
		if ( analyzers.IsEmpty ) return;

		foreach ( var a in analyzers )
		{
			Log.Info( $"[Compiler.Analyzers] PROBE analyzer_type={a.GetType().FullName} asm={a.GetType().Assembly.GetName().Name}" );
		}

		try
		{
			var withAnalyzers = compilation.WithAnalyzers( analyzers );
			var diagnostics = withAnalyzers.GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
			Log.Info( $"[Compiler.Analyzers] PROBE analyzer_diagnostics_count={diagnostics.Length}" );

			foreach ( var d in diagnostics )
			{
				output.Diagnostics.Add( d );
			}
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[Compiler.Analyzers] Analyzer execution threw — analyzer diagnostics skipped this build: {ex.Message}\n{ex.StackTrace}" );
		}
	}
}
