using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CompilingTests;

[TestClass]
[DoNotParallelize]
public class CompileTraceTest
{
	[TestCleanup]
	public void Cleanup()
	{
		CompileTrace.Observer = null;
	}

	[TestMethod]
	public void StructuredEventHasStableSchemaAndCacheFields()
	{
		CompileTraceEvent observed = null;
		CompileTrace.Observer = item => observed = item;

		var item = CompileTrace.Emit(
			"startup.project_compile",
			123.4567,
			"success",
			compiler: "local.test",
			group: "local",
			restartPath: "background-build-ready",
			cacheMode: "off",
			cacheDecision: "disabled",
			detail: "outputs=2" );

		Assert.AreSame( item, observed );
		Assert.AreEqual( CompileTrace.SchemaVersion, item.Schema );
		Assert.IsTrue( item.Sequence > 0 );
		Assert.AreEqual( 123.457, item.ElapsedMilliseconds );

		using var json = JsonDocument.Parse( CompileTrace.Serialize( item ) );
		var root = json.RootElement;

		Assert.AreEqual( "startup.project_compile", root.GetProperty( "name" ).GetString() );
		Assert.AreEqual( "background-build-ready", root.GetProperty( "restartPath" ).GetString() );
		Assert.AreEqual( "off", root.GetProperty( "cacheMode" ).GetString() );
		Assert.AreEqual( "disabled", root.GetProperty( "cacheDecision" ).GetString() );
		Assert.AreEqual( "success", root.GetProperty( "outcome" ).GetString() );
	}

	[TestMethod]
	public void ScopePublishesExplicitFailureOutcome()
	{
		CompileTraceEvent observed = null;
		CompileTrace.Observer = item => observed = item;

		using ( var scope = CompileTrace.Begin( "compiler.emit", "local.test", "local" ) )
		{
			scope.Complete( "failed", "diagnostics=1" );
		}

		Assert.IsNotNull( observed );
		Assert.AreEqual( "compiler.emit", observed.Name );
		Assert.AreEqual( "failed", observed.Outcome );
		Assert.AreEqual( "diagnostics=1", observed.Detail );
		Assert.IsTrue( observed.ElapsedMilliseconds >= 0 );
	}

	[TestMethod]
	public void ScopeWithoutCompletionIsExplicitlyIncomplete()
	{
		CompileTraceEvent observed = null;
		CompileTrace.Observer = item => observed = item;

		using ( CompileTrace.Begin( "resource.postload", group: "local.test" ) )
		{
		}

		Assert.IsNotNull( observed );
		Assert.AreEqual( "incomplete", observed.Outcome );
	}

	[TestMethod]
	public void ObserverFailureCannotBreakInstrumentation()
	{
		CompileTrace.Observer = _ => throw new InvalidOperationException( "test observer failure" );

		var item = CompileTrace.Emit( "compiler.test", 1, "success" );

		Assert.IsNotNull( item );
		Assert.AreEqual( "compiler.test", item.Name );
	}

	[TestMethod]
	public void ExtensionCountsAreDeterministic()
	{
		var counts = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase )
		{
			[".vsnd_c"] = 3,
			[".sound_c"] = 2,
			[".prefab_c"] = 1
		};

		Assert.AreEqual( ".prefab_c:1,.sound_c:2,.vsnd_c:3", CompileTrace.FormatCounts( counts ) );
	}

	[TestMethod]
	public void StartupCompilePathsHaveStableStructuredValues()
	{
		Assert.AreEqual(
			"background-build-ready",
			Editor.StartupLoadProject.GetTraceValue( Editor.StartupLoadProject.StartupCompilePath.BackgroundBuildReady ) );
		Assert.AreEqual(
			"recovery-build-ready",
			Editor.StartupLoadProject.GetTraceValue( Editor.StartupLoadProject.StartupCompilePath.RecoveryBuildReady ) );
		Assert.AreEqual(
			"recovery-cancelled",
			Editor.StartupLoadProject.GetTraceValue( Editor.StartupLoadProject.StartupCompilePath.RecoveryCancelled ) );
		Assert.AreEqual(
			"exception",
			Editor.StartupLoadProject.GetTraceValue( Editor.StartupLoadProject.StartupCompilePath.Exception ) );
	}

	[TestMethod]
	public async Task SuccessfulCompilerBuildPublishesEveryCurrentSubstage()
	{
		var events = new ConcurrentQueue<CompileTraceEvent>();
		CompileTrace.Observer = events.Enqueue;

		var codePath = Path.GetFullPath( "data/code/base" );
		using var group = new CompileGroup( "Trace Test" );
		var settings = new Compiler.Configuration();
		settings.Clean();
		group.CreateCompiler( "trace.test", codePath, settings );

		await group.BuildAsync();

		Assert.IsTrue( group.BuildResult.Success, group.BuildResult.BuildDiagnosticsString() );

		var successfulNames = events
			.Where( x => x.Outcome is "success" or "skipped" )
			.Select( x => x.Name )
			.ToHashSet();

		string[] expected =
		[
			"compiler.archive",
			"compiler.references",
			"compiler.prepare",
			"compiler.generators",
			"compiler.whitelist",
			"compiler.emit",
			"compiler.access_verify",
			"compiler.incremental_state",
			"compiler.metadata_reference",
			"compiler.build_internal",
			"compiler.total"
		];

		foreach ( var name in expected )
			Assert.IsTrue( successfulNames.Contains( name ), $"Missing successful trace event '{name}'" );
	}
}
