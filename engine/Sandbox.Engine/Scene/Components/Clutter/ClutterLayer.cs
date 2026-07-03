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
	/// </summary>
	private Dictionary<Vector2Int, List<ClutterInstance>> ModelInstancesByTile { get; } = [];

	/// <summary>
	/// Batches organized by tile and model. Keeping streamed tiles separate avoids re-uploading
	/// the entire model instance list when a single infinite tile changes.
	/// </summary>
	private readonly Dictionary<(Vector2Int TileCoord, Model Model), ClutterBatchSceneObject> _batches = [];

	private readonly HashSet<Vector2Int> _dirtyTiles = [];
	private readonly Dictionary<Model, List<Transform>> _transformsByModelScratch = [];
	private readonly HashSet<Model> _activeModelsScratch = [];
	private readonly List<(Vector2Int TileCoord, Model Model)> _staleBatchKeys = [];

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
	private const int MaxDirtyTilesPerRebuild = 4;
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
	/// </summary>
	public void ClearTileModelInstances( Vector2Int tileCoord )
	{
		if ( ModelInstancesByTile.Remove( tileCoord ) )
		{
			_dirtyTiles.Add( tileCoord );
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

		_dirtyTiles.Add( tileCoord );
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

		RebuildBatches();
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

	public void RebuildBatches()
	{
		var scene = ParentObject?.Scene ?? GridSystem?.Scene;
		if ( scene?.SceneWorld == null ) { _dirty = false; return; }

		var rebuiltTiles = 0;
		foreach ( var tileCoord in _dirtyTiles.ToArray() )
		{
			RebuildTileBatches( scene, tileCoord );
			_dirtyTiles.Remove( tileCoord );

			rebuiltTiles++;
			if ( rebuiltTiles >= MaxDirtyTilesPerRebuild )
				break;
		}

		_dirty = _dirtyTiles.Count > 0;
	}

	private void RebuildTileBatches( Scene scene, Vector2Int tileCoord )
	{
		_transformsByModelScratch.Clear();
		_activeModelsScratch.Clear();

		if ( ModelInstancesByTile.TryGetValue( tileCoord, out var instances ) )
		{
			foreach ( var instance in instances )
			{
				var model = instance.Entry?.Model;
				if ( model == null )
					continue;

				_activeModelsScratch.Add( model );

				if ( !_transformsByModelScratch.TryGetValue( model, out var transforms ) )
				{
					transforms = [];
					_transformsByModelScratch[model] = transforms;
				}

				transforms.Add( instance.Transform );
			}
		}

		foreach ( var (model, transforms) in _transformsByModelScratch )
		{
			var key = (tileCoord, model);
			if ( !_batches.TryGetValue( key, out var batch ) )
			{
				batch = new ClutterBatchSceneObject( scene.SceneWorld, model );
				_batches[key] = batch;
			}

			batch.SetInstances( transforms );
		}

		_staleBatchKeys.Clear();
		foreach ( var key in _batches.Keys )
		{
			if ( key.TileCoord == tileCoord && !_activeModelsScratch.Contains( key.Model ) )
				_staleBatchKeys.Add( key );
		}

		foreach ( var key in _staleBatchKeys )
		{
			_batches[key].Delete();
			_batches.Remove( key );
		}
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
		_dirtyTiles.Clear();
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
}
