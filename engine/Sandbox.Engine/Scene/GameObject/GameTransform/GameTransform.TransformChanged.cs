namespace Sandbox;

public partial class GameTransform
{
	/// <summary>
	/// Called when the transform is changed
	/// </summary>
	public Action OnTransformChanged;

	/// <summary>
	/// I need to know the root transform that actually changed
	/// </summary>
	internal Action<GameTransform> OnTransformChangedInternal;

	/// <summary>
	/// Root-aware callbacks that remain active for local and intermediate changes, but are skipped
	/// when the exact propagation root owns native scene parenting.
	/// </summary>
	internal Action<GameTransform> OnTransformChangedExceptNativeParentRoot;

	/// <summary>
	/// True while this transform is receiving recursive interpolation propagation from an exact
	/// native-parent root. Physics bodies remain simulation-owned during this visual-only pass.
	/// </summary>
	internal bool InsideNativeParentTargetPropagation { get; private set; }

	/// <summary>
	/// Our transform has changed, which means our children transforms changed too
	/// tell them all.
	/// </summary>
	internal unsafe void TransformChanged( bool useTargetLocal = false, GameTransform root = null,
		bool nativeParentRoot = false, bool nativeParentTargetRoot = false )
	{
		var topLevel = root is null;
		var ownsCapitalProfile = CapitalPropagationDiagnostics.Enter( this, useTargetLocal, topLevel );
		try
		{
			root ??= this;
			if ( topLevel )
			{
				nativeParentRoot = SceneModelTransformParentSystem.SkipRootCallbacks
					&& GameObject.Tags.Has( SceneModelTransformParentSystem.NativeParentTag, includeAncestors: false );
				nativeParentTargetRoot = useTargetLocal
					&& GameObject.Tags.Has( SceneModelTransformParentSystem.NativeParentTag, includeAncestors: false );
			}

			_worldCached = default;
			_worldInterpCached = default;

			InsideChangeCallback = useTargetLocal;
			InsideNativeParentTargetPropagation = nativeParentTargetRoot;

			try
			{
				var publicCallbacks = OnTransformChanged;
				var publicStart = CapitalPropagationDiagnostics.BeginCallbacks( publicCallbacks );
				publicCallbacks?.Invoke();
				CapitalPropagationDiagnostics.EndCallbacks( publicCallbacks, publicStart, false );

				if ( !nativeParentRoot )
				{
					var nativeParentCallbacks = OnTransformChangedExceptNativeParentRoot;
					var nativeParentStart = CapitalPropagationDiagnostics.BeginCallbacks( nativeParentCallbacks );
					nativeParentCallbacks?.Invoke( root );
					CapitalPropagationDiagnostics.EndCallbacks( nativeParentCallbacks, nativeParentStart, true );
				}

				var internalCallbacks = OnTransformChangedInternal;
				var internalStart = CapitalPropagationDiagnostics.BeginCallbacks( internalCallbacks );
				internalCallbacks?.Invoke( root );
				CapitalPropagationDiagnostics.EndCallbacks( internalCallbacks, internalStart, true );
			}
			finally
			{
				InsideChangeCallback = false;
				InsideNativeParentTargetPropagation = false;
			}

			var data = new TransformChangedData
			{
				Root = root,
				NativeParentRoot = nativeParentRoot,
				NativeParentTargetRoot = nativeParentTargetRoot
			};
			GameObject.ForEachChildFast( "TransformChanged", true, &TransformChangedCallback, ref data );
		}
		finally
		{
			CapitalPropagationDiagnostics.Exit( ownsCapitalProfile );
		}
	}

	// empty, no data to pass
	struct TransformChangedData
	{
		public GameTransform Root;
		public bool NativeParentRoot;
		public bool NativeParentTargetRoot;
	}

	static void TransformChangedCallback( GameObject c, ref TransformChangedData data )
	{
		if ( !c.Transform.IsFollowingParent() )
			return;

		c.Transform.TransformChanged( false, data.Root, data.NativeParentRoot, data.NativeParentTargetRoot );
	}
}
