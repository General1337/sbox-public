using System.IO;
using Sandbox;

namespace TestPackage;

public class Program : TestCompiler.IProgram
{
	[ConCmd( "test.fastpath", Help = "version one" )]
	public static void TestCommand()
	{
	}

	[ConVar( "test.fastvar", Help = "version one", Min = 0, Max = 10 )]
	public static int TestVariable { get; set; } = 1;

	public int Main( StringWriter output )
	{
		output.Write( "two" );
		return 0;
	}
}
