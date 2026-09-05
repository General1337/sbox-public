using Microsoft.CodeAnalysis;
using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;

namespace Sandbox;

/// <summary>
/// A Roslyn metadata reference paired with the digest of the exact PE image used to create it.
/// </summary>
public sealed class CompileReference
{
	public PortableExecutableReference Metadata { get; }
	public string Sha256 { get; }

	private CompileReference( PortableExecutableReference metadata, string sha256 )
	{
		Metadata = metadata;
		Sha256 = sha256;
	}

	public static CompileReference FromBytes( byte[] bytes, string filePath = null )
	{
		ArgumentNullException.ThrowIfNull( bytes );
		var image = ImmutableArray.CreateRange( bytes );
		return new CompileReference(
			MetadataReference.CreateFromImage( image, default, default, filePath ),
			Convert.ToHexString( SHA256.HashData( image.AsSpan() ) ).ToLowerInvariant() );
	}

	public static CompileReference FromFile( string path ) => FromBytes( File.ReadAllBytes( path ), path );
}
