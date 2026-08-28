namespace Sandbox;

/// <summary>
/// Moves ordinary model scene objects with one native movement parent when their GameObject hierarchy
/// explicitly opts in through <see cref="NativeParentTag"/>. The managed GameObject hierarchy remains
/// authoritative for gameplay; this only removes redundant per-renderer root-motion uploads.
/// </summary>
internal sealed class SceneModelTransformParentSystem : GameObjectSystem<SceneModelTransformParentSystem>
{
	internal const string NativeParentTag = "native_scene_parent";

	[ConVar( "scene_model_native_parent_skip_root_callbacks", Help = "Skip parented ModelRenderer delegate dispatch for exact native-parent root propagation." )]
	internal static bool SkipRootCallbacks { get; set; } = true;

	[ConVar( "scene_model_native_parent_skip_rigidbody_target_callbacks", Help = "Skip welded child-body teleports during exact native-parent interpolation propagation." )]
	internal static bool SkipRigidbodyTargetCallbacks { get; set; } = true;

	sealed class ParentEntry
	{
		public readonly GameObject Root;
		public readonly SceneCustomObject Anchor;
		public int Children;

		public ParentEntry( Scene scene, GameObject root )
		{
			Root = root;
			Anchor = new SceneCustomObject( scene.SceneWorld )
			{
				RenderingEnabled = false,
				Transform = root.Transform.InterpolatedWorld
			};
			Root.Transform.OnTransformChanged += UpdateRoot;
		}

		public void UpdateRoot()
		{
			if ( Root.IsValid() && Anchor.IsValid() )
				Anchor.Transform = Root.Transform.InterpolatedWorld;
		}

		public void Dispose()
		{
			if ( Root.IsValid() ) Root.Transform.OnTransformChanged -= UpdateRoot;
			Anchor?.Delete();
		}
	}

	readonly Dictionary<GameObject, ParentEntry> _parents = new();

	public SceneModelTransformParentSystem( Scene scene ) : base( scene )
	{
	}

	internal bool TryAttach( ModelRenderer renderer, out GameObject root )
	{
		root = FindTaggedRoot( renderer?.GameObject );
		if ( !root.IsValid() || renderer is SkinnedModelRenderer || !renderer.SceneObject.IsValid() )
			return false;

		if ( !_parents.TryGetValue( root, out var entry ) )
		{
			entry = new ParentEntry( Scene, root );
			_parents.Add( root, entry );
		}

		entry.Anchor.AddChild( renderer.Id.ToString(), renderer.SceneObject );
		entry.Children++;
		return true;
	}

	internal void Detach( GameObject root, SceneObject child )
	{
		// DestroyImmediate can invalidate the managed root before its child renderers finish disabling.
		// The dictionary is keyed by the retained object reference, so validity is neither required nor
		// desirable here: always retire the anchor/refcount for the exact root that attached the child.
		if ( root is null || !_parents.TryGetValue( root, out var entry ) ) return;
		if ( child.IsValid() && entry.Anchor.IsValid() ) entry.Anchor.RemoveChild( child );
		entry.Children--;
		if ( entry.Children > 0 ) return;

		entry.Dispose();
		_parents.Remove( root );
	}

	static GameObject FindTaggedRoot( GameObject start )
	{
		for ( var current = start; current.IsValid(); current = current.Parent )
		{
			// GameTags.Has(tag) includes ancestors. Using it here made every descendant look
			// like the opt-in owner and created one native anchor per renderer. Resolve only
			// the GameObject that explicitly owns the tag so the capital gets exactly one anchor.
			if ( current.Tags.Has( NativeParentTag, includeAncestors: false ) ) return current;
		}
		return null;
	}

	public override void Dispose()
	{
		foreach ( var entry in _parents.Values ) entry.Dispose();
		_parents.Clear();
		base.Dispose();
	}
}
