// [HANDOFF-TRUST: additive observation event implementing user-aligned phase-01-design.md decision (Option B); no hypothesis-truth dependency on any sbep-reference handoff. Predecessor 1.1/1.2/1.3 docs are reflection-binding template references only.]
using Sandbox.Diagnostics;
using System.Reflection;

namespace Sandbox.Internal;

public partial class TypeLibrary
{
	// Sandbox.Reflection.dll does not reference Sandbox.Engine, so the static
	// Log helper is not available here. The existing instance-Logger pattern
	// (Logger log; line 28) is not reachable from a static raise helper, so
	// we hold our own static Logger for the OnAssemblyRegistered handler-fault
	// path. Same NLog backend as the rest of the engine.
	static readonly Logger _onAssemblyRegisteredLog = new( "TypeLibrary.OnAssemblyRegistered" );


	/// <summary>
	/// Fired once per <see cref="AddAssembly"/> completion, after all
	/// Parallel.ForEach AddType calls have joined and PostAddCallbacks
	/// has drained. Payload is the Assembly that was registered and the
	/// array of TypeDescriptions just added to this TypeLibrary instance
	/// (only the new ones from this call, not the full cache).
	///
	/// Fires on the main thread — AddAssembly is main-thread and the
	/// internal Parallel.ForEach joins before the raise. Subscribers
	/// should NOT do heavy work synchronously; marshal to a worker
	/// thread or queue if needed.
	///
	/// Per-subscriber try/catch isolates handler exceptions so one bad
	/// subscriber can't break the chain or destabilize type registration.
	/// Standard C# multi-subscriber event semantics.
	///
	/// TypeLibrary carries <see cref="SkipHotloadAttribute"/> so the
	/// engine assembly persists across hotloads; old library subscribers
	/// remain attached unless they explicitly -= unsubscribe in their
	/// hotload handler. The intended downstream pattern is -+= re-bind
	/// on each library hotload.
	///
	/// Empty payload (TypeDescription[].Length == 0) is allowed when an
	/// assembly contains no exposed types. Subscribers may filter on
	/// length > 0 if they want fire-implies-something semantics.
	/// </summary>
	public static event Action<Assembly, TypeDescription[]> OnAssemblyRegistered;

	internal static void RaiseOnAssemblyRegistered( Assembly assembly, TypeDescription[] types )
	{
		var handlers = OnAssemblyRegistered;
		if ( handlers is null ) return;

		foreach ( Action<Assembly, TypeDescription[]> handler in handlers.GetInvocationList() )
		{
			try
			{
				handler( assembly, types );
			}
			catch ( System.Exception ex )
			{
				// [HANDOFF-TRUST: additive observation event; route to Sandbox.Diagnostics.Logger because Sandbox.Reflection.dll does not reference Sandbox.Engine's static Log helper.]
				_onAssemblyRegisteredLog.Warning( $"TypeLibrary.OnAssemblyRegistered handler threw: {ex.Message}" );
			}
		}
	}
}
