namespace Sandbox.Clutter;

/// <summary>
/// Represents a single clutter instance to be spawned.
/// </summary>
public struct ClutterInstance
{
	public Transform Transform { get; set; }
	public ClutterEntry Entry { get; set; }
	public readonly bool IsModel => Entry is { Model: not null, Prefab: null };
}

/// <summary>
/// Base class to override if you want to create custom scatterer logic.
/// Provides utility methods for entry selection and common operations.
/// </summary>
[Expose]
public abstract class Scatterer
{
	[Hide]
	protected Random Random { get; private set; }

	/// <summary>
	/// Generates clutter instances for the given bounds.
	/// The Random property is initialized before this is called.
	/// </summary>
	/// <param name="bounds">World-space bounds to scatter within</param>
	/// <param name="clutter">The clutter containing objects to scatter</param>
	/// <param name="scene">Scene to use for tracing (null falls back to Game.ActiveScene)</param>
	/// <returns>Collection of clutter instances to spawn</returns>
	protected abstract List<ClutterInstance> Generate( BBox bounds, ClutterDefinition clutter, Scene scene = null );

	/// <summary>
	/// Public entry point for scattering. Creates Random from seed and calls Generate().
	/// </summary>
	/// <param name="bounds">World-space bounds to scatter within</param>
	/// <param name="clutter">The clutter containing objects to scatter</param>
	/// <param name="seed">Seed for deterministic random generation</param>
	/// <param name="scene">Scene to use for tracing (required in editor mode)</param>
	/// <returns>Collection of clutter instances to spawn</returns>
	public List<ClutterInstance> Scatter( BBox bounds, ClutterDefinition clutter, int seed, Scene scene = null )
	{
		Random = new Random( seed );

		return Generate( bounds, clutter, scene );
	}

	/// <summary>
	/// Generates a hash from all serializable fields and properties using TypeLibrary.
	/// Override this if you need custom hash generation logic.
	/// </summary>
	public override int GetHashCode()
	{
		HashCode hash = new();
		var typeDesc = Game.TypeLibrary.GetType( GetType() );

		if ( typeDesc == null )
			return base.GetHashCode();

		hash.Add( GetType().Name );

		foreach ( var property in typeDesc.Properties )
		{
			if ( !property.HasAttribute<PropertyAttribute>() )
				continue;

			var value = property.GetValue( this );
			HashValue( ref hash, value );
		}

		return hash.ToHashCode();
	}

	private static void HashValue( ref HashCode hash, object value )
	{
		if ( value == null )
		{
			hash.Add( 0 );
			return;
		}

		if ( value is System.Collections.IEnumerable enumerable && value is not string )
		{
			foreach ( var item in enumerable )
			{
				HashValue( ref hash, item );
			}
			return;
		}

		hash.Add( value.GetHashCode() );
	}

	/// <summary>
	/// Selects a random entry from the clutter based on weights.
	/// Returns null if no valid entries exist.
	/// </summary>
	protected ClutterEntry GetRandomEntry( ClutterDefinition clutter )
	{
		return GetRandomEntry( clutter, Random );
	}

	/// <summary>
	/// Selects a random entry from the clutter using the supplied deterministic random stream.
	/// </summary>
	protected ClutterEntry GetRandomEntry( ClutterDefinition clutter, Random random )
	{
		if ( clutter.IsEmpty )
			return null;

		random ??= Random;
		if ( random is null )
			return null;

		var totalWeight = 0f;
		foreach ( var entry in clutter.Entries )
		{
			if ( entry?.HasAsset is true && entry.Weight > 0 )
				totalWeight += entry.Weight;
		}

		if ( totalWeight is 0 ) return null;

		var randomValue = random.Float( 0f, totalWeight );
		var currentWeight = 0f;

		foreach ( var entry in clutter.Entries )
		{
			if ( entry?.HasAsset is not true || entry.Weight <= 0 )
				continue;

			currentWeight += entry.Weight;
			if ( randomValue <= currentWeight )
				return entry;
		}

		return null;
	}

	/// <summary>
	/// Creates a rotation aligned to a surface normal with random yaw.
	/// </summary>
	protected static Rotation GetAlignedRotation( Vector3 normal, float yawDegrees )
	{
		var alignToSurface = Rotation.FromToRotation( Vector3.Up, normal );
		var yawRotation = Rotation.FromAxis( normal, yawDegrees );
		return yawRotation * alignToSurface;
	}

	/// <summary>
	/// Helper to perform a ground trace at a position.
	/// <para>
	/// Prefer the (scene, position, zMin, zMax) overload from a scatterer's inner loop:
	/// <c>scene.GetBounds()</c> walks every <c>IHasBounds</c> component in the scene, so calling it
	/// per point is pathological in solar-system-scale scenes (ClutterGridSystem.ProcessJobs
	/// invokes this thousands of times per streaming tile). Hoist the trace Z range once per
	/// generation job / streaming work item and pass it in.
	/// </para>
	/// </summary>
	protected static SceneTraceResult TraceGround( Scene scene, Vector3 position )
	{
		// Use scene bounds to determine trace extent
		var sceneBounds = scene.GetBounds();
		return TraceGround( scene, position, sceneBounds.Mins.z, sceneBounds.Maxs.z );
	}

	/// <summary>
	/// Performs a downward trace at <paramref name="position"/> using a caller-supplied Z envelope.
	/// Callers should resolve <paramref name="zMin"/> / <paramref name="zMax"/> once per generation
	/// job (tile bounds already carry a bounded Z envelope of ±TileHeight = ±50000u) and reuse
	/// them across every point — the per-point <c>scene.GetBounds()</c> path this replaces was the
	/// dominant fill-time cost at high densities.
	/// </summary>
	protected static SceneTraceResult TraceGround( Scene scene, Vector3 position, float zMin, float zMax )
	{
		var traceStart = position.WithZ( zMax );
		var traceEnd = position.WithZ( zMin );

		ClutterGridSystem.s_pointsTraced++;

		return scene.Trace
			.Ray( traceStart, traceEnd )
			.WithoutTags( "player", "trigger", "clutter" )
			.Run();
	}

	/// <summary>
	/// Resolves a job-scoped trace Z envelope from the caller's <paramref name="bounds"/>, falling
	/// back to <c>scene.GetBounds()</c> only when <paramref name="bounds"/> has a degenerate Z
	/// range. Call this ONCE at the start of a scatter job and thread the returned min/max through
	/// the per-point trace calls — that's what removes the O(points) <c>scene.GetBounds()</c> walk.
	/// </summary>
	protected static (float ZMin, float ZMax) ResolveTraceZRange( Scene scene, BBox bounds )
	{
		var zMin = bounds.Mins.z;
		var zMax = bounds.Maxs.z;

		if ( zMax - zMin > 1.0f )
			return (zMin, zMax);

		var sceneBounds = scene.GetBounds();
		return (sceneBounds.Mins.z, sceneBounds.Maxs.z);
	}

	/// <summary>
	/// Generates a deterministic seed from tile coordinates and base seed.
	/// Use this to create unique seeds for different tiles.
	/// </summary>
	public static int GenerateSeed( int baseSeed, int x, int y )
	{
		int seed = baseSeed;
		seed = (seed * 397) ^ x;
		seed = (seed * 397) ^ y;
		return seed;
	}

	/// <summary>
	/// Calculates the number of points to scatter based on density and area.
	/// Caps at maxPoints to prevent engine freezing.
	/// </summary>
	/// <param name="bounds">Bounds to scatter in</param>
	/// <param name="density">Points per square meter</param>
	/// <param name="maxPoints">Maximum points to cap at (default 10000)</param>
	/// <returns>Number of points to generate</returns>
	protected int CalculatePointCount( BBox bounds, float density, int maxPoints = 10000 )
	{
		return CalculatePointCount( bounds, density, Random, maxPoints );
	}

	/// <summary>
	/// Calculates the number of points to scatter using the supplied deterministic random stream.
	/// </summary>
	protected int CalculatePointCount( BBox bounds, float density, Random random, int maxPoints = 10000 )
	{
		// Convert bounds from engine units (inches) to meters
		// 1 inch = 0.0254 meters
		var widthMeters = bounds.Size.x.InchToMeter();
		var depthMeters = bounds.Size.y.InchToMeter();
		var areaSquareMeters = widthMeters * depthMeters;

		var desiredCount = areaSquareMeters * density / 10f;

		// Handle fractional points probabilistically
		// 1.3 points = 1 guaranteed + 30% chance of 1 more
		var guaranteedPoints = (int)desiredCount;
		var fractionalPart = desiredCount - guaranteedPoints;

		var finalCount = guaranteedPoints;
		random ??= Random;
		if ( random is not null && random.Float( 0f, 1f ) < fractionalPart )
		{
			finalCount++;
		}

		var clampedCount = Math.Clamp( finalCount, 0, maxPoints );

		if ( desiredCount > maxPoints )
		{
			Log.Warning( $"Scatterer: Density would generate {desiredCount:F0} points, capped to {maxPoints} to prevent freezing." );
		}

		return clampedCount;
	}
}
