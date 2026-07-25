using static Sandbox.Clutter.ClutterGridSystem;

namespace Sandbox.Clutter;

/// <summary>
/// Defines who owns the generated clutter instances.
/// </summary>
enum ClutterOwnership
{
	/// <summary>
	/// Component owns instances. Models stored in component's Storage, prefabs saved with scene.
	/// Used for volume mode.
	/// </summary>
	Component,

	/// <summary>
	/// GridSystem owns instances. Prefabs are unsaved/hidden, tiles manage cleanup.
	/// Used for infinite streaming mode.
	/// </summary>
	GridSystem
}

/// <summary>
/// Unified job for clutter generation.
/// </summary>
class ClutterGenerationJob
{
	/// <summary>
	/// The clutter definition containing entries and scatterer.
	/// </summary>
	public required ClutterDefinition Clutter { get; init; }

	/// <summary>
	/// Parent GameObject for spawned prefabs.
	/// </summary>
	public required GameObject Parent { get; init; }

	/// <summary>
	/// Bounds to scatter within.
	/// </summary>
	public required BBox Bounds { get; init; }

	/// <summary>
	/// Random seed for deterministic generation.
	/// </summary>
	public required int Seed { get; init; }

	/// <summary>
	/// Who owns the generated instances.
	/// </summary>
	public required ClutterOwnership Ownership { get; init; }

	/// <summary>
	/// Layer for batched model rendering.
	/// </summary>
	public ClutterLayer Layer { get; init; }

	/// <summary>
	/// Tile data for infinite mode (null for volume mode).
	/// </summary>
	public ClutterTile Tile { get; init; }

	/// <summary>
	/// Storage for component-owned model instances
	/// </summary>
	public ClutterStorage Storage { get; init; }

	/// <summary>
	/// Optional callback when job completes (for volume mode progress tracking).
	/// </summary>
	public Action OnComplete { get; init; }

	public BBox? LocalBounds { get; init; }
	public Transform? VolumeTransform { get; init; }

	private bool _started;
	private bool _completed;
	private bool _completeNotified;
	private int _resolvedSeed;
	private TerrainMaterialScatterer.TerrainMaterialScatterWork _terrainMaterialWork;

	/// <summary>
	/// Execute the generation job.
	/// </summary>
	public void Execute()
	{
		ExecuteBudgeted( long.MaxValue );
	}

	/// <summary>
	/// Executes this job until it completes or the supplied Stopwatch timestamp deadline is reached.
	/// Returns true when the job is complete and can be removed from the queue.
	/// </summary>
	public bool ExecuteBudgeted( long deadlineTimestamp )
	{
		if ( _completed )
			return true;

		try
		{
			if ( !Parent.IsValid() )
			{
				_completed = true;
				return true;
			}

			BeginGeneration();

			if ( _terrainMaterialWork is not null )
			{
				if ( !_terrainMaterialWork.ExecuteUntil( deadlineTimestamp ) )
					return false;

				FinishGeneration( _terrainMaterialWork.Instances );
				return true;
			}

			FinishGeneration( GenerateInstancesSynchronous() );
			return true;
		}
		catch
		{
			_completed = true;
			throw;
		}
		finally
		{
			if ( _completed )
				NotifyComplete();
		}
	}

	private void BeginGeneration()
	{
		if ( _started )
			return;

		_started = true;
		_resolvedSeed = Seed;

		if ( Tile != null )
			_resolvedSeed = Scatterer.GenerateSeed( Tile.SeedOffset, Tile.Coordinates.x, Tile.Coordinates.y );

		if ( Tile != null
			&& !LocalBounds.HasValue
			&& !VolumeTransform.HasValue
			&& Clutter.Scatterer.HasValue
			&& Clutter.Scatterer.Value is TerrainMaterialScatterer terrainMaterialScatterer )
		{
			_terrainMaterialWork = terrainMaterialScatterer.CreateStreamingWork( Bounds, Clutter, _resolvedSeed, Parent.Scene );
		}
	}

	private List<ClutterInstance> GenerateInstancesSynchronous()
	{
		return Clutter.Scatterer.HasValue
			? Clutter.Scatterer.Value.Scatter( Bounds, Clutter, _resolvedSeed, Parent.Scene )
			: null;
	}

	private void FinishGeneration( List<ClutterInstance> instances )
	{
		if ( _completed )
			return;

		if ( LocalBounds.HasValue && VolumeTransform.HasValue )
		{
			var volumeTransform = VolumeTransform.Value;
			var localBounds = LocalBounds.Value;
			instances?.RemoveAll( i => !localBounds.Contains( volumeTransform.PointToLocal( i.Transform.Position ) ) );
		}

		if ( Tile != null )
		{
			Tile.Destroy();
			Layer?.ClearTileModelInstances( Tile.Coordinates );
		}

		if ( instances is { Count: > 0 } )
		{
			// ApplyEntryLocalScale is upstream's (absent at base 91762136, added by the 2026-07-25
			// sync). Upstream calls it inline in its monolithic Execute(); this fork refactored that
			// method into BeginGeneration/ExecuteBudgeted/FinishGeneration for deadline-chunked
			// streaming, so the call has to be re-seated HERE — the merge otherwise left the new
			// method defined but never invoked, silently dropping per-entry local scale.
			ApplyEntryLocalScale( instances );
			SpawnInstances( instances );
		}

		if ( Tile != null )
		{
			Tile.IsPopulated = true;
			Layer?.OnTilePopulated( Tile );
			ClutterGridSystem.s_tilesCompleted++;
		}

		_completed = true;
	}

	private void NotifyComplete()
	{
		if ( _completeNotified )
			return;

		_completeNotified = true;
		OnComplete?.Invoke();
	}

	private static void ApplyEntryLocalScale( List<ClutterInstance> instances )
	{
		for ( int i = 0; i < instances.Count; i++ )
		{
			var instance = instances[i];
			var localScale = instance.Entry?.LocalScale ?? 1f;
			if ( localScale == 1f )
				continue;

			var transform = instance.Transform;
			transform.Scale *= localScale;
			instance.Transform = transform;
			instances[i] = instance;
		}
	}

	internal static PhysicsBody CreateStaticBodyForVolume( Model model, Transform transform, Scene scene )
	{
		return CreateStaticBody( model, transform, scene );
	}

	private static PhysicsBody CreateStaticBody( Model model, Transform transform, Scene scene )
	{
		var world = scene?.PhysicsWorld;
		if ( world == null ) return null;

		var parts = model.Physics.Parts;
		var referenceTransform = parts.Count > 0 ? parts[0].Transform : Transform.Zero;
		var bodyTransform = transform.ToWorld( referenceTransform );
		var body = new PhysicsBody( world );
		body.BodyType = PhysicsBodyType.Static;
		body.Position = bodyTransform.Position;
		body.Rotation = bodyTransform.Rotation;

		var scaleOnly = new Transform( Vector3.Zero, Rotation.Identity, transform.Scale.x );
		foreach ( var part in parts )
		{
			var relativePart = referenceTransform.ToLocal( part.Transform );
			var partTransform = scaleOnly.ToWorld( relativePart );
			foreach ( var sphere in part.Spheres )
				body.AddSphereShape( partTransform.PointToWorld( sphere.Sphere.Center ), sphere.Sphere.Radius * partTransform.UniformScale ).Tags.Add( "clutter" );
			foreach ( var capsule in part.Capsules )
				body.AddCapsuleShape( partTransform.PointToWorld( capsule.Capsule.CenterA ), partTransform.PointToWorld( capsule.Capsule.CenterB ), capsule.Capsule.Radius * partTransform.UniformScale ).Tags.Add( "clutter" );
			foreach ( var hull in part.Hulls )
				body.AddShape( hull, partTransform ).Tags.Add( "clutter" );
			foreach ( var mesh in part.Meshes )
				body.AddShape( mesh, partTransform, false ).Tags.Add( "clutter" );
		}

		return body;
	}

	private void SpawnInstances( List<ClutterInstance> instances )
	{
		var isComponentOwned = Ownership == ClutterOwnership.Component;
		var tileCoord = Tile?.Coordinates ?? Vector2Int.Zero;

		using ( Parent.Scene.Push() )
		{
			foreach ( var instance in instances )
			{
				if ( instance.IsModel )
				{
					Layer?.AddModelInstance( tileCoord, instance );

					// Component ownership: also store in component's storage for persistence
					if ( isComponentOwned )
					{
						Storage.AddInstance(
							instance.Entry.Model.ResourcePath,
							instance.Transform.Position,
							instance.Transform.Rotation,
							instance.Transform.Scale.x
						);
					}

					continue;
				}

				if ( instance.Entry.Prefab == null )
					continue;

				var obj = instance.Entry.Prefab.Clone( instance.Transform, Parent.Scene );
				obj.Tags.Add( "clutter" );
				obj.SetParent( Parent );

				if ( !isComponentOwned )
				{
					obj.Flags |= GameObjectFlags.NotSaved;
					obj.Flags |= GameObjectFlags.Hidden;
					Tile?.AddObject( obj );
				}
			}
		}
	}
}
