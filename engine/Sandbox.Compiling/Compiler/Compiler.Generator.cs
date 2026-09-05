using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Sandbox;

partial class Compiler
{
	private Generator.Processor RunGenerators( CSharpCompilation compiler, List<SyntaxTree> syntaxTrees, CompilerOutput output )
	{
		var processor = new Generator.Processor()
		{
			AddonName = Name,
			AddonFileMap = output.Archive.FileMap,
			EnableCorelibPolyfills = _config.Whitelist
		};

		if ( Group.AllowFastHotload && incrementalState.HasState )
		{
			processor.Run( compiler, syntaxTrees, incrementalState.Compilation );
		}
		else
		{
			processor.Run( compiler, syntaxTrees );
		}

		output.Diagnostics.AddRange( processor.Diagnostics );
		output.PackageAssetDependencies = processor.PackageAssetDependencies.ToDictionary( x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase );

		// Error within code generation itself
		if ( processor.Exception != null )
		{
			output.AssemblyCachePublicationAllowed = false;
			Log.Error( processor.Exception, "Error when generating code" );

			Sentry.SentrySdk.CaptureException( processor.Exception, scope =>
			{
				scope.SetTag( "group", "generator" );
			} );
		}

		return processor;
	}
}
