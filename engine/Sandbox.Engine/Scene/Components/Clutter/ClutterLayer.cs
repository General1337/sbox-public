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
	/// Batches organized by model. LOD is computed on the GPU per view, so batches are keyed by model.
	/// </summary>
	private readonly record struct ClutterBatchKey( Model Model, bool CastShadows );

	private readonly Dictionary<ClutterBatchKey, ClutterBatchSceneObject> _batches = [];
	private sealed class IncrementalBatchState
	{
		public List<Transform> Slots { get; } = [];
		public int ActiveCount;
		public int Tombstones;
	}

	private readonly record struct TileBatchRange( ClutterBatchKey Key, int Start, int Count );
	private readonly Dictionary<ClutterBatchKey, IncrementalBatchState> _incrementalStates = [];
	private readonly Dictionary<Vector2Int, List<TileBatchRange>> _tileBatchRanges = [];
	private readonly HashSet<Vector2Int> _pendingIncrementalTiles = [];
	private readonly Dictionary<ClutterBatchKey, List<Transform>> _tileAppendScratch = [];
	private readonly List<ClutterBatchKey> _incrementalKeyScratch = [];
	private bool _incrementalInitialized;
	private bool _forceFullIncrementalRebuild;
	private int _idleCompactionFrames;
	private const int DeferredCompactionFrames = 120;

	private readonly Dictionary<ClutterBatchKey, List<Transform>> _instancesByModel = [];
	private readonly HashSet<ClutterBatchKey> _activeModels = [];
	private readonly List<ClutterBatchKey> _staleModels = [];

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
	private bool _dirty = false;

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
		_forceFullIncrementalRebuild = true;
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

		if ( _dirty && jobs.Count == 0 )
			RebuildBatches();

		return jobs;
	}

	public void OnTilePopulated( ClutterTile tile )
	{
		if ( UsesIncrementalStreaming )
			_pendingIncrementalTiles.Add( tile.Coordinates );
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
		if ( UsesIncrementalStreaming )
			RemoveIncrementalTile( tileCoord );
		ModelInstancesByTile.Remove( tileCoord );
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
			var model = Model.Load( modelPath );
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

		if ( instance.Entry?.EnablePhysics is false )
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
		if ( UsesIncrementalStreaming )
		{
			RebuildBatchesIncremental();
			return;
		}

		ResetIncrementalTracking();
		RebuildBatchesLegacy();
	}

	private void RebuildBatchesLegacy()
	{
		// Don't build batch list on headless. We only care about collisions.
		if ( Application.IsHeadless ) { _dirty = false; return; }

		var scene = ParentObject?.Scene ?? GridSystem?.Scene;
		if ( scene?.SceneWorld == null ) { _dirty = false; return; }

		foreach ( var list in _instancesByModel.Values )
			list.Clear();

		_activeModels.Clear();

		foreach ( var (tileCoord, instances) in ModelInstancesByTile )
		{
			foreach ( var instance in instances )
			{
				if ( instance.Entry?.Model == null ) continue;

				var key = new ClutterBatchKey( instance.Entry.Model, instance.Entry.CastShadows );
				_activeModels.Add( key );

				if ( !_instancesByModel.TryGetValue( key, out var list ) )
				{
					list = [];
					_instancesByModel[key] = list;
				}

				list.Add( instance.Transform );
			}
		}

		foreach ( var key in _activeModels )
		{
			if ( !_batches.TryGetValue( key, out var batch ) )
			{
				batch = new ClutterBatchSceneObject( scene.SceneWorld, key.Model, key.CastShadows );
				_batches[key] = batch;
			}

			batch.SetInstances( _instancesByModel[key] );
		}

		// Remove batches whose key no longer has any instances.
		_staleModels.Clear();
		foreach ( var key in _batches.Keys )
			if ( !_activeModels.Contains( key ) ) _staleModels.Add( key );

		foreach ( var key in _staleModels )
		{
			_batches[key].Delete();
			_batches.Remove( key );
		}

		_dirty = false;
	}

	private bool UsesIncrementalStreaming
	{
		get
		{
			if ( !ClutterGridSystem.IncrementalStreaming ) return false;
			var component = ParentObject?.Components.Get<ClutterComponent>();
			return component.IsValid() && component.Infinite;
		}
	}

	private void RebuildBatchesIncremental()
	{
		if ( Application.IsHeadless ) { _dirty = false; return; }
		var scene = ParentObject?.Scene ?? GridSystem?.Scene;
		if ( scene?.SceneWorld == null ) { _dirty = false; return; }

		if ( !_incrementalInitialized || _forceFullIncrementalRebuild )
		{
			RebuildIncrementalFromSource( scene, _forceFullIncrementalRebuild );
			return;
		}

		foreach ( var tileCoord in _pendingIncrementalTiles )
		{
			if ( _tileBatchRanges.ContainsKey( tileCoord ) || !ModelInstancesByTile.TryGetValue( tileCoord, out var instances ) )
			{
				_forceFullIncrementalRebuild = true;
				break;
			}

			BuildTileScratch( instances );
			var ranges = new List<TileBatchRange>( _tileAppendScratch.Count );
			foreach ( var (key, transforms) in _tileAppendScratch )
			{
				if ( transforms.Count == 0 ) continue;
				var state = GetOrCreateIncrementalState( scene, key );
				var start = state.Slots.Count;
				state.Slots.AddRange( transforms );
				state.ActiveCount += transforms.Count;
				var uploaded = _batches[key].UpdateInstancesIncremental( state.Slots, start );
				if ( uploaded < transforms.Count )
				{
					_forceFullIncrementalRebuild = true;
					break;
				}

				ranges.Add( new TileBatchRange( key, start, transforms.Count ) );
				ClutterGridSystem.s_uploadedRecords += uploaded;
				ClutterGridSystem.s_appendedRecords += transforms.Count;
			}

			if ( _forceFullIncrementalRebuild ) break;
			_tileBatchRanges[tileCoord] = ranges;
		}

		_pendingIncrementalTiles.Clear();
		if ( _forceFullIncrementalRebuild )
		{
			RebuildIncrementalFromSource( scene, fallback: true );
			return;
		}

		_dirty = false;
		_idleCompactionFrames = 0;
	}

	private void RebuildIncrementalFromSource( Scene scene, bool fallback )
	{
		_incrementalStates.Clear();
		_tileBatchRanges.Clear();
		_pendingIncrementalTiles.Clear();
		_activeModels.Clear();

		foreach ( var (tileCoord, instances) in ModelInstancesByTile )
		{
			BuildTileScratch( instances );
			var ranges = new List<TileBatchRange>( _tileAppendScratch.Count );
			foreach ( var (key, transforms) in _tileAppendScratch )
			{
				if ( transforms.Count == 0 ) continue;
				var state = GetOrCreateIncrementalState( scene, key );
				var start = state.Slots.Count;
				state.Slots.AddRange( transforms );
				state.ActiveCount += transforms.Count;
				ranges.Add( new TileBatchRange( key, start, transforms.Count ) );
				_activeModels.Add( key );
			}
			_tileBatchRanges[tileCoord] = ranges;
		}

		foreach ( var (key, state) in _incrementalStates )
		{
			var uploaded = _batches[key].UpdateInstancesIncremental( state.Slots, 0 );
			if ( uploaded < 0 )
			{
				Log.Warning( $"Clutter incremental full rebuild failed for {key.Model?.ResourcePath}; selecting legacy rebuild." );
				ResetIncrementalTracking();
				RebuildBatchesLegacy();
				ClutterGridSystem.s_incrementalFallbacks++;
				return;
			}
			ClutterGridSystem.s_uploadedRecords += uploaded;
		}

		_staleModels.Clear();
		foreach ( var key in _batches.Keys )
			if ( !_activeModels.Contains( key ) ) _staleModels.Add( key );
		foreach ( var key in _staleModels )
		{
			_batches[key].Delete();
			_batches.Remove( key );
		}

		_incrementalInitialized = true;
		_forceFullIncrementalRebuild = false;
		_dirty = false;
		_idleCompactionFrames = 0;
		if ( fallback ) ClutterGridSystem.s_incrementalFallbacks++;
		ClutterGridSystem.s_fullBatchRebuilds++;
	}

	private IncrementalBatchState GetOrCreateIncrementalState( Scene scene, ClutterBatchKey key )
	{
		if ( !_incrementalStates.TryGetValue( key, out var state ) )
		{
			state = new IncrementalBatchState();
			_incrementalStates[key] = state;
		}
		if ( !_batches.ContainsKey( key ) )
			_batches[key] = new ClutterBatchSceneObject( scene.SceneWorld, key.Model, key.CastShadows );
		return state;
	}

	private void BuildTileScratch( List<ClutterInstance> instances )
	{
		foreach ( var list in _tileAppendScratch.Values ) list.Clear();
		foreach ( var instance in instances )
		{
			if ( instance.Entry?.Model == null ) continue;
			var key = new ClutterBatchKey( instance.Entry.Model, instance.Entry.CastShadows );
			if ( !_tileAppendScratch.TryGetValue( key, out var list ) )
			{
				list = [];
				_tileAppendScratch[key] = list;
			}
			list.Add( instance.Transform );
		}
	}

	private void RemoveIncrementalTile( Vector2Int tileCoord )
	{
		if ( _pendingIncrementalTiles.Remove( tileCoord ) ) return;
		if ( !_incrementalInitialized || !_tileBatchRanges.Remove( tileCoord, out var ranges ) ) return;

		foreach ( var range in ranges )
		{
			if ( !_incrementalStates.TryGetValue( range.Key, out var state ) ||
				range.Start < 0 || range.Count <= 0 || range.Start + range.Count > state.Slots.Count ||
				state.ActiveCount < range.Count || !_batches.TryGetValue( range.Key, out var batch ) ||
				!batch.MarkInactive( range.Start, range.Count ) )
			{
				_forceFullIncrementalRebuild = true;
				continue;
			}

			state.ActiveCount -= range.Count;
			state.Tombstones += range.Count;
			ClutterGridSystem.s_inactiveRecords += range.Count;
			ClutterGridSystem.s_uploadedRecords += range.Count;
		}
		_dirty = true;
		_idleCompactionFrames = 0;
	}

	internal void CompactDeferredIfIdle()
	{
		if ( !UsesIncrementalStreaming || !_incrementalInitialized ) return;
		var tombstones = 0;
		foreach ( var state in _incrementalStates.Values ) tombstones += state.Tombstones;
		if ( tombstones == 0 ) { _idleCompactionFrames = 0; return; }
		if ( ++_idleCompactionFrames < DeferredCompactionFrames ) return;

		var scene = ParentObject?.Scene ?? GridSystem?.Scene;
		if ( scene?.SceneWorld == null ) return;
		RebuildIncrementalFromSource( scene, fallback: false );
		ClutterGridSystem.s_deferredCompactions++;
	}

	internal bool ValidateIncremental( out string detail )
	{
		if ( !UsesIncrementalStreaming || !_incrementalInitialized || _forceFullIncrementalRebuild )
		{
			detail = "candidate state unavailable or fallback latched";
			return false;
		}

		var expectedCounts = new Dictionary<ClutterBatchKey, int>();
		var lastEnds = new Dictionary<ClutterBatchKey, int>();
		var hash = new HashCode();

		foreach ( var (tileCoord, instances) in ModelInstancesByTile )
		{
			if ( !_tileBatchRanges.TryGetValue( tileCoord, out var ranges ) )
			{
				detail = $"missing ranges for tile {tileCoord}";
				return false;
			}

			BuildTileScratch( instances );
			var expectedRangeCount = 0;
			foreach ( var (key, transforms) in _tileAppendScratch )
			{
				if ( transforms.Count == 0 ) continue;
				expectedRangeCount++;
				var found = false;
				TileBatchRange range = default;
				foreach ( var candidate in ranges )
				{
					if ( candidate.Key != key ) continue;
					if ( found ) { detail = $"duplicate range for {tileCoord}"; return false; }
					found = true;
					range = candidate;
				}

				if ( !found || range.Count != transforms.Count ||
					!_incrementalStates.TryGetValue( key, out var state ) ||
					range.Start < 0 || range.Start + range.Count > state.Slots.Count )
				{
					detail = $"invalid range/count for tile {tileCoord}";
					return false;
				}

				if ( lastEnds.TryGetValue( key, out var lastEnd ) && range.Start < lastEnd )
				{
					detail = $"overlap/order failure for tile {tileCoord}";
					return false;
				}

				for ( int i = 0; i < transforms.Count; i++ )
				{
					var expected = transforms[i];
					var actual = state.Slots[range.Start + i];
					if ( !actual.Equals( expected ) )
					{
						detail = $"transform mismatch for tile {tileCoord} index {i}";
						return false;
					}
					hash.Add( expected );
				}

				lastEnds[key] = range.Start + range.Count;
				expectedCounts[key] = expectedCounts.GetValueOrDefault( key ) + range.Count;
			}

			if ( ranges.Count != expectedRangeCount )
			{
				detail = $"extra range for tile {tileCoord}";
				return false;
			}
		}

		foreach ( var (key, state) in _incrementalStates )
		{
			var expected = expectedCounts.GetValueOrDefault( key );
			if ( expected != state.ActiveCount || state.Slots.Count - state.Tombstones != state.ActiveCount )
			{
				detail = $"active/tombstone count mismatch for {key.Model?.ResourcePath}";
				return false;
			}
		}

		detail = $"tiles={ModelInstancesByTile.Count} keys={_incrementalStates.Count} active={expectedCounts.Values.Sum()} hash={hash.ToHashCode():x8}";
		return true;
	}

	internal (int Count, int Hash) GetOrderedSourceSignature()
	{
		var count = 0;
		var hash = new HashCode();
		foreach ( var (tileCoord, instances) in ModelInstancesByTile )
		{
			hash.Add( tileCoord );
			foreach ( var instance in instances )
			{
				if ( instance.Entry?.Model == null ) continue;
				hash.Add( instance.Entry.Model.ResourcePath );
				hash.Add( instance.Entry.CastShadows );
				hash.Add( instance.Transform );
				count++;
			}
		}
		return (count, hash.ToHashCode());
	}

	internal void ForceIncrementalFallbackForTest()
	{
		if ( !UsesIncrementalStreaming ) return;
		_forceFullIncrementalRebuild = true;
		_dirty = true;
		RebuildBatches();
	}

	private void ResetIncrementalTracking()
	{
		_incrementalStates.Clear();
		_tileBatchRanges.Clear();
		_pendingIncrementalTiles.Clear();
		_incrementalInitialized = false;
		_forceFullIncrementalRebuild = false;
		_idleCompactionFrames = 0;
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

		// Copied out first, RemoveBodies mutates the dictionary.
		_coordsToRemove.Clear();
		foreach ( var coord in _bodiesByTile.Keys )
			_coordsToRemove.Add( coord );

		foreach ( var coord in _coordsToRemove )
			RemoveBodies( coord );

		foreach ( var batch in _batches.Values )
			batch.Delete();

		_batches.Clear();
		_instancesByModel.Clear();
		ResetIncrementalTracking();
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
