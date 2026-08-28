namespace Sandbox.Diagnostics;

/// <summary>
/// GPU profiler stats collected from the scene system timestamp manager
/// </summary>
public static class GpuProfilerStats
{
	private static readonly List<string> _entries = new();
	private static readonly Dictionary<string, float> _smoothedDurations = new();
	private static readonly Dictionary<string, float> _maxDurations = new();
	private static bool _enabled;
	private static RealTimeSince _lastMemoryStatsUpdate;
	private static bool _hasMemoryStats;

	// Raw (unsmoothed) per-refresh snapshot for frame-keyed diagnostics. The smoothed/decayed
	// values above exist for the on-screen overlay; a per-frame ledger needs the exact durations
	// the timestamp queries returned on THIS refresh, plus which loop/output/GPU frame they arrived on.
	private static readonly List<RawEntry> _raw = new( 128 );
	private static readonly List<RawEntry> _rawPrevious = new( 128 );

	/// <summary>One GPU timing scope exactly as the scene system reported it on the latest refresh.</summary>
	public readonly record struct RawEntry( string Path, float Ms );

	/// <summary>
	/// Keeps the native timestamp profiler in TIMESTAMP_ONLY mode independently of the
	/// <c>overlay_gpu</c> ConVar, so a diagnostic recorder can read the pass tree without drawing
	/// the overlay (which itself costs GPU time). Default-off; measurement windows set and clear it.
	/// </summary>
	public static bool ForceEnabled { get; set; }

	/// <summary>Increments every refresh that returned at least one scope; 0 until the first.</summary>
	public static long RawSequence { get; private set; }

	/// <summary>Engine loop (<see cref="Application.FrameCount"/>) on which the latest raw refresh ran.</summary>
	public static ulong RawLoopFrame { get; private set; }

	/// <summary>Rendered-output count (<c>EngineLoop.RenderedFrames</c>) at the latest raw refresh.</summary>
	public static long RawOutputSequence { get; private set; }

	/// <summary>Render-device GPU frame number reported by <see cref="PerformanceStats.GpuFrameNumber"/> at the latest raw refresh.</summary>
	public static uint RawGpuFrameNumber { get; private set; }

	/// <summary>True when the latest refresh returned a scope list identical to the previous one (no new GPU results yet).</summary>
	public static bool RawRepeated { get; private set; }

	/// <summary>
	/// Copy the latest raw pass tree into <paramref name="into"/> (cleared first). Returns the
	/// number of entries. Paths are '/'-separated; the ledger rebuilds the tree from them.
	/// </summary>
	public static int CopyRaw( List<RawEntry> into )
	{
		into.Clear();
		into.AddRange( _raw );
		return into.Count;
	}

	/// <summary>
	/// Whether GPU profiling is enabled
	/// </summary>
	public static bool Enabled
	{
		get => _enabled;
		set
		{
			if ( _enabled == value )
				return;

			_enabled = value;
			NativeEngine.CSceneSystem.SetGPUProfilerMode( value ? NativeEngine.SceneSystemGPUProfilerMode.SCENE_GPU_PROFILER_TIMESTAMP_ONLY : NativeEngine.SceneSystemGPUProfilerMode.SCENE_GPU_PROFILER_DISABLE );

			if ( !value )
			{
				_smoothedDurations.Clear();
				_maxDurations.Clear();
			}
		}
	}

	/// <summary>
	/// GPU video memory budget in bytes.
	/// </summary>
	public static ulong VideoMemoryBudget { get; private set; }

	/// <summary>
	/// GPU video memory used by the engine in bytes.
	/// </summary>
	public static ulong VideoMemoryUsed { get; private set; }

	/// <summary>
	/// GPU video memory free within the current budget in bytes.
	/// </summary>
	public static ulong VideoMemoryFree { get; private set; }

	/// <summary>
	/// GPU video memory usage as a 0-1 fraction of budget.
	/// </summary>
	public static float VideoMemoryUsageFraction { get; private set; }

	/// <summary>
	/// Full '/'-separated paths of the current GPU timing scopes (split to build the tree).
	/// </summary>
	public static IReadOnlyList<string> Entries => _entries;

	/// <summary>
	/// Get a smoothed duration for a given name (for display purposes)
	/// </summary>
	public static float GetSmoothedDuration( string name )
	{
		return _smoothedDurations.GetValueOrDefault( name, 0f );
	}

	/// <summary>
	/// Get a decayed max duration for a given name (for display purposes)
	/// </summary>
	public static float GetMaxDuration( string name )
	{
		return _maxDurations.GetValueOrDefault( name, 0f );
	}

	internal static void Update()
	{
		if ( !_enabled )
		{
			_entries.Clear();
			return;
		}

		if ( !_hasMemoryStats || _lastMemoryStatsUpdate >= 1f )
		{
			UpdateMemoryStats();
		}

		_entries.Clear();
		_rawPrevious.Clear();
		_rawPrevious.AddRange( _raw );
		_raw.Clear();
		NativeEngine.CSceneSystem.RefreshGpuTimestampSnapshot();
		int count = NativeEngine.CSceneSystem.GetGpuTimestampCount();
		for ( int i = 0; i < count; i++ )
		{
			var path = NativeEngine.CSceneSystem.GetGpuTimestampPath( i );

			if ( string.IsNullOrEmpty( path ) )
				continue;

			float duration = NativeEngine.CSceneSystem.GetGpuTimestampDuration( i );
			_raw.Add( new RawEntry( path, duration ) );

			// Smooth the duration for display
			if ( _smoothedDurations.TryGetValue( path, out var smoothed ) )
			{
				smoothed = MathX.LerpTo( smoothed, duration, Time.Delta );
			}
			else
			{
				smoothed = duration;
			}
			_smoothedDurations[path] = smoothed;

			if ( _maxDurations.TryGetValue( path, out var maxDuration ) )
			{
				maxDuration = duration > maxDuration ? duration : MathX.LerpTo( maxDuration, duration, Time.Delta * 0.25f );
			}
			else
			{
				maxDuration = duration;
			}
			_maxDurations[path] = maxDuration;

			_entries.Add( path );
		}

		if ( _raw.Count > 0 )
		{
			RawSequence++;
			RawLoopFrame = Application.FrameCount;
			RawOutputSequence = EngineLoop.RenderedFrames;
			RawGpuFrameNumber = PerformanceStats.GpuFrameNumber;
			RawRepeated = SameAsPrevious();
		}
	}

	static bool SameAsPrevious()
	{
		if ( _raw.Count != _rawPrevious.Count ) return false;
		for ( var i = 0; i < _raw.Count; i++ )
		{
			if ( _raw[i].Ms != _rawPrevious[i].Ms || _raw[i].Path != _rawPrevious[i].Path ) return false;
		}
		return true;
	}

	private static void UpdateMemoryStats()
	{
		VideoMemoryBudget = Graphics.VideoMemoryBudget;
		VideoMemoryUsed = Graphics.VideoMemoryUsed;
		VideoMemoryFree = VideoMemoryUsed >= VideoMemoryBudget ? 0 : VideoMemoryBudget - VideoMemoryUsed;
		VideoMemoryUsageFraction = VideoMemoryBudget > 0
			? Math.Clamp( VideoMemoryUsed / (float)VideoMemoryBudget, 0f, 1f )
			: 0f;

		_lastMemoryStatsUpdate = 0;
		_hasMemoryStats = true;
	}
}
