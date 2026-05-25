using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Reflection;

namespace Sandbox;

// FORK-PATCH (Sandbox++ — game-side analyzer support) — Game-side Roslyn analyzer support.
//
// Adds project-level Roslyn analyzer discovery + execution to Compiler.BuildInternal.
// Before this patch, Roslyn analyzers shipped inside editor libraries (e.g.,
// Libraries/<name>/Editor/Analyzers/) fired only in the IDE (VS / Rider) because
// MSBuild loaded them from the .csproj; the in-engine compiler ignored them entirely.
//
// Discovery convention: any concrete class deriving from
// Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer, decorated with
// [DiagnosticAnalyzer(LanguageNames.CSharp)], living in any loaded assembly whose
// AssemblyName matches "package.*.editor" is instantiated and executed against
// every Compiler.BuildInternal compilation. Analyzers are run AFTER source generators
// but BEFORE the SB1000 whitelist walker, so the whitelist still has the final say
// on what compiles successfully.
//
// Anchor case (the use case that motivated the patch): the Sandbox++ FrameBasisAnalyzer
// (Libraries/arenula_mcp/Editor/Analyzers/FrameBasisAnalyzer/FrameBasisAnalyzer.cs)
// flags untyped Vector3 declarations + cross-frame arithmetic in gravity-hull code.
// Without this fork-patch, the analyzer's enforcement was IDE-only — agents that
// skipped IDE-level checks could silently ship typed-bypass regressions. With this
// patch the analyzer fires on every in-engine compile, regardless of IDE state.
//
// Cost: one AppDomain.GetAssemblies() iteration per compile (cheap), plus the
// analyzer's own work (proportional to its scanned-syntax-node count). The
// FrameBasisAnalyzer adds <1ms per typical compile.
//
// Safety: analyzer instances run with full CLR trust (same as any editor library
// code). They observe but cannot mutate compilation. Whitelist walker runs AFTER
// analyzers, so an analyzer cannot smuggle in blacklisted API usage.
partial class Compiler
{
	/// <summary>
	/// Scan the current AppDomain for project-shipped Roslyn analyzers.
	/// Returns analyzer instances declared in any loaded <c>package.*.editor</c>
	/// assembly that derives from <see cref="DiagnosticAnalyzer"/> and is decorated
	/// with <c>[DiagnosticAnalyzer]</c>. Empty when no game-side analyzers exist
	/// (the common case — preserves zero-overhead for projects that ship none).
	/// </summary>
	private static ImmutableArray<DiagnosticAnalyzer> DiscoverProjectAnalyzers()
	{
		var found = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();

		foreach ( var assembly in AppDomain.CurrentDomain.GetAssemblies() )
		{
			var name = assembly.GetName().Name;
			if ( string.IsNullOrEmpty( name ) ) continue;
			if ( !name.StartsWith( "package.", StringComparison.Ordinal ) ) continue;
			if ( !name.EndsWith( ".editor", StringComparison.Ordinal ) ) continue;

			Type[] types;
			try { types = assembly.GetTypes(); }
			catch ( ReflectionTypeLoadException ex )
			{
				types = ex.Types.Where( t => t is not null ).ToArray()!;
			}
			catch ( Exception )
			{
				continue;
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

		return found.ToImmutable();
	}

	/// <summary>
	/// Run project-shipped analyzers against <paramref name="compilation"/>; append
	/// their diagnostics to <paramref name="output"/>. No-op when no analyzers exist.
	/// Called synchronously from <see cref="BuildInternal"/> (which itself runs on
	/// a Task.Run worker thread), so the async Roslyn call is awaited inline.
	/// </summary>
	private void RunProjectAnalyzers( CSharpCompilation compilation, CompilerOutput output )
	{
		var analyzers = DiscoverProjectAnalyzers();
		if ( analyzers.IsEmpty ) return;

		try
		{
			var withAnalyzers = compilation.WithAnalyzers( analyzers );
			var diagnostics = withAnalyzers.GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();

			foreach ( var d in diagnostics )
			{
				output.Diagnostics.Add( d );
			}
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[Compiler.Analyzers] Analyzer execution threw — analyzer diagnostics skipped this build: {ex.Message}" );
		}
	}
}
