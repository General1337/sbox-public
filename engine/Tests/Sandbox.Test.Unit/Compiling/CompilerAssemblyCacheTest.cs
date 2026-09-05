using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Sandbox;

[TestClass]
[DoNotParallelize]
public class CompilerAssemblyCacheTest
{
	static (CompileGroup Group, Compiler Compiler) CreateCompiler( Compiler.Configuration? configuration = null )
	{
		var group = new CompileGroup( "AssemblyCacheTest" );
		var config = configuration ?? new Compiler.Configuration { Whitelist = false };
		config.Clean();
		return (group, group.CreateCompiler( "cache.test", null, config ));
	}

	static CodeArchive Archive( Compiler.Configuration config, params (string Path, string Text)[] sources )
	{
		var archive = new CodeArchive { CompilerName = "cache.test", Configuration = config };
		foreach ( var reference in new[] { "Sandbox.System", "Sandbox.Engine", "Sandbox.Filesystem", "Sandbox.Reflection", "Sandbox.Mounting", "Microsoft.AspNetCore.Components" } )
			archive.References.Add( reference );
		foreach ( var source in sources )
		{
			archive.SyntaxTrees.Add( CSharpSyntaxTree.ParseText( SourceText.From( source.Text, Encoding.UTF8 ), path: source.Path, options: config.GetParseOptions() ) );
			archive.FileMap[source.Path] = source.Path;
		}
		return archive;
	}

	[TestMethod]
	public void LookupKey_IgnoresVolatileCompilerExtra()
	{
		var created = CreateCompiler();
		using var group = created.Group;
		created.Compiler.GeneratedCode.AppendLine( "global using System;" );
		var config = created.Compiler.GetConfiguration();
		var first = Archive( config, ("Code/A.cs", "class A {}"), (Sandbox.Generator.Processor.CompilerExtraPath, "CompileTime=A;Version=1") );
		var second = Archive( config, ("Code/A.cs", "class A {}"), (Sandbox.Generator.Processor.CompilerExtraPath, "CompileTime=B;Version=2") );

		Assert.AreEqual( CompilerAssemblyCache.CreateLookupKey( created.Compiler, first, [] ), CompilerAssemblyCache.CreateLookupKey( created.Compiler, second, [] ) );
	}

	[TestMethod]
	public void LookupKey_InvalidatesSourceSetPathAndContent()
	{
		var created = CreateCompiler();
		using var group = created.Group;
		var config = created.Compiler.GetConfiguration();
		var baseline = CompilerAssemblyCache.CreateLookupKey( created.Compiler, Archive( config, ("Code/A.cs", "class A {}") ), [] );

		Assert.AreNotEqual( baseline, CompilerAssemblyCache.CreateLookupKey( created.Compiler, Archive( config, ("Code/A.cs", "class B {}") ), [] ), "content" );
		Assert.AreNotEqual( baseline, CompilerAssemblyCache.CreateLookupKey( created.Compiler, Archive( config, ("Code/Renamed.cs", "class A {}") ), [] ), "rename" );
		Assert.AreNotEqual( baseline, CompilerAssemblyCache.CreateLookupKey( created.Compiler, Archive( config, ("Code/A.cs", "class A {}"), ("Code/B.cs", "class B {}") ), [] ), "add" );
		Assert.AreNotEqual( baseline, CompilerAssemblyCache.CreateLookupKey( created.Compiler, Archive( config ), [] ), "delete" );
	}

	[TestMethod]
	public void LookupKey_InvalidatesEveryEffectiveConfigurationFamily()
	{
		var baselineCreated = CreateCompiler();
		using var baselineGroup = baselineCreated.Group;
		baselineCreated.Compiler.GeneratedCode.Append( "generated-a" );
		var baselineConfig = baselineCreated.Compiler.GetConfiguration();
		var baseline = CompilerAssemblyCache.CreateLookupKey( baselineCreated.Compiler, Archive( baselineConfig, ("A.cs", "class A {}") ), [] );

		var variants = new List<Compiler.Configuration>
		{
			baselineConfig with { RootNamespace = "Changed" },
			baselineConfig with { DefineConstants = "SANDBOX;CHANGED" },
			baselineConfig with { NoWarn = "1234" },
			baselineConfig with { WarningsAsErrors = "1234" },
			baselineConfig with { TreatWarningsAsErrors = true },
			baselineConfig with { Nullables = true },
			baselineConfig with { Whitelist = true },
			baselineConfig with { Unsafe = true },
			baselineConfig with { ReleaseMode = Compiler.ReleaseMode.Release },
			baselineConfig with { StripDisabledTextTrivia = true },
			baselineConfig with { AssemblyReferences = ["System.Memory"] },
			baselineConfig with { IgnoreFolders = ["ignored"] },
			baselineConfig with { ReplacementDirectives = new Dictionary<string, string> { [".Other.cs"] = "OTHER" } }
		};

		foreach ( var variant in variants )
		{
			var created = CreateCompiler( variant );
			using var group = created.Group;
			created.Compiler.GeneratedCode.Append( "generated-a" );
			var actual = CompilerAssemblyCache.CreateLookupKey( created.Compiler, Archive( created.Compiler.GetConfiguration(), ("A.cs", "class A {}") ), [] );
			Assert.AreNotEqual( baseline, actual, Json.Serialize( variant ) );
		}

		var generatedCreated = CreateCompiler( baselineConfig );
		using var generatedGroup = generatedCreated.Group;
		generatedCreated.Compiler.GeneratedCode.Append( "generated-b" );
		Assert.AreNotEqual( baseline, CompilerAssemblyCache.CreateLookupKey( generatedCreated.Compiler, Archive( baselineConfig, ("A.cs", "class A {}") ), [] ), "generated code" );
	}

	[TestMethod]
	public void LookupKey_InvalidatesResolvedReferenceImage()
	{
		var created = CreateCompiler();
		using var group = created.Group;
		var archive = Archive( created.Compiler.GetConfiguration(), ("A.cs", "class A {}") );
		var first = CompileReference.FromFile( typeof( object ).Assembly.Location );
		var second = CompileReference.FromFile( typeof( CompilerAssemblyCacheTest ).Assembly.Location );
		Assert.AreNotEqual(
			CompilerAssemblyCache.CreateLookupKey( created.Compiler, archive, [first] ),
			CompilerAssemblyCache.CreateLookupKey( created.Compiler, archive, [second] ) );
		Assert.AreNotEqual(
			CompilerAssemblyCache.CreateLookupKey( created.Compiler, archive, [first, second] ),
			CompilerAssemblyCache.CreateLookupKey( created.Compiler, archive, [second, first] ), "reference order" );
	}

	[TestMethod]
	public void CompileReference_SnapshotsBytesAndPreservesFilePath()
	{
		var path = typeof( CompilerAssemblyCacheTest ).Assembly.Location;
		var bytes = File.ReadAllBytes( path );
		var expected = Convert.ToHexString( System.Security.Cryptography.SHA256.HashData( bytes ) ).ToLowerInvariant();
		var fromBytes = CompileReference.FromBytes( bytes, path );
		bytes[0] ^= 0xff;

		Assert.AreEqual( expected, fromBytes.Sha256 );
		Assert.AreEqual( path, fromBytes.Metadata.FilePath );
		var fromFile = CompileReference.FromFile( path );
		Assert.AreEqual( expected, fromFile.Sha256 );
		Assert.AreEqual( path, fromFile.Metadata.FilePath );
	}

	[TestMethod]
	public void PackageAssetDependencies_AreReResolved()
	{
		var previous = Sandbox.Generator.Processor.DefaultPackageAssetResolver;
		try
		{
			Sandbox.Generator.Processor.DefaultPackageAssetResolver = ident => ident == "org.asset" ? "maps/a.scene" : null;
			var dependencies = new Dictionary<string, string> { ["org.asset"] = "maps/a.scene" };
			Assert.IsTrue( CompilerAssemblyCache.ValidatePackageAssets( dependencies ) );
			Sandbox.Generator.Processor.DefaultPackageAssetResolver = _ => "maps/b.scene";
			Assert.IsFalse( CompilerAssemblyCache.ValidatePackageAssets( dependencies ) );
			Sandbox.Generator.Processor.DefaultPackageAssetResolver = null;
			Assert.IsFalse( CompilerAssemblyCache.ValidatePackageAssets( dependencies ) );
		}
		finally
		{
			Sandbox.Generator.Processor.DefaultPackageAssetResolver = previous;
		}
	}

	[TestMethod]
	public async Task PublishedGeneration_ValidatesArtifactsAndHydratesExactOutput()
	{
		var root = Path.Combine( Path.GetTempPath(), $"sbox-assembly-cache-test-{Guid.NewGuid():N}" );
		try
		{
			var created = CreateCompiler();
			using var group = created.Group;
			created.Compiler.AssemblyCacheSettings = new CompilerAssemblyCacheSettings( root, CompilerAssemblyCacheMode.Read );
			var config = created.Compiler.GetConfiguration();
			var archive = Archive( config, ("A.cs", "public class CacheFixture { public int Value => 42; }") );
			created.Compiler.UpdateFromArchive( archive );
			Assert.IsTrue( await group.BuildAsync() );
			var original = created.Compiler.Output;
			original.AssemblyCacheKey = "integration-key";
			original.AssemblyCachePublicationAllowed = true;
			CompilerAssemblyCache.Publish( created.Compiler, original );

			Assert.IsTrue( CompilerAssemblyCache.TryRead( created.Compiler, original.AssemblyCacheKey, out var cached, out var decision ), decision );
			var hydrated = new CompilerOutput( created.Compiler ) { Version = original.Version };
			Assert.IsTrue( created.Compiler.TryHydrateAssemblyCache( hydrated, cached ) );
			Assert.IsTrue( hydrated.Successful );
			Assert.IsTrue( hydrated.LoadedFromAssemblyCache );
			CollectionAssert.AreEqual( original.AssemblyData, hydrated.AssemblyData );
			Assert.AreEqual( original.Version, hydrated.Version );
			Assert.AreEqual( original.XmlDocumentation, hydrated.XmlDocumentation );
			Assert.AreEqual( original.Archive.CompilerName, hydrated.Archive.CompilerName );
			Assert.IsNotNull( hydrated.MetadataReference );

			var entry = Path.Combine( root, created.Compiler.AssemblyName, original.AssemblyCacheKey );
			foreach ( var file in new[] { "output.dll", "output.cll", "output.xml" } )
			{
				var path = Path.Combine( entry, file );
				var bytes = File.ReadAllBytes( path );
				File.WriteAllBytes( path, [.. bytes, (byte)0x7f] );
				Assert.IsFalse( CompilerAssemblyCache.TryRead( created.Compiler, original.AssemblyCacheKey, out _, out decision ), $"{file}: {decision}" );
				File.WriteAllBytes( path, bytes );
			}

			var manifestPath = Path.Combine( entry, "manifest.json" );
			var manifest = File.ReadAllText( manifestPath );
			File.WriteAllText( manifestPath, "{" );
			Assert.IsFalse( CompilerAssemblyCache.TryRead( created.Compiler, original.AssemblyCacheKey, out _, out decision ), decision );
			File.WriteAllText( manifestPath, manifest );

			File.Move( Path.Combine( entry, "output.cll" ), Path.Combine( entry, "output.cll.missing" ) );
			Assert.IsFalse( CompilerAssemblyCache.TryRead( created.Compiler, original.AssemblyCacheKey, out _, out decision ), decision );
		}
		finally
		{
			if ( Directory.Exists( root ) ) Directory.Delete( root, true );
		}
	}
}
