using System.Diagnostics;

namespace Sandbox.Clutter;

/// <summary>
/// Game object system that manages clutter generation.
/// Handles infinite streaming layers and executes generation jobs.
/// </summary>
public sealed partial class ClutterGridSystem : GameObjectSystem
{
	/// <summary>
	/// Mapping of clutter components to their respective layers
	/// </summary>
	private readonly Dictionary<ClutterComponent, ClutterLayer> _componentToLayer = [];

	private const int MAX_JOBS_PER_FRAME = 8;
	private const int MAX_PENDING_JOBS = 100;
	private const float MIN_STREAMING_BUDGET_MS = 0.1f;

	[ConVar( "clutter_incremental_streaming", ConVarFlags.Cheat )]
	internal static bool IncrementalStreaming { get; set; } = false;

	[ConVar( "clutter_stream_budget_ms", ConVarFlags.Cheat )]
	internal static float StreamingBudgetMs { get; set; } = 1.5f;

	internal static long s_pointsProcessed;
	internal static long s_tilesCompleted;
	internal static long s_uploadedRecords;
	internal static long s_appendedRecords;
	internal static long s_inactiveRecords;
	internal static long s_fullBatchRebuilds;
	internal static long s_deferredCompactions;
	internal static long s_incrementalFallbacks;

	private readonly List<ClutterGenerationJob> _pendingJobs = [];
	private readonly HashSet<ClutterTile> _pendingTiles = [];
	private readonly HashSet<Terrain> _subscribedTerrains = [];
	private Vector3 _lastCameraPosition;

	// Reused by the update path so an idle scene doesn't allocate.
	private readonly List<ClutterComponent> _activeInfinite = [];
	private readonly List<ClutterComponent> _componentsToRemove = [];
	private readonly List<Terrain> _sceneTerrains = [];
	private readonly HashSet<ClutterLayer> _layersToRebuild = [];

	/// <summary>
	/// Storage for painted clutter model instances.
	/// Serialized with the scene - this is the source of truth for painted clutter.
	/// </summary>
	[Property, Hide]
	public ClutterStorage Storage
	{
		get;
		set
		{
			field = value;
			_dirty = true;
		}
	} = new();

	/// <summary>
	/// Layer for rendering painted model instances from Storage.
	/// This is transient - rebuilt from Storage on scene load.
	/// </summary>
	private ClutterLayer _painted;

	private bool _dirty = false;

	public ClutterGridSystem( Scene scene ) : base( scene )
	{
		Listen( Stage.FinishUpdate, 0, OnUpdate, "ClutterGridSystem.Update" );
		Listen( Stage.SceneLoaded, 0, RestorePaintedLayer, "ClutterGridSystem.RestorePainted" );
	}

	public override void Dispose()
	{
		base.Dispose();

		foreach ( var terrain in _subscribedTerrains )
			if ( terrain.IsValid() ) terrain.OnTerrainModified -= OnTerrainModified;
		_subscribedTerrains.Clear();

		_painted?.ClearAllTiles();
		_painted = null;

		foreach ( var layer in _componentToLayer.Values )
			layer.ClearAllTiles();

		_componentToLayer.Clear();
	}

	/// <summary>
	/// Check for new terrains, queue generation/cleanup jobs, and process pending jobs.
	/// </summary>
	private void OnUpdate()
	{
		var camera = GetActiveCamera();
		if ( camera is not null )
		{
			_lastCameraPosition = camera.WorldPosition;

			PublishLodParameters( camera );

			SubscribeToTerrains();
			UpdateInfiniteLayers( _lastCameraPosition );
			ProcessJobs();
		}

		if ( _dirty )
		{
			RebuildPaintedLayer();
			_dirty = false;
		}

		_painted?.RebuildIfDirty();

		foreach ( var (component, layer) in _componentToLayer )
		{
			if ( component.IsValid() && !component.Infinite )
				layer.RebuildIfDirty();
		}
	}

	private void RestorePaintedLayer()
	{
		RebuildPaintedLayer();
		_dirty = false;
	}

	/// <summary>
	/// Pushes the active camera's LOD parameters to the clutter scene objects for GPU LOD selection.
	/// </summary>
	private static void PublishLodParameters( CameraComponent camera )
	{
		var sceneCamera = camera.SceneCamera;

		ClutterBatchSceneObject.Lod = new ClutterBatchSceneObject.LodParams
		{
			CameraPos = camera.WorldPosition,
			TanHalfFov = MathF.Tan( camera.FieldOfView.DegreeToRadian() * 0.5f ),
			ViewportWidth = sceneCamera is not null ? sceneCamera.Size.x : 1920f,
		};
	}

	private void SubscribeToTerrains()
	{
		_sceneTerrains.Clear();
		Scene.GetAll( _sceneTerrains );

		foreach ( var terrain in _sceneTerrains )
		{
			if ( _subscribedTerrains.Add( terrain ) )
			{
				terrain.OnTerrainModified += OnTerrainModified;
			}
		}

		_subscribedTerrains.RemoveWhere( t => !t.IsValid() );
	}

	private void UpdateActiveComponents( List<ClutterComponent> components, Vector3 cameraPosition )
	{
		foreach ( var component in components )
		{
			var settings = component.GetCurrentSettings();
			if ( !settings.IsValid )
				continue;

			var layer = GetOrCreateLayer( component, settings );
			layer.UpdateSettings( settings );

			foreach ( var job in layer.UpdateTiles( cameraPosition ) )
				QueueJob( job );
		}
	}

	private void RemoveInactiveComponents( List<ClutterComponent> activeInfiniteComponents )
	{
		_componentsToRemove.Clear();

		foreach ( var component in _componentToLayer.Keys )
		{
			if ( !component.IsValid() || (component.Infinite && !activeInfiniteComponents.Contains( component )) )
				_componentsToRemove.Add( component );
		}

		foreach ( var component in _componentsToRemove )
		{
			_componentToLayer[component].ClearAllTiles();
			_componentToLayer.Remove( component );
		}
	}

	private void UpdateInfiniteLayers( Vector3 cameraPosition )
	{
		_activeInfinite.Clear();
		Scene.GetAll( _activeInfinite );
		_activeInfinite.RemoveAll( static c => !c.Active || !c.Infinite );

		RemoveInactiveComponents( _activeInfinite );
		UpdateActiveComponents( _activeInfinite, cameraPosition );
	}

	/// <summary>
	/// Queues a generation job for processing.
	/// </summary>
	internal void QueueJob( ClutterGenerationJob job )
	{
		if ( !job.Parent.IsValid() )
			return;

		// Prevent duplicate jobs for the same tile
		if ( job.Tile is not null && !_pendingTiles.Add( job.Tile ) )
			return;

		_pendingJobs.Add( job );
	}

	/// <summary>
	/// Removes a tile from pending set (called when tile is destroyed).
	/// </summary>
	internal void RemovePendingTile( ClutterTile tile )
	{
		_pendingTiles.Remove( tile );
		_pendingJobs.RemoveAll( job => job.Tile == tile );
	}

	/// <summary>
	/// Clears all tiles for a specific component.
	/// </summary>
	public void ClearComponent( ClutterComponent component )
	{
		// Remove any pending jobs for this component (both tile and volume jobs)
		_pendingJobs.RemoveAll( job => job.Parent == component.GameObject );

		if ( _componentToLayer.Remove( component, out var layer ) )
		{
			layer.ClearAllTiles();
		}
	}

	/// <summary>
	/// Invalidates the tile at the given world position for a component, causing it to regenerate.
	/// </summary>
	public void InvalidateTileAt( ClutterComponent component, Vector3 worldPosition )
	{
		if ( _componentToLayer.TryGetValue( component, out var layer ) )
		{
			layer.InvalidateTile( worldPosition );
		}
	}

	/// <summary>
	/// Invalidates all tiles within the given bounds for a component, causing them to regenerate.
	/// </summary>
	public void InvalidateTilesInBounds( ClutterComponent component, BBox bounds )
	{
		if ( _componentToLayer.TryGetValue( component, out var layer ) )
		{
			layer.InvalidateTilesInBounds( bounds );
		}
	}

	/// <summary>
	/// Invalidates all tiles within the given bounds for ALL infinite clutter components.
	/// Useful for terrain painting where you want to refresh all clutter layers.
	/// </summary>
	public void InvalidateTilesInBounds( BBox bounds )
	{
		foreach ( var layer in _componentToLayer.Values )
		{
			layer.InvalidateTilesInBounds( bounds );
		}
	}

	private void OnTerrainModified( Terrain.SyncFlags flags, RectInt region )
	{
		var bounds = TerrainRegionToWorldBounds( _subscribedTerrains.First(), region );
		InvalidateTilesInBounds( bounds );
	}

	private static BBox TerrainRegionToWorldBounds( Terrain terrain, RectInt region )
	{
		var terrainTransform = terrain.WorldTransform;
		var storage = terrain.Storage;

		// Convert pixel coordinates to normalized (0-1) coordinates
		var minNorm = new Vector2(
			(float)region.Left / storage.Resolution,
			(float)region.Top / storage.Resolution
		);
		var maxNorm = new Vector2(
			(float)region.Right / storage.Resolution,
			(float)region.Bottom / storage.Resolution
		);

		var terrainSize = storage.TerrainSize;
		var minLocal = new Vector3( minNorm.x * terrainSize, minNorm.y * terrainSize, -1000f );
		var maxLocal = new Vector3( maxNorm.x * terrainSize, maxNorm.y * terrainSize, 1000f );

		var minWorld = terrainTransform.PointToWorld( minLocal );
		var maxWorld = terrainTransform.PointToWorld( maxLocal );

		return new BBox( minWorld, maxWorld );
	}

	internal CameraComponent GetActiveCamera()
	{
		if ( Scene.IsEditor )
		{
			var editorCamera = Application.Editor?.Camera;
			if ( editorCamera.IsValid() )
				return editorCamera;
		}

		return Scene.Camera;
	}

	internal ClutterLayer GetOrCreateLayer( ClutterComponent component, ClutterSettings settings )
	{
		if ( _componentToLayer.TryGetValue( component, out var layer ) )
			return layer;

		layer = new ClutterLayer( settings, component.GameObject, this );
		_componentToLayer[component] = layer;
		return layer;
	}

	private int _lastSortedJobCount = 0;

	private void ProcessJobs()
	{
		if ( !IncrementalStreaming )
		{
			ProcessJobsLegacy();
			return;
		}

		ProcessJobsBudgeted();
	}

	private void ProcessJobsLegacy()
	{
		if ( _pendingJobs.Count == 0 )
			return;

		// Track which layers had tiles populated
		_layersToRebuild.Clear();

		_pendingJobs.RemoveAll( job =>
			!job.Parent.IsValid() ||
			job.Tile?.IsPopulated == true
		);

		// Only sort when job count changes significantly (avoid sorting every frame)
		if ( Math.Abs( _pendingJobs.Count - _lastSortedJobCount ) > 50 || _lastSortedJobCount == 0 )
		{
			_pendingJobs.Sort( ( a, b ) =>
			{
				// Use tile bounds for infinite mode, job bounds for volume mode
				var distA = a.Tile != null
					? a.Tile.Bounds.Center.Distance( _lastCameraPosition )
					: a.Bounds.Center.Distance( _lastCameraPosition );
				var distB = b.Tile != null
					? b.Tile.Bounds.Center.Distance( _lastCameraPosition )
					: b.Bounds.Center.Distance( _lastCameraPosition );
				return distA.CompareTo( distB );
			} );
			_lastSortedJobCount = _pendingJobs.Count;
		}

		// Process nearest tiles first
		int processed = 0;
		while ( processed < MAX_JOBS_PER_FRAME && _pendingJobs.Count > 0 )
		{
			var job = _pendingJobs[0];
			_pendingJobs.RemoveAt( 0 );

			if ( job.Tile != null )
				_pendingTiles.Remove( job.Tile );

			// Execute if still valid and not populated
			if ( job.Parent.IsValid() && job.Tile?.IsPopulated != true )
			{
				job.Execute();
				processed++;

				if ( job.Layer != null )
					_layersToRebuild.Add( job.Layer );
			}
		}

		// Rebuild batches for layers that had tiles populated
		foreach ( var layer in _layersToRebuild )
		{
			layer.RebuildBatches();
		}

		var infiniteJobs = 0;
		foreach ( var job in _pendingJobs )
			if ( job.Tile != null ) infiniteJobs++;

		// Drop the furthest queued tiles. Sorted by distance, so walking back from the end takes those.
		for ( int i = _pendingJobs.Count - 1; i >= 0 && infiniteJobs > MAX_PENDING_JOBS; i-- )
		{
			var job = _pendingJobs[i];
			if ( job.Tile == null )
				continue;

			_pendingTiles.Remove( job.Tile );
			_pendingJobs.RemoveAt( i );
			infiniteJobs--;
		}
	}

	private void ProcessJobsBudgeted()
	{
		var deadline = CreateStreamingDeadline();
		if ( _pendingJobs.Count == 0 )
		{
			ServiceDeferredCompaction();
			return;
		}

		_layersToRebuild.Clear();
		_pendingJobs.RemoveAll( job => !job.Parent.IsValid() || job.Tile?.IsPopulated == true );

		if ( Math.Abs( _pendingJobs.Count - _lastSortedJobCount ) > 50 || _lastSortedJobCount == 0 )
		{
			var camera = _lastCameraPosition;
			_pendingJobs.Sort( (a, b) =>
			{
				var distA = a.Tile != null ? a.Tile.Bounds.Center.DistanceSquared( camera ) : a.Bounds.Center.DistanceSquared( camera );
				var distB = b.Tile != null ? b.Tile.Bounds.Center.DistanceSquared( camera ) : b.Bounds.Center.DistanceSquared( camera );
				return distA.CompareTo( distB );
			} );
			_lastSortedJobCount = _pendingJobs.Count;
		}

		var completed = 0;
		while ( completed < MAX_JOBS_PER_FRAME && _pendingJobs.Count > 0 )
		{
			var job = _pendingJobs[0];
			if ( !job.Parent.IsValid() || job.Tile?.IsPopulated == true )
			{
				_pendingJobs.RemoveAt( 0 );
				if ( job.Tile != null ) _pendingTiles.Remove( job.Tile );
				continue;
			}

			if ( completed > 0 && Stopwatch.GetTimestamp() >= deadline ) break;
			if ( !job.ExecuteBudgeted( deadline ) ) break;

			_pendingJobs.RemoveAt( 0 );
			if ( job.Tile != null ) _pendingTiles.Remove( job.Tile );
			completed++;
			if ( job.Layer != null ) _layersToRebuild.Add( job.Layer );
		}

		foreach ( var layer in _layersToRebuild ) layer.RebuildBatches();
		TrimPendingJobs();
		if ( _pendingJobs.Count == 0 ) ServiceDeferredCompaction();
	}

	private static long CreateStreamingDeadline()
	{
		var budgetMs = StreamingBudgetMs;
		if ( !float.IsFinite( budgetMs ) || budgetMs < MIN_STREAMING_BUDGET_MS ) budgetMs = MIN_STREAMING_BUDGET_MS;
		var budgetTicks = Math.Max( 1L, (long)(budgetMs * Stopwatch.Frequency / 1000.0) );
		var now = Stopwatch.GetTimestamp();
		return long.MaxValue - now <= budgetTicks ? long.MaxValue : now + budgetTicks;
	}

	private void ServiceDeferredCompaction()
	{
		foreach ( var layer in _componentToLayer.Values ) layer.CompactDeferredIfIdle();
	}

	private void TrimPendingJobs()
	{
		var infiniteJobs = 0;
		foreach ( var job in _pendingJobs ) if ( job.Tile != null ) infiniteJobs++;

		for ( int i = _pendingJobs.Count - 1; i >= 0 && infiniteJobs > MAX_PENDING_JOBS; i-- )
		{
			var job = _pendingJobs[i];
			if ( job.Tile == null ) continue;
			_pendingTiles.Remove( job.Tile );
			_pendingJobs.RemoveAt( i );
			infiniteJobs--;
		}
	}

	[ConCmd( "clutter_stats", ConVarFlags.Cheat )]
	internal static void ConCmdClutterStats()
	{
		var scene = Game.ActiveScene;
		var system = scene?.GetSystem<ClutterGridSystem>();
		if ( system == null )
		{
			Log.Info( "clutter_stats: no active ClutterGridSystem" );
			return;
		}

		Log.Info( $"clutter_stats candidate={IncrementalStreaming} budget_ms={StreamingBudgetMs:0.###} pending={system._pendingJobs.Count} points={s_pointsProcessed} tiles={s_tilesCompleted} appended={s_appendedRecords} uploaded={s_uploadedRecords} inactive={s_inactiveRecords} full_rebuilds={s_fullBatchRebuilds} compactions={s_deferredCompactions} fallbacks={s_incrementalFallbacks}" );
	}

	[ConCmd( "clutter_stats_reset", ConVarFlags.Cheat )]
	internal static void ConCmdClutterStatsReset()
	{
		s_pointsProcessed = 0;
		s_tilesCompleted = 0;
		s_uploadedRecords = 0;
		s_appendedRecords = 0;
		s_inactiveRecords = 0;
		s_fullBatchRebuilds = 0;
		s_deferredCompactions = 0;
		s_incrementalFallbacks = 0;
		Log.Info( "clutter_stats_reset: ok" );
	}

	[ConCmd( "clutter_validate_incremental", ConVarFlags.Cheat )]
	internal static void ConCmdClutterValidateIncremental()
	{
		var system = Game.ActiveScene?.GetSystem<ClutterGridSystem>();
		if ( system == null ) { Log.Info( "clutter_validate_incremental: FAIL no system" ); return; }
		var checkedLayers = 0;
		foreach ( var (component, layer) in system._componentToLayer )
		{
			if ( !component.IsValid() || !component.Infinite ) continue;
			checkedLayers++;
			if ( !layer.ValidateIncremental( out var detail ) )
			{
				Log.Info( $"clutter_validate_incremental: FAIL layer={component.GameObject?.Name} {detail}" );
				return;
			}
			Log.Info( $"clutter_validate_incremental: layer={component.GameObject?.Name} {detail}" );
		}
		Log.Info( $"clutter_validate_incremental: PASS layers={checkedLayers}" );
	}

	[ConCmd( "clutter_force_incremental_fallback", ConVarFlags.Cheat )]
	internal static void ConCmdClutterForceIncrementalFallback()
	{
		var system = Game.ActiveScene?.GetSystem<ClutterGridSystem>();
		if ( system == null ) { Log.Info( "clutter_force_incremental_fallback: FAIL no system" ); return; }
		var count = 0;
		foreach ( var (component, layer) in system._componentToLayer )
		{
			if ( !component.IsValid() || !component.Infinite ) continue;
			layer.ForceIncrementalFallbackForTest();
			count++;
		}
		Log.Info( $"clutter_force_incremental_fallback: PASS layers={count} fallbacks={s_incrementalFallbacks}" );
	}

	[ConCmd( "clutter_source_signature", ConVarFlags.Cheat )]
	internal static void ConCmdClutterSourceSignature()
	{
		var system = Game.ActiveScene?.GetSystem<ClutterGridSystem>();
		if ( system == null ) { Log.Info( "clutter_source_signature: FAIL no system" ); return; }
		var total = 0;
		var hash = new HashCode();
		var layers = 0;
		foreach ( var (component, layer) in system._componentToLayer )
		{
			if ( !component.IsValid() || !component.Infinite ) continue;
			var signature = layer.GetOrderedSourceSignature();
			hash.Add( component.GameObject?.Name );
			hash.Add( signature.Hash );
			total += signature.Count;
			layers++;
		}
		Log.Info( $"clutter_source_signature candidate={IncrementalStreaming} layers={layers} records={total} hash={hash.ToHashCode():x8}" );
	}

	/// <summary>
	/// Paint instance. Rebuilds on next frame update.
	/// Models are batched, Prefabs become GameObjects.
	/// </summary>
	public void Paint( ClutterEntry entry, Vector3 pos, Rotation rot, float scale = 1f )
	{
		if ( entry == null || !entry.HasAsset ) return;

		if ( entry.Prefab != null )
		{
			var go = entry.Prefab.Clone( pos, rot );
			go.WorldScale = scale;
			go.SetParent( Scene );
			go.Tags.Add( "clutter_painted" );
		}
		else if ( entry.Model != null )
		{
			Storage.AddInstance( entry.Model.ResourcePath, pos, rot, scale );
			_dirty = true;
		}
	}

	/// <summary>
	/// Erase instances. Rebuilds on next frame update.
	/// Erases both model batches and prefab GameObjects.
	/// </summary>
	public void Erase( Vector3 pos, float radius )
	{
		var radiusSquared = radius * radius;
		if ( Storage.Erase( pos, radius ) > 0 )
		{
			_dirty = true;
		}

		// Only erase painted prefabs, not streamed/volume clutter
		var paintedObjects = Scene.FindAllWithTag( "clutter_painted" )
			.Where( go => go.WorldPosition.DistanceSquared( pos ) <= radiusSquared )
			.ToList();

		foreach ( var go in paintedObjects )
		{
			go.Destroy();
		}
	}

	/// <summary>
	/// Clears all painted clutter (both model instances from storage and prefab GameObjects).
	/// Does not affect clutter owned by ClutterComponent volumes.
	/// </summary>
	public void ClearAllPainted()
	{
		Storage.ClearAll();

		var paintedObjects = Scene.FindAllWithTag( "clutter_painted" ).ToList();
		foreach ( var go in paintedObjects )
		{
			go.Destroy();
		}

		_painted?.ClearAllTiles();
		_dirty = false;
	}

	/// <summary>
	/// Flush painted changes and rebuild visual batches immediately.
	/// </summary>
	public void Flush()
	{
		RebuildPaintedLayer();
		_dirty = false;
	}

	/// <summary>
	/// Rebuild the painted clutter layer from stored instances in Storage.
	/// </summary>
	private void RebuildPaintedLayer()
	{
		if ( Storage is null )
		{
			Storage = new ClutterStorage();
		}

		if ( Storage.TotalCount == 0 )
		{
			_painted?.ClearAllTiles();
			return;
		}

		// Create or reuse painted layer
		if ( _painted == null )
		{
			var settings = new ClutterSettings( 0, new ClutterDefinition() );
			_painted = new ClutterLayer( settings, null, this );
		}

		// The layer owns both rendering and collision for its instances.
		_painted.PopulateFromStorage( Storage );
	}
}
