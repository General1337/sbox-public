using Sandbox.Utility;
using static Sandbox.Component;

namespace Sandbox;

public partial class Scene : GameObject
{
	// Generic allocation seams for performance investigations. Values are stored in the existing
	// timing registry as KiB so game projects can read them by name without taking a dependency on
	// fork-only API. These are main-thread bytes allocated inside the named stage window; the frame
	// ledger compares their sum with PerformanceStats.BytesAllocated to expose worker/unattributed
	// allocation instead of silently charging it to whichever main-thread stage happened to run.
	static long AllocationCheckpoint() => GC.GetAllocatedBytesForCurrentThread();

	static void RecordAllocationKb( string name, ref long checkpoint )
	{
		var now = GC.GetAllocatedBytesForCurrentThread();
		var bytes = Math.Max( 0L, now - checkpoint );
		checkpoint = now;
		PerformanceStats.Timings.Get( name ).AddMilliseconds( bytes / 1024.0 );
	}

	FixedUpdate fixedUpdate = new FixedUpdate();
	public bool IsFixedUpdate { get; private set; }

	public float FixedDelta => (float)fixedUpdate.Delta;

	[Obsolete( "Moved to Sandbox.ProjectSettings.PhysicsSettings" )] public float FixedUpdateFrequency { get; set; } = 50.0f;
	[Obsolete( "Moved to Sandbox.ProjectSettings.PhysicsSettings" )] public int MaxFixedUpdates { get; set; } = 5;
	[Obsolete( "Moved to Sandbox.ProjectSettings.PhysicsSettings" )] public int PhysicsSubSteps { get; set; }
	[Obsolete( "Unused. Animation is always threaded." )] public bool ThreadedAnimation { get; set; } = true;
	[Obsolete( "Moved to Sandbox.ProjectSettings.PhysicsSettings" )] public bool UseFixedUpdate { get; set; }

	[Property, Range( 0, 1 )] public float TimeScale { get; set; } = 1.0f;

	/// <summary>
	/// The update loop will turn certain settings on
	/// Here we turn them to their defaults.
	/// </summary>
	void PreTickReset()
	{
		// Forward our preference to the Scene's PhysicsWorld
		// Access the backing fields - ticking shouldn't force these worlds to exist
		if ( _physicsWorld.IsValid() )
		{
			_physicsWorld.SubSteps = Sandbox.ProjectSettings.Physics.SubSteps;
		}

		if ( _sceneWorld is not null )
		{
			_sceneWorld.GradientFog.Enabled = false;
		}
	}

	double estimatedServerTime;

	/// <summary>
	/// Update the current time from the host
	/// </summary>
	internal void UpdateTimeFromHost( double time )
	{
		estimatedServerTime = time;

		if ( TimeNow == 0f )
		{
			TimeNow = time;
		}
	}

	internal double TimeNow { get; private set; }
	internal double TimeDelta { get; private set; } = 0.1;

	public void EditorTick( float timeNow, float timeDelta )
	{
		// Only tick here if we're an editor scene
		// The game will tick a game scene!
		if ( !IsEditor || !IsValid )
			return;

		TimeNow = timeNow;
		TimeDelta = timeDelta;

		using var timeScope = Time.Scope( TimeNow, TimeDelta );
		using var gizmoScope = gizmoInstance.Push();

		SharedTick();

		using ( PerformanceStats.Timings.NavMesh.Scope() )
		{
			Nav_Update();
		}
	}

	public void EditorDraw()
	{
		DebugDraw();
		DrawGizmos();
	}

	/// <summary>
	/// Run OnStart on all components that haven't had OnStart called yet
	/// </summary>
	internal void RunPendingStarts()
	{
		foreach ( var c in pendingStartComponents.EnumerateLocked() )
		{
			if ( !c.IsValid() )
				continue;

			if ( !PerformanceTailAttribution.Enabled || IsEditor )
			{
				c.InternalOnStart();
				continue;
			}

			var tail = PerformanceTailAttribution.Begin();
			c.InternalOnStart();
			PerformanceTailAttribution.End( tail, "component.start", PerformanceTailAttribution.OwnerForType( c.GetType() ), c.GameObject?.Name );
		}
	}

	internal void InternalUpdate()
	{
		RunPendingStarts();

		Signal( GameObjectSystem.Stage.Interpolation );

		foreach ( var c in updateComponents.EnumerateLocked( true ) )
		{
			if ( !PerformanceTailAttribution.Enabled || IsEditor )
			{
				c.InternalUpdate();
				continue;
			}

			var tail = PerformanceTailAttribution.Begin();
			c.InternalUpdate();
			PerformanceTailAttribution.End( tail, "component.update", PerformanceTailAttribution.OwnerForType( c.GetType() ), c.GameObject?.Name );
		}
	}

	List<CameraComponent> _cameraViewScratch = new();

	/// <summary>
	/// Composes every enabled camera's view - the one point in the frame where the camera moves.
	/// Runs after Update and bone merging, before PreRender.
	/// </summary>
	void UpdateCameraViews()
	{
		// Snapshot - a modifier could add or remove cameras while we iterate.
		_cameraViewScratch.Clear();
		_cameraViewScratch.AddRange( Cameras );

		// One sorted modifier set serves every camera this tick.
		var modifiers = IsEditor ? null : CameraComponent.GatherModifiers( this );

		foreach ( var camera in _cameraViewScratch )
		{
			if ( !camera.IsValid() || !camera.Active )
				continue;

			camera.ComposeView( modifiers );
		}
	}

	List<IRenderThread> renderThreadEventTargets = new();

	/// <summary>
	/// Screen panels in draw order, captured in PreRender(). Sorted here so the render
	/// thread neither walks the object index nor sorts every frame.
	/// </summary>
	internal List<ScreenPanel> renderScreenPanels = new();

	internal void PreRender()
	{
		// Snapshot IRenderThread components on the main thread so the render thread
		// can iterate without racing against concurrent Add/Remove in objectIndex.
		renderThreadEventTargets.Clear();
		GetAll( renderThreadEventTargets );

		renderScreenPanels.Clear();
		GetAll( renderScreenPanels );
		SortScreenPanels( renderScreenPanels );

		foreach ( var c in preRenderComponents.EnumerateLocked() )
		{
			if ( !PerformanceTailAttribution.Enabled || IsEditor )
			{
				c.OnPreRenderInternal();
				continue;
			}

			var tail = PerformanceTailAttribution.Begin();
			c.OnPreRenderInternal();
			PerformanceTailAttribution.End( tail, "component.prerender", PerformanceTailAttribution.OwnerForType( c.GetType() ), c.GameObject?.Name );
		}
	}

	/// <summary>
	/// Sort by ZIndex, keeping scene order for equal ZIndex so overlapping panels draw
	/// in a predictable order. List.Sort isn't stable; a stable insertion sort is fine
	/// for the handful of panels a scene has and doesn't allocate.
	/// </summary>
	static void SortScreenPanels( List<ScreenPanel> panels )
	{
		for ( int i = 1; i < panels.Count; i++ )
		{
			var panel = panels[i];
			int j = i - 1;

			while ( j >= 0 && panels[j].ZIndex > panel.ZIndex )
			{
				panels[j + 1] = panels[j];
				j--;
			}

			panels[j + 1] = panel;
		}
	}

	static Superluminal _updateTimer = new Superluminal( "Scene.Update", Color.Cyan );
	static Superluminal _preRenderTimer = new Superluminal( "Scene.PreRender", Color.Cyan );
	static Superluminal _signalUpdateBones = new Superluminal( "Signal.UpdateBones", Color.Cyan );
	static Superluminal _signalStarthUpdate = new Superluminal( "Signal.StartUpdate", Color.Cyan );
	static Superluminal _signalFinishUpdate = new Superluminal( "Signal.FinishUpdate", Color.Cyan );

	private void FixedUpdate()
	{
		if ( !ProjectSettings.Physics.UseFixedUpdate )
		{
			InternalFixedUpdate();
		}
		else
		{
			fixedUpdate.Frequency = ProjectSettings.Physics.FixedUpdateFrequency;

			IsFixedUpdate = true;
			fixedUpdate.Run( InternalFixedUpdate, Time.NowDouble, ProjectSettings.Physics.MaxFixedUpdates );
			IsFixedUpdate = false;
		}
	}

	/// <summary>
	/// This is called in EditorTick and GameTick. It's only called in EditorTick if we're actually
	/// an editor scene.
	/// </summary>
	void SharedTick()
	{
		var alloc = AllocationCheckpoint();
		Scene.RunEvent<ISceneStage>( x => x.Start() );
		RecordAllocationKb( "AllocKB.Scene.StartEvent", ref alloc );

		if ( !IsEditor )
		{
			using ( PerformanceStats.Timings.Network.Scope() )
			{
				SceneNetworkUpdate();
			}
			RecordAllocationKb( "AllocKB.Scene.Network", ref alloc );
		}

		// no profile scope, profile inside instead
		{
			FixedUpdate();
		}
		alloc = AllocationCheckpoint(); // fixed ticks publish their own detailed allocation windows

		{
			ProcessDeletes();
			RecordAllocationKb( "AllocKB.Update.DeletesBefore", ref alloc );

			using ( _signalStarthUpdate.Start() )
			{
				Signal( GameObjectSystem.Stage.StartUpdate );
			}
			RecordAllocationKb( "AllocKB.Update.StartListeners", ref alloc );

			using ( _updateTimer.Start() )
			using ( PerformanceStats.Timings.Update.Scope() )
			{
				PreTickReset();
				InternalUpdate();
			}
			RecordAllocationKb( "AllocKB.Update.Components", ref alloc );

			using ( _signalUpdateBones.Start() )
			{
				Signal( GameObjectSystem.Stage.UpdateBones );
			}
			RecordAllocationKb( "AllocKB.Update.Bones", ref alloc );

			// The cameras' views become final here - PreRender and rendering read a settled camera.
			UpdateCameraViews();
			RecordAllocationKb( "AllocKB.Update.Camera", ref alloc );

			if ( !Application.IsHeadless )
			{
				using ( _preRenderTimer.Start() )
				using ( PerformanceStats.Timings.Render.Scope() )
				{
					PreRender();
				}
			}
			RecordAllocationKb( "AllocKB.Update.PreRender", ref alloc );

			ProcessDeletes();
			RecordAllocationKb( "AllocKB.Update.DeletesAfter", ref alloc );

			using ( _signalFinishUpdate.Start() )
			{
				Signal( GameObjectSystem.Stage.FinishUpdate );
			}
			RecordAllocationKb( "AllocKB.Update.FinishListeners", ref alloc );
		}

		Scene.RunEvent<ISceneStage>( x => x.End() );
		RecordAllocationKb( "AllocKB.Scene.EndEvent", ref alloc );

		if ( !IsEditor )
		{
			using ( PerformanceStats.Timings.Async.Scope() )
			{
				SyncContext.FrameStage.PreRender.Trigger();
			}
			RecordAllocationKb( "AllocKB.Scene.PreRenderTrigger", ref alloc );
		}

	}

	internal void SyncServerTime()
	{
		if ( Networking.IsHost ) return;

		// Estimate what the server time is now
		estimatedServerTime += TimeDelta;

		// How far off are we?
		var timeDifference = Math.Abs( TimeNow - estimatedServerTime );

		// If the time difference is large, snap to it
		if ( timeDifference > 0.25f )
		{
			TimeNow = estimatedServerTime;
			return;
		}

		// Smoothly lerp to the correct time
		// The larger the difference, the faster we lerp
		TimeNow = MathX.Lerp( TimeNow, estimatedServerTime, RealTime.Delta + timeDifference );
	}

	internal void UpdateTime( double delta )
	{
		if ( delta <= 0.0 ) return;

		TimeDelta = delta * TimeScale;
		TimeNow += TimeDelta;
	}

	public void GameTick( double timeDelta = 0.1 )
	{
		UpdateTime( timeDelta );

		if ( Camera is not null )
		{
			gizmoInstance.Input.Camera = Camera.SceneCamera;

			UpdateDefaultListener();
		}

		using var timeScope = Time.Scope( TimeNow, TimeDelta );
		using var gizmoScope = gizmoInstance?.Push();

		using ( PerformanceStats.Timings.Async.Scope() )
		{
			SyncContext.FrameStage.Update.Trigger();
		}

		if ( IsLoading )
			return;

		if ( Game.IsPaused )
			return;

		SharedTick();

		// If we started loading, then try to run a full tick again - because we might
		// be able to immediately finish the load and be ready to render propertly on
		// the next render!
		if ( IsLoading )
		{
			GameTick();
		}

	}

	Input.Context FixedUpdateInputContext { get; set; } = Input.Context.Create( "Scene.FixedUpdate" );

	static Superluminal _fixedUpdateTimer = new Superluminal( "Scene.FixedUpdate", Color.Cyan );
	static Superluminal _processDeletesTimer = new Superluminal( "ProcessDeletes", Color.Orange );

	internal void InternalFixedUpdate()
	{
		var alloc = AllocationCheckpoint();
		FixedUpdateInputContext.Flip();
		using var _ = FixedUpdateInputContext.Push();

		using ( _fixedUpdateTimer.Start() )
		{
			Signal( GameObjectSystem.Stage.StartFixedUpdate );
			RecordAllocationKb( "AllocKB.Fixed.StartListeners", ref alloc );

			// All components that have not had OnStart() called yet
			// ?: Is there a chance this gets called before Update()? Should this even be here

			RunPendingStarts();
			RecordAllocationKb( "AllocKB.Fixed.PendingStarts", ref alloc );

			using ( PerformanceStats.Timings.Update.Scope() )
			{
				foreach ( var c in fixedUpdateComponents.EnumerateLocked() )
				{
					if ( !c.IsValid() )
						continue;

					if ( !PerformanceTailAttribution.Enabled || IsEditor )
					{
						c.InternalFixedUpdate();
						continue;
					}

					var tail = PerformanceTailAttribution.Begin();
					c.InternalFixedUpdate();
					PerformanceTailAttribution.End( tail, "component.fixed", PerformanceTailAttribution.OwnerForType( c.GetType() ), c.GameObject?.Name );
				}
			}
			RecordAllocationKb( "AllocKB.Fixed.Components", ref alloc );

			Signal( GameObjectSystem.Stage.PhysicsStep );
			RecordAllocationKb( "AllocKB.Fixed.Physics", ref alloc );

			if ( !IsEditor )
			{
				using ( PerformanceStats.Timings.NavMesh.Scope() )
				{
					Nav_Update();
				}
				RecordAllocationKb( "AllocKB.Fixed.NavMesh", ref alloc );
			}

			using ( _processDeletesTimer.Start() )
			{
				ProcessDeletes();
			}
			RecordAllocationKb( "AllocKB.Fixed.Deletes", ref alloc );

			using ( PerformanceStats.Timings.Async.Scope() )
			{
				SyncContext.FrameStage.FixedUpdate.Trigger();
			}
			RecordAllocationKb( "AllocKB.Fixed.AsyncTrigger", ref alloc );

			Signal( GameObjectSystem.Stage.FinishFixedUpdate );
			RecordAllocationKb( "AllocKB.Fixed.FinishListeners", ref alloc );
		}

		Connection.ClearFixedUpdateContextInput();
		RecordAllocationKb( "AllocKB.Fixed.InputClear", ref alloc );
	}
}
