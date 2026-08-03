using System.Reflection;

namespace Sandbox;

internal static partial class ConVarSystem
{
	internal static bool CanFastHotload( (MethodBase Old, MethodBase New)[] changes, out string reason )
	{
		var changedAttribute = changes.FirstOrDefault( static change =>
			typeof( ConVarAttribute ).IsAssignableFrom( change.New.DeclaringType ) );

		if ( changedAttribute.New is null )
		{
			reason = null;
			return true;
		}

		reason = $"console attribute implementation changed: {changedAttribute.New.DeclaringType?.FullName}.{changedAttribute.New.Name}";
		return false;
	}
}
