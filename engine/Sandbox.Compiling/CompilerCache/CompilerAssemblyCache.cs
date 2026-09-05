using Microsoft.CodeAnalysis;
using System.Buffers.Binary;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Sandbox;

internal enum CompilerAssemblyCacheMode
{
	Off,
	Write,
	Read,
	Miss
}

internal sealed record CompilerAssemblyCacheSettings( string Directory, CompilerAssemblyCacheMode Mode )
{
	public bool CanRead => Mode == CompilerAssemblyCacheMode.Read;
	public bool CanWrite => Mode != CompilerAssemblyCacheMode.Off;
	public bool ShadowRead => Mode == CompilerAssemblyCacheMode.Write;
}

internal sealed record CachedCompilerGeneration(
	byte[] AssemblyData,
	CodeArchive Archive,
	byte[] ArchiveData,
	string XmlDocumentation,
	Version Version,
	IReadOnlyDictionary<string, string> PackageAssets );

internal static class CompilerAssemblyCache
{
	internal const int SchemaVersion = 1;
	const string ManifestName = "manifest.json";
	const string AssemblyName = "output.dll";
	const string ArchiveName = "output.cll";
	const string XmlName = "output.xml";

	sealed record Artifact( long Length, string Sha256 );
	sealed record Manifest(
		int Schema,
		string Key,
		string CompilerName,
		string AssemblyName,
		string AssemblyVersion,
		Artifact Dll,
		Artifact Cll,
		Artifact Xml,
		Dictionary<string, string> PackageAssets );

	internal static string CreateLookupKey( Compiler compiler, CodeArchive archive, IReadOnlyList<CompileReference> references )
	{
		using var hash = IncrementalHash.CreateHash( HashAlgorithmName.SHA256 );
		var writer = new CanonicalHashWriter( hash );

		writer.String( "sandbox.compiler-assembly-cache" );
		writer.Int32( SchemaVersion );
		writer.String( compiler.Name );
		writer.String( compiler.AssemblyName );
		writer.Bool( compiler.UseAbsoluteSourcePaths );
		writer.Bool( Sandbox.Generator.Processor.DefaultPackageAssetResolver is not null );

		foreach ( var assembly in new[]
		{
			typeof( Compiler ).Assembly,
			typeof( Sandbox.Generator.Processor ).Assembly,
			typeof( Sandbox.Razor.RazorProcessor ).Assembly,
			typeof( AccessControl ).Assembly,
			typeof( Microsoft.CodeAnalysis.CSharp.CSharpCompilation ).Assembly
		} )
		{
			writer.String( assembly.GetName().Name );
			writer.String( assembly.GetName().Version?.ToString() );
			writer.String( assembly.ManifestModule.ModuleVersionId.ToString( "N" ) );
		}

		WriteConfiguration( writer, compiler.GetConfiguration() );
		writer.String( compiler.GeneratedCode.ToString() );
		writer.String( archive.CompilerName );
		writer.Int64( archive.Version );

		foreach ( var reference in archive.References.OrderBy( x => x, StringComparer.OrdinalIgnoreCase ) )
			writer.String( reference );

		var sourceTrees = archive.SyntaxTrees
			.Where( x => !string.Equals( NormalizePath( x.FilePath ), NormalizePath( Sandbox.Generator.Processor.CompilerExtraPath ), StringComparison.OrdinalIgnoreCase ) )
			.Select( x => (Path: NormalizePath( x.FilePath ), Text: x.GetText().ToString()) )
			.OrderBy( x => x.Path, StringComparer.OrdinalIgnoreCase )
			.ThenBy( x => x.Path, StringComparer.Ordinal );

		foreach ( var source in sourceTrees )
		{
			writer.String( source.Path );
			writer.String( source.Text );
		}

		foreach ( var file in archive.AdditionalFiles
			.OrderBy( x => NormalizePath( x.LocalPath ), StringComparer.OrdinalIgnoreCase )
			.ThenBy( x => NormalizePath( x.LocalPath ), StringComparer.Ordinal ) )
		{
			writer.String( NormalizePath( file.LocalPath ) );
			writer.String( file.Text );
		}

		foreach ( var pair in archive.FileMap
			.OrderBy( x => NormalizePath( x.Key ), StringComparer.OrdinalIgnoreCase )
			.ThenBy( x => NormalizePath( x.Key ), StringComparer.Ordinal ) )
		{
			writer.String( NormalizePath( pair.Key ) );
			writer.String( NormalizePath( pair.Value ) );
		}

		foreach ( var identity in references.Select( ReferenceIdentity ) )
			writer.String( identity );

		return Convert.ToHexString( hash.GetHashAndReset() ).ToLowerInvariant();
	}

	internal static bool TryRead( Compiler compiler, string key, out CachedCompilerGeneration generation, out string decision )
	{
		generation = null;
		decision = "disabled";
		var settings = compiler.AssemblyCacheSettings;
		if ( settings is null || (!settings.CanRead && !settings.ShadowRead) ) return false;

		try
		{
			var directory = EntryDirectory( settings, compiler.AssemblyName, key );
			var manifestPath = Path.Combine( directory, ManifestName );
			if ( !File.Exists( manifestPath ) )
			{
				decision = "miss-not-found";
				return false;
			}

			var manifest = JsonSerializer.Deserialize<Manifest>( File.ReadAllText( manifestPath ) );
			if ( manifest is null || manifest.Schema != SchemaVersion || manifest.Key != key ||
				manifest.CompilerName != compiler.Name || manifest.AssemblyName != compiler.AssemblyName )
			{
				decision = "miss-manifest";
				return false;
			}

			if ( !ValidatePackageAssets( manifest.PackageAssets ) )
			{
				decision = "miss-package-assets";
				return false;
			}

			var dll = ReadArtifact( Path.Combine( directory, AssemblyName ), manifest.Dll );
			var cll = ReadArtifact( Path.Combine( directory, ArchiveName ), manifest.Cll );
			var xmlBytes = ReadArtifact( Path.Combine( directory, XmlName ), manifest.Xml );
			if ( dll is null || cll is null || xmlBytes is null )
			{
				decision = "miss-artifact";
				return false;
			}

			if ( !TryReadAssemblyIdentity( dll, out var assemblyName, out var peVersion ) ||
				assemblyName != compiler.AssemblyName || peVersion.ToString() != manifest.AssemblyVersion )
			{
				decision = "miss-assembly-identity";
				return false;
			}

			var archive = new CodeArchive( cll );
			if ( archive.CompilerName != compiler.Name )
			{
				decision = "miss-archive-identity";
				return false;
			}

			generation = new CachedCompilerGeneration( dll, archive, cll, Encoding.UTF8.GetString( xmlBytes ), null, manifest.PackageAssets );
			decision = settings.CanRead ? "hit" : "shadow-hit";
			return settings.CanRead;
		}
		catch ( Exception ex )
		{
			decision = $"miss-exception-{ex.GetType().Name}";
			return false;
		}
	}

	internal static void Publish( Compiler compiler, CompilerOutput output )
	{
		var settings = compiler.AssemblyCacheSettings;
		if ( settings is null || !settings.CanWrite || output is null || !output.Successful ||
			output.LoadedFromAssemblyCache || string.IsNullOrWhiteSpace( output.AssemblyCacheKey ) ||
			!output.AssemblyCachePublicationAllowed || output.AssemblyData is null || output.Archive is null ) return;
		if ( !TryReadAssemblyIdentity( output.AssemblyData, out var emittedAssemblyName, out var emittedVersion ) || emittedAssemblyName != compiler.AssemblyName ) return;

		var cll = output.Archive.Serialize();
		var xml = Encoding.UTF8.GetBytes( output.XmlDocumentation ?? string.Empty );
		var finalDirectory = EntryDirectory( settings, compiler.AssemblyName, output.AssemblyCacheKey );
		if ( Directory.Exists( finalDirectory ) ) return;

		var parent = Path.GetDirectoryName( finalDirectory );
		Directory.CreateDirectory( parent );
		var temp = Path.Combine( parent, $".{Path.GetFileName( finalDirectory )}.{Guid.NewGuid():N}.tmp" );

		try
		{
			Directory.CreateDirectory( temp );
			File.WriteAllBytes( Path.Combine( temp, AssemblyName ), output.AssemblyData );
			File.WriteAllBytes( Path.Combine( temp, ArchiveName ), cll );
			File.WriteAllBytes( Path.Combine( temp, XmlName ), xml );

			var manifest = new Manifest(
				SchemaVersion,
				output.AssemblyCacheKey,
				compiler.Name,
				compiler.AssemblyName,
				emittedVersion.ToString(),
				Describe( output.AssemblyData ),
				Describe( cll ),
				Describe( xml ),
				new Dictionary<string, string>( output.PackageAssetDependencies, StringComparer.OrdinalIgnoreCase ) );
			File.WriteAllText( Path.Combine( temp, ManifestName ), JsonSerializer.Serialize( manifest ) );

			try { Directory.Move( temp, finalDirectory ); }
			catch ( IOException ) when ( Directory.Exists( finalDirectory ) ) { }
		}
		finally
		{
			if ( Directory.Exists( temp ) ) Directory.Delete( temp, true );
		}
	}

	internal static bool ValidatePackageAssets( IReadOnlyDictionary<string, string> dependencies )
	{
		if ( dependencies is null || dependencies.Count == 0 ) return true;
		var resolver = Sandbox.Generator.Processor.DefaultPackageAssetResolver;
		if ( resolver is null ) return false;
		foreach ( var pair in dependencies )
		{
			if ( !string.Equals( resolver( pair.Key ), pair.Value, StringComparison.Ordinal ) ) return false;
		}
		return true;
	}

	static void WriteConfiguration( CanonicalHashWriter writer, Compiler.Configuration config )
	{
		writer.String( config.RootNamespace );
		writer.String( config.DefineConstants );
		writer.String( config.NoWarn );
		writer.String( config.WarningsAsErrors );
		writer.Bool( config.TreatWarningsAsErrors );
		writer.Bool( config.Nullables );
		writer.Bool( config.Whitelist );
		writer.Bool( config.Unsafe );
		writer.Int32( (int)config.ReleaseMode );
		writer.Bool( config.StripDisabledTextTrivia );
		foreach ( var value in config.AssemblyReferences ?? [] ) writer.String( value );
		foreach ( var value in (config.IgnoreFolders ?? []).OrderBy( x => x, StringComparer.OrdinalIgnoreCase ) ) writer.String( value );
		foreach ( var pair in config.ReplacementDirectives ?? [] )
		{
			writer.String( pair.Key );
			writer.String( pair.Value );
		}
		foreach ( var symbol in config.GetPreprocessorSymbols().OrderBy( x => x, StringComparer.Ordinal ) ) writer.String( symbol );
	}

	static string ReferenceIdentity( CompileReference resolved )
	{
		var reference = resolved.Metadata;
		return $"pe:{resolved.Sha256}:{reference.Properties.Kind}:{reference.Properties.EmbedInteropTypes}:{string.Join( ",", reference.Properties.Aliases )}:{reference.FilePath}:{reference.Display}";
	}

	static byte[] ReadArtifact( string path, Artifact expected )
	{
		if ( expected is null || !File.Exists( path ) ) return null;
		var bytes = File.ReadAllBytes( path );
		return bytes.LongLength == expected.Length && Sha256( bytes ) == expected.Sha256 ? bytes : null;
	}

	static Artifact Describe( byte[] bytes ) => new( bytes.LongLength, Sha256( bytes ) );
	static string Sha256( byte[] bytes ) => Convert.ToHexString( SHA256.HashData( bytes ) ).ToLowerInvariant();
	static string NormalizePath( string path ) => (path ?? string.Empty).Replace( '\\', '/' );
	static string EntryDirectory( CompilerAssemblyCacheSettings settings, string assemblyName, string key ) => Path.Combine( settings.Directory, Sanitize( assemblyName ), key );
	static string Sanitize( string value ) => string.Concat( value.Select( c => char.IsLetterOrDigit( c ) || c is '.' or '-' or '_' ? c : '_' ) );

	static bool TryReadAssemblyIdentity( byte[] bytes, out string name, out Version version )
	{
		name = null;
		version = null;
		try
		{
			using var stream = new MemoryStream( bytes, false );
			using var pe = new PEReader( stream );
			var reader = pe.GetMetadataReader();
			var definition = reader.GetAssemblyDefinition();
			name = reader.GetString( definition.Name );
			version = definition.Version;
			return true;
		}
		catch { return false; }
	}

	sealed class CanonicalHashWriter
	{
		readonly IncrementalHash _hash;
		public CanonicalHashWriter( IncrementalHash hash ) => _hash = hash;
		public void Bool( bool value ) => Int32( value ? 1 : 0 );
		public void Int32( int value ) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian( bytes, value ); _hash.AppendData( bytes ); }
		public void Int64( long value ) { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian( bytes, value ); _hash.AppendData( bytes ); }
		public void String( string value ) { var bytes = Encoding.UTF8.GetBytes( value ?? string.Empty ); Int32( bytes.Length ); _hash.AppendData( bytes ); }
	}
}
