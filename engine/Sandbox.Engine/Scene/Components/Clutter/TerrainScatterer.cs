using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Sandbox.Clutter;

/// <summary>
/// Maps an clutter entry to a slope angle range.
/// </summary>
[Expose]
public class SlopeMapping
{
	/// <summary>
	/// Minimum slope angle (degrees) for this entry.
	/// </summary>
	[Property, Range( 0, 90 )]
	public float MinAngle { get; set; } = 0f;

	/// <summary>
	/// Maximum slope angle (degrees) for this entry.
	/// </summary>
	[Property, Range( 0, 90 )]
	public float MaxAngle { get; set; } = 45f;

	/// <summary>
	/// Which clutter entry to use for this slope range.
	/// </summary>
	[Property]
	[Title( "Entry" )]
	[Editor( "ClutterEntryPicker" )]
	public int EntryIndex { get; set; } = 0;

	public override int GetHashCode() => HashCode.Combine( MinAngle, MaxAngle, EntryIndex );
}

/// <summary>
/// Scatterer that filters and selects assets based on the slope angle of the surface.
/// Useful for placing different vegetation or rocks on flat vs steep terrain.
/// </summary>
[Expose]
public class SlopeScatterer : Scatterer
{
	/// <summary>
	/// Scale range for spawned objects.
	/// </summary>
	[Property]
	public RangedFloat Scale { get; set; } = new RangedFloat( 0.8f, 1.2f );

	/// <summary>
	/// Points per square meter (density).
	/// </summary>
	[Property, Range( 0.001f, 10f )]
	public float Density { get; set; } = 0.1f;

	/// <summary>
	/// Offset from ground surface.
	/// </summary>
	[Property, Group( "Placement" )]
	public float HeightOffset { get; set; } = 0f;

	/// <summary>
	/// Align objects to surface normal.
	/// </summary>
	[Property, Group( "Placement" )]
	public bool AlignToNormal { get; set; } = false;

	/// <summary>
	/// Define which entries spawn at which slope angles.
	/// </summary>
	[Property, Group( "Slope Mappings" )]
	public List<SlopeMapping> Mappings { get; set; } = new();

	/// <summary>
	/// Use random clutter entry if no slope mapping matches.
	/// </summary>
	[Property]
	public bool UseFallback { get; set; } = true;

	protected override List<ClutterInstance> Generate( BBox bounds, ClutterDefinition clutter, Scene scene = null )
	{
		scene ??= Game.ActiveScene;
		if ( scene == null || clutter == null || clutter.IsEmpty )
			return [];

		var pointCount = CalculatePointCount( bounds, Density );
		var points = JitteredGridPoints( bounds, pointCount );
		var instances = new List<ClutterInstance>( points.Length );

		if ( points.Length == 0 )
			return instances;

		var sceneBounds = scene.GetBounds();
		using var pooledTraces = RentGroundTraces( scene, points, sceneBounds );

		foreach ( var trace in pooledTraces.Span )
		{
			if ( !trace.Hit )
				continue;

			// Calculate slope angle
			var normal = trace.Normal;
			var slopeAngle = Vector3.GetAngle( Vector3.Up, normal );

			var entry = GetEntryForSlope( clutter, slopeAngle );
			if ( entry == null )
			{
				if ( UseFallback )
				{
					entry = GetRandomEntry( clutter );
				}
				if ( entry == null )
					continue;
			}

			// Setup transform
			var scale = Random.Float( Scale.Min, Scale.Max );
			var yaw = Random.Float( 0f, 360f );
			var rotation = AlignToNormal
				? GetAlignedRotation( normal, yaw )
				: Rotation.FromYaw( yaw );

			var position = trace.HitPosition + normal * HeightOffset;

			instances.Add( new ClutterInstance
			{
				Transform = new Transform( position, rotation, scale ),
				Entry = entry
			} );
		}

		return instances;
	}

	/// <summary>
	/// Finds an entry that matches the given slope angle based on mappings.
	/// </summary>
	private ClutterEntry GetEntryForSlope( ClutterDefinition clutter, float slopeAngle )
	{
		if ( Mappings is null or { Count: 0 } )
			return GetRandomEntry( clutter );

		var matchCount = 0;
		foreach ( var m in Mappings )
		{
			if ( slopeAngle >= m.MinAngle && slopeAngle <= m.MaxAngle )
				matchCount++;
		}

		if ( matchCount is 0 )
			return null;

		// Pick a random index within matching mappings
		var randomIndex = Random.Int( 0, matchCount - 1 );
		var currentIndex = 0;

		foreach ( var m in Mappings )
		{
			if ( slopeAngle >= m.MinAngle && slopeAngle <= m.MaxAngle )
			{
				if ( currentIndex == randomIndex )
				{
					if ( m.EntryIndex >= 0 && m.EntryIndex < clutter.Entries.Count )
					{
						var entry = clutter.Entries[m.EntryIndex];
						if ( entry?.HasAsset is true )
							return entry;
					}
					break;
				}
				currentIndex++;
			}
		}

		return null;
	}
}

/// <summary>
/// Maps a terrain material to a list of clutter entries that can spawn on it.
/// </summary>
[Expose]
public class TerrainMaterialMapping
{
	/// <summary>
	/// The terrain material to match.
	/// </summary>
	[Property]
	public TerrainMaterial Material { get; set; }

	/// <summary>
	/// Indices of clutter entries that can spawn on this material.
	/// </summary>
	[Property]
	[Title( "Entry Indices" )]
	public List<int> EntryIndices { get; set; } = [];

	public override int GetHashCode()
	{
		var hash = new HashCode();
		hash.Add( Material?.GetHashCode() ?? 0 );
		foreach ( var index in EntryIndices )
			hash.Add( index );
		return hash.ToHashCode();
	}
}

/// <summary>
/// Scatterer that selects assets based on the terrain material at the hit position.
/// Useful for placing different vegetation on different terrain textures (grass, dirt, rock, etc).
/// </summary>
[Expose]
public class TerrainMaterialScatterer : Scatterer
{
	private const int StreamingTraceChunkSize = 32;
	/// <summary>
	/// Scale range for spawned objects.
	/// </summary>
	[Property]
	public RangedFloat Scale { get; set; } = new RangedFloat( 0.8f, 1.2f );

	/// <summary>
	/// Points per square meter (density).
	/// </summary>
	[Property, Range( 0.001f, 10f )]
	public float Density { get; set; } = 0.1f;

	/// <summary>
	/// Offset from ground surface.
	/// </summary>
	[Property, Group( "Placement" )]
	public float HeightOffset { get; set; } = 0f;

	/// <summary>
	/// Align objects to surface normal.
	/// </summary>
	[Property, Group( "Placement" )]
	public bool AlignToNormal { get; set; } = false;

	/// <summary>
	/// Apply random rotation around vertical axis.
	/// </summary>
	[Property, Group( "Placement" )]
	public bool RandomYaw { get; set; } = true;

	/// <summary>
	/// Define which entries spawn on which terrain materials.
	/// </summary>
	[Property, Group( "Material Mappings" )]
	public List<TerrainMaterialMapping> Mappings { get; set; } = new();

	/// <summary>
	/// Use random clutter entry if no material mapping matches or no terrain is present.
	/// </summary>
	[Property, Group( "Fallback" )]
	public bool UseFallback { get; set; } = true;

	/// <summary>
	/// Cached terrain reference to avoid repeated GetComponent calls within same tile.
	/// </summary>
	[JsonIgnore, Hide]
	private Terrain _cachedTerrain;

	[JsonIgnore, Hide]
	private GameObject _cachedTerrainObject;

	protected override List<ClutterInstance> Generate( BBox bounds, ClutterDefinition clutter, Scene scene = null )
	{
		scene ??= Game.ActiveScene;
		if ( scene == null || clutter == null || clutter.IsEmpty )
			return [];

		// Clear terrain cache for new generation
		_cachedTerrain = null;
		_cachedTerrainObject = null;

		var pointCount = CalculatePointCount( bounds, Density );
		var points = JitteredGridPoints( bounds, pointCount );
		var instances = new List<ClutterInstance>( points.Length );

		if ( points.Length == 0 )
			return instances;

		var sceneBounds = scene.GetBounds();
		using var pooledTraces = RentGroundTraces( scene, points, sceneBounds );

		foreach ( var trace in pooledTraces.Span )
		{
			if ( !trace.Hit )
				continue;

			var terrain = GetTerrainFromTrace( trace );
			if ( terrain == null )
			{
				if ( UseFallback )
				{
					var fallbackEntry = GetRandomEntry( clutter );
					if ( fallbackEntry != null )
					{
						instances.Add( CreateInstance( trace, fallbackEntry ) );
					}
				}
				continue;
			}

			// Query terrain material at hit position
			var materialInfo = terrain.GetMaterialAtWorldPosition( trace.HitPosition );
			if ( !materialInfo.HasValue || materialInfo.Value.IsHole )
				continue;

			// Find matching entry from material mappings
			var entry = GetEntryForMaterial( clutter, materialInfo.Value );
			if ( entry == null )
			{
				if ( UseFallback )
				{
					entry = GetRandomEntry( clutter );
				}
				if ( entry == null )
					continue;
			}

			instances.Add( CreateInstance( trace, entry ) );
		}

		return instances;
	}

	/// <summary>
	/// Creates main-thread resumable work for an infinite terrain tile. The local RNG, jittered
	/// point order, trace order, material selection and transform RNG consumption are identical to
	/// <see cref="Generate"/>; only the trace/result loop is split into bounded chunks.
	/// VERIFIED via /check-engine: no scene or GPU API leaves the main thread.
	/// </summary>
	internal TerrainMaterialScatterWork CreateStreamingWork( BBox bounds, ClutterDefinition clutter, int seed, Scene scene )
	{
		return new TerrainMaterialScatterWork( this, bounds, clutter, seed, scene ?? Game.ActiveScene );
	}

	private ClutterInstance CreateInstance( SceneTraceResult trace, ClutterEntry entry )
	{
		return CreateInstance( trace, entry, Random );
	}

	private ClutterInstance CreateInstance( SceneTraceResult trace, ClutterEntry entry, Random random )
	{
		var scale = random.Float( Scale.Min, Scale.Max );
		var normal = trace.Normal;
		var yaw = RandomYaw ? random.Float( 0f, 360f ) : 0f;

		Rotation rotation;
		if ( AlignToNormal )
		{
			rotation = GetAlignedRotation( normal, yaw );
		}
		else
		{
			rotation = Rotation.FromYaw( yaw );
		}

		var position = trace.HitPosition + normal * HeightOffset;

		return new ClutterInstance
		{
			Transform = new Transform( position, rotation, scale ),
			Entry = entry
		};
	}

	/// <summary>
	/// Gets the Terrain component from a trace result, with caching.
	/// </summary>
	private Terrain GetTerrainFromTrace( SceneTraceResult trace )
	{
		return GetTerrainFromTrace( trace, ref _cachedTerrainObject, ref _cachedTerrain );
	}

	private static Terrain GetTerrainFromTrace( SceneTraceResult trace, ref GameObject cachedTerrainObject, ref Terrain cachedTerrain )
	{
		var hitObject = trace.GameObject;
		if ( hitObject == null )
			return null;

		// Use cached terrain if hitting same object
		if ( cachedTerrainObject == hitObject )
			return cachedTerrain;

		// Cache the terrain lookup
		cachedTerrainObject = hitObject;
		cachedTerrain = hitObject.Components.Get<Terrain>();

		return cachedTerrain;
	}

	/// <summary>
	/// Finds an entry that matches the terrain material at the given position.
	/// </summary>
	private ClutterEntry GetEntryForMaterial( ClutterDefinition clutter, Terrain.TerrainMaterialInfo materialInfo )
	{
		return GetEntryForMaterial( clutter, materialInfo, Random );
	}

	private ClutterEntry GetEntryForMaterial( ClutterDefinition clutter, Terrain.TerrainMaterialInfo materialInfo, Random random )
	{
		if ( Mappings is null or { Count: 0 } )
			return null;

		// Get the dominant material
		var dominantMaterial = materialInfo.GetDominantMaterial();
		if ( dominantMaterial is null )
			return null;

		// Find mapping for this material
		TerrainMaterialMapping mapping = null;
		foreach ( var m in Mappings )
		{
			if ( m.Material == dominantMaterial )
			{
				mapping = m;
				break;
			}
		}

		if ( mapping is null || mapping.EntryIndices is null or { Count: 0 } )
			return null;

		var totalWeight = 0f;
		foreach ( var index in mapping.EntryIndices )
		{
			if ( index >= 0 && index < clutter.Entries.Count )
			{
				var entry = clutter.Entries[index];
				if ( entry?.HasAsset is true && entry.Weight > 0 )
					totalWeight += entry.Weight;
			}
		}

		if ( totalWeight <= 0 )
			return null;

		// Pick a weighted random entry
		var randomValue = random.Float( 0f, totalWeight );
		var currentWeight = 0f;

		foreach ( var index in mapping.EntryIndices )
		{
			if ( index >= 0 && index < clutter.Entries.Count )
			{
				var entry = clutter.Entries[index];
				if ( entry?.HasAsset is true && entry.Weight > 0 )
				{
					currentWeight += entry.Weight;
					if ( randomValue <= currentWeight )
						return entry;
				}
			}
		}

		// Fallback: return last valid entry
		for ( var i = mapping.EntryIndices.Count - 1; i >= 0; i-- )
		{
			var index = mapping.EntryIndices[i];
			if ( index >= 0 && index < clutter.Entries.Count )
			{
				var entry = clutter.Entries[index];
				if ( entry?.HasAsset is true && entry.Weight > 0 )
					return entry;
			}
		}

		return null;
	}

	private static ClutterEntry GetRandomEntry( ClutterDefinition clutter, Random random )
	{
		if ( clutter.IsEmpty )
			return null;

		var totalWeight = 0f;
		foreach ( var entry in clutter.Entries )
			if ( entry?.HasAsset is true && entry.Weight > 0 ) totalWeight += entry.Weight;

		if ( totalWeight is 0 )
			return null;

		var randomValue = random.Float( 0f, totalWeight );
		var currentWeight = 0f;
		foreach ( var entry in clutter.Entries )
		{
			if ( entry?.HasAsset is not true || entry.Weight <= 0 ) continue;
			currentWeight += entry.Weight;
			if ( randomValue <= currentWeight ) return entry;
		}

		return null;
	}

	internal sealed class TerrainMaterialScatterWork
	{
		private readonly TerrainMaterialScatterer _scatterer;
		private readonly ClutterDefinition _clutter;
		private readonly Scene _scene;
		private readonly Random _random;
		private readonly Vector3[] _points;
		private readonly BBox _sceneBounds;
		private readonly List<ClutterInstance> _instances;

		private GameObject _cachedTerrainObject;
		private Terrain _cachedTerrain;
		private int _nextPoint;

		internal TerrainMaterialScatterWork( TerrainMaterialScatterer scatterer, BBox bounds, ClutterDefinition clutter, int seed, Scene scene )
		{
			_scatterer = scatterer;
			_clutter = clutter;
			_scene = scene;
			_random = new Random( seed );

			if ( scene == null || clutter == null || clutter.IsEmpty )
			{
				_points = [];
				_instances = [];
				return;
			}

			var pointCount = CalculatePointCountExact( bounds, scatterer.Density, _random );
			_points = CreateJitteredGridExact( bounds, pointCount, _random );
			_instances = new List<ClutterInstance>( _points.Length );
			_sceneBounds = scene.GetBounds();
		}

		internal List<ClutterInstance> Instances => _instances;
		internal int PointsProcessed => _nextPoint;

		internal bool ExecuteUntil( long deadlineTimestamp )
		{
			while ( _nextPoint < _points.Length )
			{
				var count = Math.Min( StreamingTraceChunkSize, _points.Length - _nextPoint );
				var chunk = new ArraySegment<Vector3>( _points, _nextPoint, count );
				using var pooledTraces = RentGroundTraces( _scene, chunk, _sceneBounds );

				foreach ( var trace in pooledTraces.Span )
					ProcessTrace( trace );

				_nextPoint += count;
				if ( _nextPoint < _points.Length && Stopwatch.GetTimestamp() >= deadlineTimestamp )
					return false;
			}

			return true;
		}

		private void ProcessTrace( SceneTraceResult trace )
		{
			if ( !trace.Hit ) return;

			var terrain = GetTerrainFromTrace( trace, ref _cachedTerrainObject, ref _cachedTerrain );
			if ( terrain == null )
			{
				if ( _scatterer.UseFallback )
				{
					var fallback = GetRandomEntry( _clutter, _random );
					if ( fallback != null ) _instances.Add( _scatterer.CreateInstance( trace, fallback, _random ) );
				}
				return;
			}

			var materialInfo = terrain.GetMaterialAtWorldPosition( trace.HitPosition );
			if ( !materialInfo.HasValue || materialInfo.Value.IsHole ) return;

			var entry = _scatterer.GetEntryForMaterial( _clutter, materialInfo.Value, _random );
			if ( entry == null && _scatterer.UseFallback ) entry = GetRandomEntry( _clutter, _random );
			if ( entry != null ) _instances.Add( _scatterer.CreateInstance( trace, entry, _random ) );
		}

		private static int CalculatePointCountExact( BBox bounds, float density, Random random )
		{
			var desired = bounds.Size.x.InchToMeter() * bounds.Size.y.InchToMeter() * density / 10f;
			var guaranteed = (int)desired;
			var count = guaranteed + (random.Float( 0f, 1f ) < desired - guaranteed ? 1 : 0);
			return Math.Clamp( count, 0, 10000 );
		}

		private static Vector3[] CreateJitteredGridExact( BBox bounds, int pointCount, Random random )
		{
			GetJitteredGridSize( bounds, pointCount, out int cellsX, out int cellsY );
			if ( cellsX <= 0 || cellsY <= 0 ) return [];

			var cellWidth = bounds.Size.x / cellsX;
			var cellHeight = bounds.Size.y / cellsY;
			var points = new Vector3[cellsX * cellsY];
			var index = 0;
			for ( int cy = 0; cy < cellsY; cy++ )
				for ( int cx = 0; cx < cellsX; cx++ )
					points[index++] = new Vector3(
						bounds.Mins.x + cx * cellWidth + random.Float( cellWidth ),
						bounds.Mins.y + cy * cellHeight + random.Float( cellHeight ), 0f );
			return points;
		}
	}
}
