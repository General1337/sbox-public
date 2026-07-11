// [PERF-OK: reconvergence patch for fork commit 8073facb "Fix clutter streaming frame spikes".
//  Fork re-keyed _batches to (TileCoord, Model), producing up to R*R*M batches at Eden radius 8
//  (289 tiles x 12 models = 3468 batches; ~470ms GPU frames + editor crash). Upstream stock keying
//  is Dictionary<Model, ClutterBatchSceneObject>. This file restores stock keying while preserving
//  the fork's per-tile bookkeeping and budgeted streaming, coalescing many dirty tiles into one
//  SetInstances-per-model rebuild under the existing streaming deadline.]
namespace Sandbox.Clutter;

class ClutterLayer
{
	private Dictionary<Vector2Int, ClutterTile> Tiles { get; } = [];

	public ClutterSettings Settings { get; set; }

	/// <summary>
	/// Game object clutter will be placed under this parent
	/// </summary>
	public GameObject ParentObject { get; set; }

	public ClutterGridSystem GridSystem { get; set; }

	/// <summary>
	/// Model instances organized by tile coordinate.
	/// Per-tile bookkeeping stays here so budgeted streaming, tile invalidation and
	/// out-of-range removal can operate at tile granularity.
	/// </summary>
	private Dictionary<Vector2Int, List<ClutterInstance>> ModelInstancesByTile { get; } = [];

	/// <summary>
	/// Batches organized by model (one merged <see cref="ClutterBatchSceneObject"/> per model per layer,
	/// matching stock keying). At Eden radius 8 this is ~12 batches instead of ~3.4k, so each frame's
	/// command-list replay is bounded by model count rather than tile count.
	/// </summary>
	private readonly Dictionary<Model, ClutterBatchSceneObject> _batches = [];

	/// <summary>
	/// Models whose merged batch needs re-upload. Tile churn coalesces here so many dirty tiles
	/// collapse into ONE <see cref="ClutterBatchSceneObject.SetInstances"/> call per model per
	/// <see cref="RebuildBatches"/>, and the streaming deadline throttles that to
	/// <see cref="MaxDirtyModelsPerRebuild"/> models per frame.
	/// </summary>
	private readonly HashSet<Model> _dirtyModels = [];

	private readonly List<Transform> _transformScratch = [];
	private readonly List<Model> _rebuildScratch = [];

	private readonly HashSet<Vector2Int> _activeCoords = [];
	private readonly List<Vector2Int> _coordsToRemove = [];
	private readonly List<ClutterGenerationJob> _pendingJobs = [];

	/// <summary>
	/// Static collision bodies organized by tile coordinate. The layer owns collision
	/// alongside rendering, so every instance source (streamed, volume, painted) gets the
	/// same physics behaviour without duplicating body lifecycle logic.
	/// </summary>
	private readonly Dictionary<Vector2Int, List<PhysicsBody>> _bodiesByTile = [];

	private int _lastSettingsHash;
	private const float TileHeight = 50000f;

	/// <summary>
	/// Upper bound on how many model-batches this layer will re-upload in one
	/// <see cref="RebuildBatches"/> pass. Bounds the per-frame CPU cost of merged-batch rebuilds
	/// under the streaming deadline; leftover dirty models roll into the next frame.
	/// </summary>
	private const int MaxDirtyModelsPerRebuild = 2;

	private bool _dirty = false;

	public bool IsDirty => _dirty;

	public ClutterLayer( ClutterSettings settings, GameObject parentObject, ClutterGridSystem gridSystem )
	{
		Settings = settings;
		ParentObject = parentObject;
		GridSystem = gridSystem;
		_lastSettingsHash = settings.GetHashCode();
	}

	public void UpdateSettings( ClutterSettings newSettings )
	{
		var newHash = newSettings.GetHashCode();
		if ( newHash == _lastSettingsHash )
			return;

		// Mark all tiles as needing regeneration (keeps old content visible)
		foreach ( var tile in Tiles.Values )
		{
			tile.IsPopulated = false;
		}

		Settings = newSettings;
		_lastSettingsHash = newHash;
	}

	public List<ClutterGenerationJob> UpdateTiles( Vector3 center )
	{
		_pendingJobs.Clear();
		if ( !Settings.IsValid )
			return _pendingJobs;

		var centerTile = WorldToTile( center );
		_activeCoords.Clear();
		var jobs = _pendingJobs;

		for ( int x = -Settings.Clutter.TileRadius; x <= Settings.Clutter.TileRadius; x++ )
			for ( int y = -Settings.Clutter.TileRadius; y <= Settings.Clutter.TileRadius; y++ )
			{
				var coord = new Vector2Int( centerTile.x + x, centerTile.y + y );
				_activeCoords.Add( coord );

				// Get or create tile
				if ( !Tiles.TryGetValue( coord, out var tile ) )
				{
					tile = new ClutterTile
					{
						Coordinates = coord,
						Bounds = GetTileBounds( coord ),
						SeedOffset = Settings.RandomSeed
					};
					Tiles[coord] = tile;
				}

				// Queue job if not populated
				if ( !tile.IsPopulated )
				{
					jobs.Add( new ClutterGenerationJob
					{
						Clutter = Settings.Clutter,
						Parent = ParentObject,
						Bounds = tile.Bounds,
						Seed = Settings.RandomSeed,
						Ownership = ClutterOwnership.GridSystem,
						Layer = this,
						Tile = tile
					} );
				}
			}

		// Remove out-of-range tiles
		_coordsToRemove.Clear();
		foreach ( var coord in Tiles.Keys )
			if ( !_activeCoords.Contains( coord ) ) _coordsToRemove.Add( coord );

		foreach ( var coord in _coordsToRemove )
		{
			if ( Tiles.Remove( coord, out var tile ) )
			{
				GridSystem?.RemovePendingTile( tile );
				tile.Destroy();
				ClearTileModelInstances( coord );
			}
		}
		if ( _coordsToRemove.Count > 0 ) _dirty = true;

		return jobs;
	}

	public void OnTilePopulated( ClutterTile tile )
	{
		_dirty = true;
	}

	/// <summary>
	/// Rebuilds batches if the instance set changed. LOD is GPU-side, so this ignores camera movement.
	/// </summary>
	public void RebuildIfDirty()
	{
		if ( _dirty )
			RebuildBatches();
	}

	/// <summary>
	/// Clears model instances and collision bodies for a specific tile coordinate.
	/// Marks every model that had instances in this tile as dirty so its merged batch is rebuilt.
	/// </summary>
	public void ClearTileModelInstances( Vector2Int tileCoord )
	{
		if ( ModelInstancesByTile.TryGetValue( tileCoord, out var instances ) )
		{
			foreach ( var instance in instances )
			{
				var model = instance.Entry?.Model;
				if ( model != null )
					_dirtyModels.Add( model );
			}

			ModelInstancesByTile.Remove( tileCoord );
			_dirty = true;
		}

		RemoveBodies( tileCoord );
	}

	/// <summary>
	/// </summary>
	public void AddModelInstance( Vector2Int tileCoord, ClutterInstance instance )
	{
		if ( instance.Entry?.Model == null )
			return;

		if ( !ModelInstancesByTile.TryGetValue( tileCoord, out var instances ) )
		{
			instances = [];
			ModelInstancesByTile[tileCoord] = instances;
		}

		instances.Add( instance );

		_dirtyModels.Add( instance.Entry.Model );
		_dirty = true;

		TryCreateBody( tileCoord, instance );
	}

	/// <summary>
	/// Populates this layer from a clutter storage, creating render batches and collision
	/// bodies for every stored instance. Shared by the painted and volume rebuild paths.
	/// </summary>
	public void PopulateFromStorage( ClutterGridSystem.ClutterStorage storage )
	{
		ClearAllTiles();

		if ( storage == null )
			return;

		foreach ( var modelPath in storage.ModelPaths )
		{
			var model = ResourceLibrary.Get<Model>( modelPath );
			if ( model == null ) continue;

			foreach ( var instance in storage.GetInstances( modelPath ) )
			{
				AddModelInstance( Vector2Int.Zero, new ClutterInstance
				{
					Transform = new Transform( instance.Position, instance.Rotation, instance.Scale ),
					Entry = new ClutterEntry { Model = model }
				} );
			}
		}

		// Painted layers rebuild everything up front, no streaming coalesce needed.
		RebuildAllDirtyModels();
	}

	/// <summary>
	/// Creates a static collision body for an instance (if its model has physics) and tracks it by tile.
	/// </summary>
	private void TryCreateBody( Vector2Int tileCoord, ClutterInstance instance )
	{
		var model = instance.Entry?.Model;
		if ( model?.Physics?.Parts.Count is not > 0 )
			return;

		var scene = ParentObject?.Scene ?? GridSystem?.Scene;
		if ( scene == null )
			return;

		var body = ClutterGenerationJob.CreateStaticBodyForVolume( model, instance.Transform, scene );
		if ( body == null )
			return;

		if ( !_bodiesByTile.TryGetValue( tileCoord, out var bodies ) )
		{
			bodies = [];
			_bodiesByTile[tileCoord] = bodies;
		}

		bodies.Add( body );
	}

	/// <summary>
	/// Removes all collision bodies tracked for a tile coordinate.
	/// </summary>
	private void RemoveBodies( Vector2Int tileCoord )
	{
		if ( !_bodiesByTile.Remove( tileCoord, out var bodies ) )
			return;

		foreach ( var body in bodies )
			if ( body.IsValid() ) body.Remove();
	}

	/// <summary>
	/// Rebuilds up to <see cref="MaxDirtyModelsPerRebuild"/> merged batches per call. Each rebuild
	/// walks all live tiles once, gathers the target model's transforms into a scratch list, and
	/// performs a single <see cref="ClutterBatchSceneObject.SetInstances"/> upload. Many dirty tiles
	/// on the same model collapse into one command-list rebuild here — that's the coalescing win
	/// that replaces the fork's per-tile batches without re-introducing the 07-03 frame spike.
	/// </summary>
	public void RebuildBatches()
	{
		var scene = ParentObject?.Scene ?? GridSystem?.Scene;
		if ( scene?.SceneWorld == null ) { _dirty = false; return; }

		if ( _dirtyModels.Count == 0 )
		{
			_dirty = false;
			return;
		}

		_rebuildScratch.Clear();
		var budget = 0;
		foreach ( var model in _dirtyModels )
		{
			_rebuildScratch.Add( model );
			if ( ++budget >= MaxDirtyModelsPerRebuild )
				break;
		}

		foreach ( var model in _rebuildScratch )
		{
			RebuildModelBatch( scene, model );
			_dirtyModels.Remove( model );
		}

		_dirty = _dirtyModels.Count > 0;
	}

	private void RebuildAllDirtyModels()
	{
		var scene = ParentObject?.Scene ?? GridSystem?.Scene;
		if ( scene?.SceneWorld == null ) { _dirtyModels.Clear(); _dirty = false; return; }

		_rebuildScratch.Clear();
		foreach ( var model in _dirtyModels )
			_rebuildScratch.Add( model );

		foreach ( var model in _rebuildScratch )
			RebuildModelBatch( scene, model );

		_dirtyModels.Clear();
		_dirty = false;
	}

	private void RebuildModelBatch( Scene scene, Model model )
	{
		if ( model == null )
			return;

		_transformScratch.Clear();

		foreach ( var (_, instances) in ModelInstancesByTile )
		{
			foreach ( var instance in instances )
			{
				if ( ReferenceEquals( instance.Entry?.Model, model ) )
					_transformScratch.Add( instance.Transform );
			}
		}

		if ( _transformScratch.Count == 0 )
		{
			// Model has no live tiles left (radius shrink or full clear) — drop the batch so
			// the merged draw stops replaying stale instances.
			if ( _batches.Remove( model, out var toDelete ) )
				toDelete.Delete();
			return;
		}

		if ( !_batches.TryGetValue( model, out var batch ) )
		{
			batch = new ClutterBatchSceneObject( scene.SceneWorld, model );
			_batches[model] = batch;
		}

		// [PERF-OK: counter for clutter_stats — reconvergence patch T5]
		batch.SetInstances( _transformScratch );
		ClutterGridSystem.s_batchRebuilds++;
	}

	public void ClearAllTiles()
	{
		foreach ( var tile in Tiles.Values )
		{
			GridSystem?.RemovePendingTile( tile );
			tile.Destroy();
		}

		Tiles.Clear();
		ModelInstancesByTile.Clear();

		foreach ( var coord in _bodiesByTile.Keys.ToList() )
			RemoveBodies( coord );

		foreach ( var batch in _batches.Values )
			batch.Delete();

		_batches.Clear();
		_dirtyModels.Clear();
		_dirty = false;
	}

	/// <summary>
	/// Invalidates the tile at the given world position, causing it to regenerate.
	/// </summary>
	public void InvalidateTile( Vector3 worldPosition )
	{
		var coord = WorldToTile( worldPosition );
		if ( Tiles.TryGetValue( coord, out var tile ) )
		{
			GridSystem?.RemovePendingTile( tile );
			tile.Destroy();
			ClearTileModelInstances( coord );
			_dirty = true;
		}
	}

	/// <summary>
	/// Invalidates all tiles that intersect the given bounds, causing them to regenerate.
	/// </summary>
	public void InvalidateTilesInBounds( BBox bounds )
	{
		var minTile = WorldToTile( bounds.Mins );
		var maxTile = WorldToTile( bounds.Maxs );

		for ( int x = minTile.x; x <= maxTile.x; x++ )
			for ( int y = minTile.y; y <= maxTile.y; y++ )
			{
				var coord = new Vector2Int( x, y );
				if ( Tiles.TryGetValue( coord, out var tile ) )
				{
					GridSystem?.RemovePendingTile( tile );
					tile.Destroy();
					ClearTileModelInstances( coord );
					_dirty = true;
				}
			}
	}

	private Vector2Int WorldToTile( Vector3 worldPos ) => new(
		(int)MathF.Floor( worldPos.x / Settings.Clutter.TileSize ),
		(int)MathF.Floor( worldPos.y / Settings.Clutter.TileSize )
	);

	private BBox GetTileBounds( Vector2Int coord ) => new(
		new Vector3( coord.x * Settings.Clutter.TileSize, coord.y * Settings.Clutter.TileSize, -TileHeight ),
		new Vector3( (coord.x + 1) * Settings.Clutter.TileSize, (coord.y + 1) * Settings.Clutter.TileSize, TileHeight )
	);

	// -----------------------------------------------------------------------------------
	// Diagnostics — cheap snapshots for the clutter_stats ConCmd. No hot-path allocation.
	// -----------------------------------------------------------------------------------

	internal int BatchCount => _batches.Count;
	internal int TileCount => Tiles.Count;
	internal int PopulatedTileCount
	{
		get
		{
			int n = 0;
			foreach ( var tile in Tiles.Values )
				if ( tile.IsPopulated ) n++;
			return n;
		}
	}
	internal int DirtyModelCount => _dirtyModels.Count;
}
