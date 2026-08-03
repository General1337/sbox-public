using System.Reflection;

namespace Sandbox;

[TestClass]
public class ConVarSystemHotloadTest
{
	[TestMethod]
	public void CommandBodyChangeStaysFast()
	{
		var method = typeof( Commands ).GetMethod( nameof( Commands.Run ) );
		var allowed = ConVarSystem.CanFastHotload( [(method, method)], out var reason );

		Assert.IsTrue( allowed );
		Assert.IsNull( reason );
	}

	[TestMethod]
	public void ConsoleAttributeImplementationChangeFallsBack()
	{
		var method = typeof( CustomConCmdAttribute ).GetMethod( nameof( CustomConCmdAttribute.Touch ) );
		var allowed = ConVarSystem.CanFastHotload( [(method, method)], out var reason );

		Assert.IsFalse( allowed );
		StringAssert.Contains( reason, nameof( CustomConCmdAttribute ) );
	}

	private static class Commands
	{
		[ConCmd( "hotload_body" )]
		public static void Run() { }
	}

	private sealed class CustomConCmdAttribute : ConCmdAttribute
	{
		public void Touch() { }
	}
}
