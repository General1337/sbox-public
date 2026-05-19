// [HANDOFF-TRUST: additive observation event implementing user-aligned phase-01-design.md decision (Option B); no hypothesis-truth dependency on any sbep-reference handoff. Predecessor 1.1/1.2/1.3 docs are reflection-binding template references only.]
using System.Reflection;

namespace Sandbox;

internal static partial class ConVarSystem
{
	/// <summary>
	/// Fired once per <see cref="AddAssembly"/> completion. Payload is the
	/// Assembly just scanned and the array of Commands (ConCmds + ConVars)
	/// registered from it during this call.
	///
	/// Fires synchronously at the end of AddAssembly on the main thread.
	/// Per-subscriber try/catch isolates handler exceptions so one bad
	/// subscriber can't break the chain or destabilize ConVar registration.
	/// Standard C# multi-subscriber event semantics.
	///
	/// Empty payload (Command[].Length == 0) is allowed when an assembly
	/// has no [ConVar]-attributed static members for the given context.
	/// Subscribers may filter on length > 0 if they want fire-implies-something
	/// semantics.
	///
	/// Note: payload may include Commands that lost a TryAdd collision (name
	/// already registered). The producer logs a warning in that case but the
	/// Command object itself is still constructed and appears in the payload.
	/// Subscribers that need collision-free state should cross-check against
	/// ConVarSystem.Find(name).
	/// </summary>
	public static event Action<Assembly, Command[]> OnAssemblyRegistered;

	internal static void RaiseOnAssemblyRegistered( Assembly assembly, Command[] commands )
	{
		var handlers = OnAssemblyRegistered;
		if ( handlers is null ) return;

		foreach ( Action<Assembly, Command[]> handler in handlers.GetInvocationList() )
		{
			try
			{
				handler( assembly, commands );
			}
			catch ( System.Exception ex )
			{
				Log.Warning( $"ConVarSystem.OnAssemblyRegistered handler threw: {ex}" );
			}
		}
	}
}
