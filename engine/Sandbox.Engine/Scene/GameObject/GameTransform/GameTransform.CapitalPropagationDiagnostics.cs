using System.Diagnostics;

namespace Sandbox;

/// <summary>
/// Default-off causal counter for transform propagation under an opted-in capital root. This is
/// intentionally rooted at top-level TransformChanged calls so recursive work is not double-counted.
/// </summary>
internal static class CapitalPropagationDiagnostics
{
	[ConVar( "scene_transform_capital_profile", Help = "Profile top-level transform propagation beneath native_scene_parent roots." )]
	internal static bool Enabled { get; set; }

	sealed class ActiveSample
	{
		public string RootName;
		public string CapitalName;
		public bool DirectCapitalRoot;
		public bool UseTargetLocal;
		public int Nodes = 1;
		public long Started;
		public readonly Dictionary<string, CallbackAggregate> Callbacks = new();
	}

	sealed class RootAggregate
	{
		public long Calls;
		public long Nodes;
		public long Ticks;
	}

	sealed class CallbackAggregate
	{
		public long Calls;
		public long Ticks;
	}

	[ThreadStatic]
	static ActiveSample _active;

	static readonly object Sync = new();
	static readonly Dictionary<string, RootAggregate> Roots = new();
	static readonly Dictionary<string, CallbackAggregate> Callbacks = new();

	internal static bool Enter( GameTransform transform, bool useTargetLocal, bool topLevel )
	{
		if ( !Enabled ) return false;

		if ( _active is not null )
		{
			_active.Nodes++;
			return false;
		}

		if ( !topLevel ) return false;

		var capital = FindCapitalRoot( transform.GameObject );
		if ( !capital.IsValid() ) return false;

		_active = new ActiveSample
		{
			RootName = transform.GameObject.Name,
			CapitalName = capital.Name,
			DirectCapitalRoot = transform.GameObject == capital,
			UseTargetLocal = useTargetLocal,
			Started = Stopwatch.GetTimestamp()
		};
		return true;
	}

	internal static long BeginCallbacks( Delegate callbacks )
	{
		return _active is not null && callbacks is not null ? Stopwatch.GetTimestamp() : 0;
	}

	internal static void EndCallbacks( Delegate callbacks, long started, bool internalCallback )
	{
		if ( started == 0 || callbacks is null || _active is null ) return;

		var elapsed = Stopwatch.GetTimestamp() - started;
		var invocationList = callbacks.GetInvocationList();
		var splitTicks = invocationList.Length > 0 ? elapsed / invocationList.Length : elapsed;
		foreach ( var callback in invocationList )
		{
			var method = callback.Method;
			var owner = callback.Target?.GetType().Name ?? method.DeclaringType?.Name ?? "static";
			var propagation = _active.DirectCapitalRoot
				? (_active.UseTargetLocal ? "direct-target" : "direct-world")
				: "local";
			var key = $"{(internalCallback ? "internal" : "public")}:{owner}.{method.Name}|propagation={propagation}";
			if ( !_active.Callbacks.TryGetValue( key, out var aggregate ) )
			{
				aggregate = new CallbackAggregate();
				_active.Callbacks.Add( key, aggregate );
			}
			aggregate.Calls++;
			aggregate.Ticks += splitTicks;
		}
	}

	internal static void Exit( bool ownsSample )
	{
		if ( !ownsSample || _active is null ) return;

		var sample = _active;
		_active = null;
		var ticks = Stopwatch.GetTimestamp() - sample.Started;
		var key = $"capital={sample.CapitalName}|root={sample.RootName}|direct={sample.DirectCapitalRoot}|target={sample.UseTargetLocal}";

		lock ( Sync )
		{
			if ( !Roots.TryGetValue( key, out var root ) )
			{
				root = new RootAggregate();
				Roots.Add( key, root );
			}
			root.Calls++;
			root.Nodes += sample.Nodes;
			root.Ticks += ticks;

			foreach ( var pair in sample.Callbacks )
			{
				if ( !Callbacks.TryGetValue( pair.Key, out var callback ) )
				{
					callback = new CallbackAggregate();
					Callbacks.Add( pair.Key, callback );
				}
				callback.Calls += pair.Value.Calls;
				callback.Ticks += pair.Value.Ticks;
			}
		}
	}

	[ConCmd( "scene_transform_capital_profile_reset", Help = "Clear retained capital transform propagation counters." )]
	static void Reset()
	{
		lock ( Sync )
		{
			Roots.Clear();
			Callbacks.Clear();
		}
		Log.Info( "[capital-transform-profile] reset" );
	}

	[ConCmd( "scene_transform_capital_profile_dump", Help = "Dump retained capital transform propagation counters." )]
	static void Dump()
	{
		lock ( Sync )
		{
			Log.Info( $"[capital-transform-profile] enabled={Enabled} roots={Roots.Count} callbacks={Callbacks.Count}" );
			foreach ( var pair in Roots.OrderByDescending( x => x.Value.Ticks ).Take( 32 ) )
			{
				var value = pair.Value;
				Log.Info( $"[capital-transform-profile] root calls={value.Calls} nodes={value.Nodes} nodesPerCall={(double)value.Nodes / Math.Max( 1, value.Calls ):F2} ms={TicksToMs( value.Ticks ):F3} {pair.Key}" );
			}
			foreach ( var pair in Callbacks.OrderByDescending( x => x.Value.Ticks ).Take( 32 ) )
			{
				var value = pair.Value;
				Log.Info( $"[capital-transform-profile] callback calls={value.Calls} ms={TicksToMs( value.Ticks ):F3} owner={pair.Key}" );
			}
		}
	}

	static double TicksToMs( long ticks ) => ticks * 1000.0 / Stopwatch.Frequency;

	static GameObject FindCapitalRoot( GameObject start )
	{
		for ( var current = start; current.IsValid(); current = current.Parent )
		{
			if ( current.Tags.Has( SceneModelTransformParentSystem.NativeParentTag, includeAncestors: false ) ) return current;
		}
		return null;
	}
}
