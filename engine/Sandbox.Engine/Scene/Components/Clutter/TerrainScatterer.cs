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
		var instances = new List<ClutterInstance>( pointCount );

		// Hoist the trace Z envelope once — see Scatterer.TraceGround docs.
		var (traceZMin, traceZMax) = ResolveTraceZRange( scene, bounds );

		for ( int i = 0; i < pointCount; i++ )
		{
			var point = new Vector3(
				bounds.Mins.x + Random.Float( bounds.Size.x ),
				bounds.Mins.y + Random.Float( bounds.Size.y ),
				0f
			);

			// Trace to ground
			var trace = TraceGround( scene, point, traceZMin, traceZMax );
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
		var instances = new List<ClutterInstance>( pointCount );

		// Hoist the trace Z envelope once for the whole job — replaces the per-point
		// scene.GetBounds() walk that was O(points) x O(scene-components-with-IHasBounds).
		var (traceZMin, traceZMax) = ResolveTraceZRange( scene, bounds );

		for ( int i = 0; i < pointCount; i++ )
		{
			if ( TryCreateInstance( bounds, clutter, scene, Random, traceZMin, traceZMax, ref _cachedTerrainObject, ref _cachedTerrain, out var instance ) )
				instances.Add( instance );
		}

		return instances;
	}

	internal TerrainMaterialScatterWork CreateStreamingWork( BBox bounds, ClutterDefinition clutter, int seed, Scene scene = null )
	{
		scene ??= Game.ActiveScene;
		return new TerrainMaterialScatterWork( this, bounds, clutter, seed, scene );
	}

	private bool TryCreateInstance(
		BBox bounds,
		ClutterDefinition clutter,
		Scene scene,
		Random random,
		float traceZMin,
		float traceZMax,
		ref GameObject cachedTerrainObject,
		ref Terrain cachedTerrain,
		out ClutterInstance instance )
	{
		instance = default;

		if ( scene == null || clutter == null || clutter.IsEmpty || random is null )
			return false;

		var point = new Vector3(
			bounds.Mins.x + random.Float( bounds.Size.x ),
			bounds.Mins.y + random.Float( bounds.Size.y ),
			0f
		);

		var trace = TraceGround( scene, point, traceZMin, traceZMax );
		if ( !trace.Hit )
			return false;

		var terrain = GetTerrainFromTrace( trace, ref cachedTerrainObject, ref cachedTerrain );
		if ( terrain == null )
		{
			if ( UseFallback )
			{
				var fallbackEntry = GetRandomEntry( clutter, random );
				if ( fallbackEntry != null )
				{
					instance = CreateInstance( trace, fallbackEntry, random );
					return true;
				}
			}
			return false;
		}

		var materialInfo = terrain.GetMaterialAtWorldPosition( trace.HitPosition );
		if ( !materialInfo.HasValue || materialInfo.Value.IsHole )
			return false;

		var entry = GetEntryForMaterial( clutter, materialInfo.Value, random );
		if ( entry == null )
		{
			if ( UseFallback )
			{
				entry = GetRandomEntry( clutter, random );
			}
			if ( entry == null )
				return false;
		}

		instance = CreateInstance( trace, entry, random );
		return true;
	}

	private ClutterInstance CreateInstance( SceneTraceResult trace, ClutterEntry entry )
	{
		return CreateInstance( trace, entry, Random );
	}

	private ClutterInstance CreateInstance( SceneTraceResult trace, ClutterEntry entry, Random random )
	{
		random ??= Random;
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

		random ??= Random;
		if ( random is null )
			return null;

		// Get the dominant material
		var dominantMaterial = materialInfo.GetDominantMaterial();
		if ( dominantMaterial is null )
			return null;

		// Find mapping for this material
		var mapping = Mappings.FirstOrDefault( m => m.Material == dominantMaterial );
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

	internal sealed class TerrainMaterialScatterWork
	{
		private const int BudgetCheckInterval = 1;

		private readonly TerrainMaterialScatterer _scatterer;
		private readonly BBox _bounds;
		private readonly ClutterDefinition _clutter;
		private readonly Scene _scene;
		private readonly Random _random;
		private readonly int _pointCount;
		private readonly List<ClutterInstance> _instances;

		// Trace Z envelope resolved ONCE at job creation via ResolveTraceZRange — see Scatterer.cs.
		// Prevents the streaming work item from calling scene.GetBounds() per-point (was the fill-time
		// killer on Eden because the solar-system-scale scene has thousands of IHasBounds components).
		private readonly float _traceZMin;
		private readonly float _traceZMax;

		private GameObject _cachedTerrainObject;
		private Terrain _cachedTerrain;
		private int _nextPoint;

		public TerrainMaterialScatterWork( TerrainMaterialScatterer scatterer, BBox bounds, ClutterDefinition clutter, int seed, Scene scene )
		{
			_scatterer = scatterer;
			_bounds = bounds;
			_clutter = clutter;
			_scene = scene;
			_random = new Random( seed );

			if ( scene == null || clutter == null || clutter.IsEmpty )
			{
				_pointCount = 0;
				_instances = [];
				return;
			}

			(_traceZMin, _traceZMax) = ResolveTraceZRange( scene, bounds );

			_pointCount = scatterer.CalculatePointCount( bounds, scatterer.Density, _random );
			_instances = new List<ClutterInstance>( _pointCount );
		}

		public List<ClutterInstance> Instances => _instances;

		public bool ExecuteUntil( long deadlineTimestamp )
		{
			var processed = 0;

			while ( _nextPoint < _pointCount )
			{
				if ( processed > 0 && processed % BudgetCheckInterval == 0 && Stopwatch.GetTimestamp() >= deadlineTimestamp )
					return false;

				if ( _scatterer.TryCreateInstance( _bounds, _clutter, _scene, _random, _traceZMin, _traceZMax, ref _cachedTerrainObject, ref _cachedTerrain, out var instance ) )
					_instances.Add( instance );

				_nextPoint++;
				processed++;
			}

			return true;
		}
	}
}
