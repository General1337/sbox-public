using System.IO;
using Sandbox;

namespace TestPackage;

public class Program : TestCompiler.IProgram
{
	[ConCmd( "test.fastpath", Help = "version two" )]
	public static void TestCommand()
	{
	}

	[ConVar( "test.fastvar", Help = "version two", Min = 0, Max = 20 )]
	public static int TestVariable { get; set; } = 1;

	public int Main( StringWriter output )
	{
		output.Write( "one" );
		return 0;
	}
}
