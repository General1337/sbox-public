using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using System.Collections.Immutable;
using System.IO;
using System.Threading;

namespace Sandbox;

partial class Compiler
{
	private static readonly DiagnosticDescriptor WhitelistRule = new DiagnosticDescriptor(
		id: "SB1000",
		title: "Whitelist Error",
		messageFormat: "'{0}' is not allowed when whitelist is enabled",
		helpLinkUri: "https://sbox.game/dev/doc/code/code-basics/api-whitelist/",
		category: "Usage",
		defaultSeverity: DiagnosticSeverity.Error,
		isEnabledByDefault: true );

	CodeArchive _currentArchive;

	/// <summary>
	/// Task completed at the end of <see cref="BuildAsync"/>, for other compilers to await if
	/// they reference this one.
	/// </summary>
	private TaskCompletionSource<CompilerOutput> _compileTcs;

	/// <summary>
	/// Fill this compiler from a code archive
	/// </summary>
	public void UpdateFromArchive( CodeArchive a )
	{
		_currentArchive = a;

		CopyReferencesFromArchive( a );
		MarkForRecompile();
	}

	/// <summary>
	/// Fill CompilerOutput from a precompiled assembly, as if it had been built by this compiler.
	/// </summary>
	internal void UpdateFromAssembly( byte[] bytes )
	{
		var compileReference = CompileReference.FromBytes( bytes );
		MetadataReference = compileReference.Metadata;

		var version = Interlocked.Increment( ref compileCounter );
		Output = new CompilerOutput( this )
		{
			Successful = true,
			Version = Version.Parse( $"0.0.{version}.0" ),
			MetadataReference = MetadataReference,
			CompileReference = compileReference
		};
	}

	/// <summary>
	/// Called by <see cref="CompileGroup"/> before a build starts. Prepares this compiler
	/// to be referenced by other compilers before they build with <see cref="BuildAsync"/>.
	/// </summary>
	internal void PreBuild()
	{
		Assert.False( IsBuilding, "This compiler is already building" );

		// We set up the TCS here so other compilers can use it when they BuildReferencesAsync(),
		// avoiding a race condition if we set it up at the start of BuildAsync()

		_compileTcs = new TaskCompletionSource<CompilerOutput>();
	}

	/// <summary>
	/// Build and load the assembly.
	/// </summary>
	internal async Task BuildAsync()
	{
		Assert.True( IsBuilding, $"{nameof( PreBuild )} must be called first" );

		log.Trace( "Build Start" );
		using var totalTrace = CompileTrace.Begin( "compiler.total", Name, Group.Name );

		var output = new CompilerOutput( this );
		output.AssemblyCachePublicationAllowed = !Group.AllowFastHotload || !incrementalState.HasState;

		Interlocked.Increment( ref compileCounter );

		output.Version = Version.Parse( $"0.0.{compileCounter}.0" );

		try
		{
			// Do the expensive archive building on a worker thread

			CodeArchive archive;
			using ( var stage = CompileTrace.Begin( "compiler.archive", Name, Group.Name ) )
			{
				archive = await Task.Run( () => BuildArchive( output ) );
				stage.Complete( "success", $"syntaxTrees={archive.SyntaxTrees.Count};additionalFiles={archive.AdditionalFiles.Count}" );
			}

			// Build a list of references, waiting for other compilers to finish if needed

			IReadOnlyList<CompileReference> refs;
			using ( var stage = CompileTrace.Begin( "compiler.references", Name, Group.Name ) )
			{
				refs = await BuildReferencesAsync( archive );
				stage.Complete( "success", $"resolved={refs.Count};declared={archive.References.Count}" );
			}

			if ( AssemblyCacheSettings is { Mode: not CompilerAssemblyCacheMode.Off } cacheSettings )
			{
				using var stage = CompileTrace.Begin( "compiler.cache_lookup", Name, Group.Name, cacheMode: cacheSettings.Mode.ToString().ToLowerInvariant() );
				output.AssemblyCacheKey = await Task.Run( () => CompilerAssemblyCache.CreateLookupKey( this, archive, refs ) );
				var lookup = cacheSettings.Mode == CompilerAssemblyCacheMode.Miss
					? (Hit: false, Generation: (CachedCompilerGeneration)null, Decision: "forced-miss")
					: await Task.Run( () =>
					{
						var hit = CompilerAssemblyCache.TryRead( this, output.AssemblyCacheKey, out var generation, out var readDecision );
						return (Hit: hit, Generation: generation, Decision: readDecision);
					} );
				var cached = lookup.Generation;
				var decision = lookup.Decision;
				var cacheHit = lookup.Hit;
				if ( cacheHit )
				{
					var validationOutput = new CompilerOutput( this ) { Version = output.Version };
					var currentArchive = await Task.Run( () => BuildArchive( validationOutput ) );
					var currentRefs = await BuildReferencesAsync( currentArchive );
					var currentKey = await Task.Run( () => CompilerAssemblyCache.CreateLookupKey( this, currentArchive, currentRefs ) );
					if ( currentKey == output.AssemblyCacheKey && TryHydrateAssemblyCache( output, cached ) )
					{
						stage.Complete( "hit", $"key={output.AssemblyCacheKey}" );
						return;
					}
					decision = "miss-revalidation";
				}
				else if ( cacheSettings.Mode == CompilerAssemblyCacheMode.Miss ) decision = "forced-miss";
				stage.Complete( "miss", $"decision={decision};key={output.AssemblyCacheKey}" );
			}

			// Actually compile, again on a worker thread since it's expensive

			using ( var stage = CompileTrace.Begin( "compiler.build_internal", Name, Group.Name ) )
			{
				await Task.Run( () => BuildInternal( refs.Select( x => x.Metadata ).ToArray(), output ) );
				stage.Complete( output.Successful ? "success" : "failed", $"diagnostics={output.Diagnostics.Count}" );
			}
		}
		catch ( System.Exception e )
		{
			output.Exception = e;
			totalTrace.Complete( "exception", $"{e.GetType().Name}: {e.Message}" );
			log.Warning( e, e.Message );
		}
		finally
		{
			if ( output.Exception is null )
				totalTrace.Complete( output.Successful ? "success" : "failed", $"diagnostics={output.Diagnostics.Count}" );

			Output = output;

			_compileTcs.SetResult( output );

			log.Trace( "Build Finished" );
		}
	}

	internal bool TryHydrateAssemblyCache( CompilerOutput output, CachedCompilerGeneration cached )
	{
		try
		{
			if ( _config.Whitelist )
			{
				if ( Group.AccessControl is null ) return false;
				using var input = new MemoryStream( cached.AssemblyData, false );
				var result = Group.AccessControl.VerifyAssembly( input, out TrustedBinaryStream trusted );
				trusted?.Dispose();
				if ( !result.Success ) return false;
			}

			var compileReference = CompileReference.FromBytes( cached.AssemblyData );
			MetadataReference = output.MetadataReference = compileReference.Metadata;
			output.CompileReference = compileReference;
			output.Successful = true;
			output.AssemblyData = cached.AssemblyData;
			output.Archive = cached.Archive;
			output.XmlDocumentation = cached.XmlDocumentation;
			output.PackageAssetDependencies = cached.PackageAssets;
			output.LoadedFromAssemblyCache = true;
			_recentMetadataReferences.Clear();
			_recentMetadataReferences[output.Version] = output.MetadataReference;
			return true;
		}
		catch { return false; }
	}

	void CopyReferencesFromArchive( CodeArchive a )
	{
		_references.Clear();

		foreach ( var reference in a.References )
		{
			_references.Add( reference );
		}
	}

	CodeArchive BuildArchive( CompilerOutput output )
	{
		if ( _currentArchive is not null )
		{
			output.Archive = _currentArchive;
			return _currentArchive;
		}

		var archive = new CodeArchive();
		archive.CompilerName = Name;
		archive.Configuration = _config;
		output.Archive = archive;

		var parseOptions = _config.GetParseOptions();

		//
		// References
		//
		foreach ( var e in _references )
		{
			archive.References.Add( e );
		}

		//
		// Syntax trees
		//
		GetSyntaxTree( archive, parseOptions );

		if ( GetGeneratedCode( output.Version, parseOptions ) is SyntaxTree generated )
		{
			archive.SyntaxTrees.Add( generated );
		}

		archive.SyntaxTrees.Sort( ( a, b ) => string.CompareOrdinal( a.FilePath, b.FilePath ) );

		return archive;
	}

	void BuildInternal( IReadOnlyList<PortableExecutableReference> refs, CompilerOutput output )
	{
		var archive = output.Archive;
		var releaseMode = archive.Configuration.ReleaseMode == ReleaseMode.Release;
		var conf = archive.Configuration;

		var options = incrementalState.Compilation?.Options;
		options ??= new CSharpCompilationOptions( OutputKind.DynamicallyLinkedLibrary )
							.WithConcurrentBuild( true )
							.WithGeneralDiagnosticOption( ReportDiagnostic.Info )
							.WithPlatform( Microsoft.CodeAnalysis.Platform.AnyCpu );

		options = options
					.WithDeterministic( releaseMode ? true : false )
					.WithOptimizationLevel( releaseMode ? OptimizationLevel.Release : OptimizationLevel.Debug )
					.WithGeneralDiagnosticOption( conf.TreatWarningsAsErrors ? ReportDiagnostic.Error : ReportDiagnostic.Default )
					.WithSpecificDiagnosticOptions( conf.GetReportDiagnostics() )
					.WithNullableContextOptions( conf.Nullables ? NullableContextOptions.Enable : NullableContextOptions.Disable )
					.WithAllowUnsafe( conf.Unsafe );

		CSharpCompilation compiler;

		List<SyntaxTree> inputSyntaxTrees;
		using ( var stage = CompileTrace.Begin( "compiler.prepare", Name, Group.Name ) )
		{
			inputSyntaxTrees =
			[
				.. archive.SyntaxTrees, // from source files
				.. ProcessRazorFiles(archive, output) // processed razor files
			];
			stage.Complete( "success", $"syntaxTrees={inputSyntaxTrees.Count};references={refs.Count}" );
		}

		List<SyntaxTree> modifiedSyntaxTrees;
		if ( incrementalState.HasState )
		{
			compiler = incrementalState.Compilation
							.WithAssemblyName( AssemblyName )
							.WithOptions( options );

			var oldRefs = compiler.References.ToHashSet();

			if ( !oldRefs.SetEquals( refs ) )
			{
				compiler = compiler.WithReferences( refs );
			}

			compiler = ReplaceSyntaxTrees( compiler, inputSyntaxTrees, out modifiedSyntaxTrees );
		}
		else
		{
			compiler = CSharpCompilation.Create( AssemblyName, inputSyntaxTrees, refs, options );
			modifiedSyntaxTrees = compiler.SyntaxTrees.ToList();
		}

		bool ilHotloadSupported;

		using ( var stage = CompileTrace.Begin( "compiler.generators", Name, Group.Name ) )
		{
			var processor = RunGenerators( compiler, modifiedSyntaxTrees, output );

			compiler = processor.Compilation;

			ilHotloadSupported = processor.ILHotloadSupported;

			// If you have any errors in codegen don't bother compiling, developer should sort it out
			if ( processor.Diagnostics.Any( x => x.Severity == DiagnosticSeverity.Error ) )
			{
				stage.Complete( "failed", $"diagnostics={processor.Diagnostics.Count()}" );
				return;
			}

			stage.Complete( "success", $"diagnostics={processor.Diagnostics.Count()}" );
		}

		// check for blacklisted methods/types used in compilation
		// we need this because the c# compiler will post optimize and use tons of blacklisted methods
		// run this after generators because they can contain user inputs too
		if ( _config.Whitelist )
		{
			using var stage = CompileTrace.Begin( "compiler.whitelist", Name, Group.Name );
			RunBlacklistWalker( compiler, modifiedSyntaxTrees, output );

			// Errors, fail
			var whitelistErrors = output.Diagnostics.Count( x => x.Severity == DiagnosticSeverity.Error );
			if ( whitelistErrors > 0 )
			{
				stage.Complete( "failed", $"errors={whitelistErrors}" );
				return;
			}

			stage.Complete( "success", "errors=0" );
		}
		else
		{
			CompileTrace.Emit( "compiler.whitelist", 0, "skipped", Name, Group.Name, detail: "disabled" );
		}

		using ( var xmlStream = new System.IO.MemoryStream() )
		using ( var peStream = new System.IO.MemoryStream() )
		{
			var emitOptions = new EmitOptions()
				.WithDebugInformationFormat( DebugInformationFormat.Embedded );

			using ( var stage = CompileTrace.Begin( "compiler.emit", Name, Group.Name ) )
			{
				BuildResult = compiler.Emit( peStream: peStream, xmlDocumentationStream: xmlStream, options: emitOptions );
				stage.Complete( BuildResult.Success ? "success" : "failed", $"diagnostics={BuildResult.Diagnostics.Length}" );
			}

			if ( BuildResult.Success )
			{
				output.Successful = true;

				peStream.Seek( 0, System.IO.SeekOrigin.Begin );

				if ( _config.Whitelist && Group.AccessControl is { } access )
				{
					using var stage = CompileTrace.Begin( "compiler.access_verify", Name, Group.Name );
					var result = access.VerifyAssembly( peStream, out TrustedBinaryStream stream );
					if ( !result.Success )
					{
						stage.Complete( "failed", $"violations={result.WhitelistErrors.Count}" );
						log.Error( "Whitelist violation(s), build unsuccessful." );

						output.Successful = false;

						foreach ( var error in result.WhitelistErrors )
						{
							foreach ( var location in error.Locations )
							{
								output.Diagnostics.Add( Diagnostic.Create( WhitelistRule, location.RoslynLocation ?? Location.None, error.Name ) );
							}
						}
					}
					else
					{
						stage.Complete( "success", "violations=0" );
					}
					stream?.Dispose();
				}
				else
				{
					CompileTrace.Emit( "compiler.access_verify", 0, "skipped", Name, Group.Name, detail: "not-required" );
				}
			}
			else
			{
				CompileTrace.Emit( "compiler.access_verify", 0, "skipped", Name, Group.Name, detail: "emit-failed" );
			}

			output.AssemblyData = peStream.ToArray();
			output.XmlDocumentation = System.Text.Encoding.UTF8.GetString( xmlStream.ToArray() );
		}

		output.Diagnostics.AddRange( BuildResult.Diagnostics );

		if ( !BuildResult.Success )
		{
			return;
		}

		using ( var stage = CompileTrace.Begin( "compiler.incremental_state", Name, Group.Name ) )
		{
			incrementalState.Update( archive, inputSyntaxTrees, compiler );
			stage.Complete( "success" );
		}

		using ( var stage = CompileTrace.Begin( "compiler.metadata_reference", Name, Group.Name ) )
		{
			var compileReference = CompileReference.FromBytes( output.AssemblyData );
			MetadataReference = output.MetadataReference = compileReference.Metadata;
			output.CompileReference = compileReference;

			if ( MetadataReference == null )
				throw new System.Exception( "metaRef is null!" );

			stage.Complete( "success", $"assemblyBytes={output.AssemblyData.Length}" );
		}

		if ( !ilHotloadSupported )
		{
			_recentMetadataReferences.Clear();
		}

		_recentMetadataReferences.Add( output.Version, MetadataReference );
	}
}
