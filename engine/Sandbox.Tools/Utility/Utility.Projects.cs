namespace Editor;

public static partial class EditorUtility
{
	public static partial class Projects
	{
		public static IReadOnlyList<Project> GetAll() => Project.All.AsReadOnly();

		public static async Task<bool> Updated( Project addon )
		{
			// Save changes
			addon?.Save();

			//
			// If we're a transient project we don't need to dirty everything else.
			// we just really want to call the save callback and return.
			//
			if ( addon is not null && addon.IsTransient )
			{
				return true;
			}

			bool compileSuccess = await Project.CompileAsync();
			if ( !compileSuccess )
				return false;

			if ( addon is not null )
			{
				if ( addon.Compiler is not null && !addon.Compiler.BuildSuccess )
					return false;

				if ( addon.EditorCompiler is not null && !addon.EditorCompiler.BuildSuccess )
					return false;
			}

			await WaitForCompiles();

			EditorEvent.Run( "localaddons.changed" );
			SceneEditorSession.Active?.UpdateEditorTitle();

			return true;
		}

		/// <summary>
		/// Wait for the local compiles to be finished
		/// </summary>
		public static async Task WaitForCompiles()
		{
			// give time for any files to finish being written
			//await Task.Delay( 1000 );

			// force finding new files, running callbacks
			FileWatch.Tick();

			// wait for compiles to finish
			await Project.CompileAsync();

			// give time for any files to finish being written
			// this is horrible in this context. We have to wait for 
			// filewatch to tick again to make sure we've picked up all the written files
			// search for FileSystem.Watch( "/.bin/*.dll" ); We need a better way to trigger
			// this shit manually in PackageLoader to 
			// 1. Say this project changed so reload the new package
			// 2. Don't re-trigger after it detects filesystem changes
			await Task.Delay( 500 );

			// filesystem callbacks..
			FileWatch.Tick();

			// Tick the loader to actually load
			// [PERF-OK: reverting a flag from an earlier draft of this same patch, per the M3 measurement.]
			// FORK PATCH #11: deliberately NOT force. This is the editor's own
			// compile-then-load path for locally edited game code - i.e. exactly the
			// hotload the fleet is trying to batch. An earlier draft forced it here on
			// the reasoning that it was "an explicitly requested load", which made
			// hotload_hold a no-op for game code while still batching the menu package.
			// Nothing here is network-sourced; the join path forces separately in
			// GameInstanceDll.Network.cs.
			Sandbox.GameInstanceDll.PackageLoader.Tick();

			// give time for any files to finish being written
			//await Task.Delay( 1000 );
		}

		/// <summary>
		/// Regenerates the project's solution
		/// </summary>
		public static async Task GenerateSolution()
		{
			await Project.GenerateSolution();
		}
	}
}
