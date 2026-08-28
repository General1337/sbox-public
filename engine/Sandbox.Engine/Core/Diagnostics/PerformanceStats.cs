using System.Collections.Concurrent;
using System.Diagnostics;

namespace Sandbox.Diagnostics;

public static partial class PerformanceStats
{
	public struct Block
	{
		public float FrameAvg;
		public float FrameMin;
		public float FrameMax;

		public long ByteAlloc;
		public int Gc0;
		public int Gc1;
		public int Gc2;
		public long GcPause;
	}

	/// <summary>
	/// Get the time taken, in seconds, that were required to process the previous frame.
	/// </summary>
	public static double FrameTime { get; internal set; }

	/// <summary>
	/// Latest available GPU frametime, in ms.
	/// </summary>
	public static float GpuFrametime { get; internal set; }

	/// <summary>
	/// Frame number of the last reported <see cref="GpuFrametime"/>.
	/// </summary>
	public static uint GpuFrameNumber { get; internal set; }

	/// <summary>
	/// The number of bytes that were allocated on the managed heap in the last frame.
	/// <remarks>This may not include allocations from threads other than the game thread.</remarks>
	/// </summary>
	public static long BytesAllocated { get; internal set; }

	/// <summary>
	/// Number of generation 0 (fastest) garbage collections were done in the last frame.
	/// </summary>
	public static int Gen0Collections { get; internal set; }

	/// <summary>
	/// Number of generation 1 (fast) garbage collections were done in the last frame.
	/// </summary>
	public static int Gen1Collections { get; internal set; }

	/// <summary>
	/// Number of generation 2 (slow) garbage collections were done in the last frame.
	/// </summary>
	public static int Gen2Collections { get; internal set; }

	/// <summary>
	/// How many ticks we paused in the last frame
	/// </summary>
	public static long GcPause { get; internal set; }

	/// <summary>
	/// Number of exceptions in the last frame.
	/// </summary>
	public static int Exceptions { get; internal set; }

	/// <summary>
	/// Approximate working set of this process.
	/// </summary>
	public static ulong ApproximateProcessMemoryUsage { get; internal set; }

	/// <summary>
	/// CPU time consumed by the whole editor process between the previous two frame boundaries, in
	/// milliseconds. Unlike the main-thread timing rows, this includes render, physics, audio,
	/// networking and worker threads; it can legitimately exceed wall-clock frame time on many cores.
	/// </summary>
	public static double ProcessCpuTimeMs { get; internal set; }

	/// <summary>Logical CPU count, exposed through the engine so sandboxed game reports need no System.Environment access.</summary>
	public static int LogicalProcessorCount => Environment.ProcessorCount;

	// ── frame-keyed diagnostics (Developer fork seams; default-cheap, read by the battle ledger) ──

	/// <summary>
	/// One render-device GPU frame-time result exactly as it arrived. The device reports the latest
	/// COMPLETED GPU frame, which lands one or more loops after the CPU submitted it and is only
	/// visible when polled; polling at several points per loop (<see cref="PollGpuFrameTime"/>) means
	/// no completed frame is skipped, so a ledger can join results to loops by frame number instead of
	/// copying whatever value happened to be current.
	/// </summary>
	/// <param name="FrameNumber">Render-device frame number the result belongs to.</param>
	/// <param name="Ms">GPU time of that frame in milliseconds.</param>
	/// <param name="ArrivalLoop"><see cref="Application.FrameCount"/> when the result was first observed.</param>
	/// <param name="ArrivalOutput"><c>EngineLoop.RenderedFrames</c> when the result was first observed.</param>
	/// <param name="ArrivalPoint">0 = frame start, 1 = after native engine frame, 2 = frame end, 3 = client output.</param>
	public readonly record struct GpuFrameSample( uint FrameNumber, float Ms, ulong ArrivalLoop, long ArrivalOutput, byte ArrivalPoint );

	const int GpuSampleRingSize = 512;
	static readonly GpuFrameSample[] _gpuSamples = new GpuFrameSample[GpuSampleRingSize];
	static long _gpuSampleCount;

	/// <summary>Total GPU frame-time results observed since boot; use with <see cref="CopyGpuFrameSamples"/>.</summary>
	public static long GpuSampleSequence => _gpuSampleCount;

	/// <summary>
	/// Copy every GPU sample observed after <paramref name="sinceSequence"/> into <paramref name="into"/>
	/// (cleared first) and return the new sequence. Samples older than the ring are lost; a caller that
	/// drains once per loop never loses any.
	/// </summary>
	public static long CopyGpuFrameSamples( long sinceSequence, List<GpuFrameSample> into )
	{
		into.Clear();
		var first = Math.Max( sinceSequence, _gpuSampleCount - GpuSampleRingSize );
		for ( var i = first; i < _gpuSampleCount; i++ ) into.Add( _gpuSamples[i % GpuSampleRingSize] );
		return _gpuSampleCount;
	}

	internal static void PollGpuFrameTime( byte point )
	{
		if ( !g_pRenderDevice.GetGPUFrameTimeMS( IntPtr.Zero, out float gpuFrametime, out uint gpuFrameNo ) ) return;
		if ( _gpuSampleCount > 0 && gpuFrameNo == GpuFrameNumber ) return;
		GpuFrametime = gpuFrametime;
		GpuFrameNumber = gpuFrameNo;
		_gpuSamples[_gpuSampleCount % GpuSampleRingSize] = new GpuFrameSample( gpuFrameNo, gpuFrametime, Application.FrameCount, EngineLoop.RenderedFrames, point );
		_gpuSampleCount++;
	}

	/// <summary>Managed bytes allocated on EVERY thread of the process in the last frame (main-thread share is <see cref="BytesAllocated"/>).</summary>
	public static long ProcessBytesAllocated { get; internal set; }
	static long _prevProcessAllocatedBytes;

	/// <summary>Index of the most recent garbage collection (any generation), from <see cref="GC.GetGCMemoryInfo(GCKind)"/>; 0 until the first.</summary>
	public static long LastGcIndex { get; internal set; }
	/// <summary>Generation of the most recent collection.</summary>
	public static int LastGcGeneration { get; internal set; }
	/// <summary>Sum of the most recent collection's pause durations, in milliseconds.</summary>
	public static double LastGcPauseMs { get; internal set; }
	/// <summary>Bytes promoted by the most recent collection.</summary>
	public static long LastGcPromotedBytes { get; internal set; }
	/// <summary>Managed heap size after the most recent collection.</summary>
	public static long LastGcHeapSizeBytes { get; internal set; }
	/// <summary>True when the most recent collection compacted the heap.</summary>
	public static bool LastGcCompacted { get; internal set; }
	/// <summary>True when the most recent collection ran concurrently (background).</summary>
	public static bool LastGcConcurrent { get; internal set; }
	/// <summary>Number of distinct collections whose info was observed in the last frame (0 or more).</summary>
	public static int GcInfoUpdatesThisFrame { get; internal set; }

	/// <summary>Stopwatch ticks at which the previous engine loop finished (0 before the first loop).</summary>
	internal static long LastLoopEndTicks;

	/// <summary>
	/// Performance statistics over the last period, which is dictated by "perf_time" console command.
	/// </summary>
	public static Block LastSecond { get; internal set; }

	private static Stopwatch frameTimer;
	private static Stopwatch secondTimer;
	private static List<Block> _history = new List<Block>( 1024 );
	private static long _prevAllocatedBytes;
	private static long _prevPauseTime;
	private static int _prevGen0, _prevGen1, _prevGen2;
	private static int _exceptions;
	private static readonly Process CurrentProcess = Process.GetCurrentProcess();
	private static TimeSpan _prevProcessCpuTime;
	private static int _lastSecond; // the actual rounded second of RealTime.Now when we last captured

	internal static bool Frame()
	{
		frameTimer ??= Stopwatch.StartNew();
		secondTimer ??= Stopwatch.StartNew();

		float frameMs = (float)frameTimer.Elapsed.TotalMilliseconds;

		PerformanceStats.FrameTime = frameTimer.Elapsed.TotalSeconds;
		frameTimer.Restart();

		PollGpuFrameTime( 0 );

		var allocatedBytes = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
		PerformanceStats.BytesAllocated = allocatedBytes - _prevAllocatedBytes;
		_prevAllocatedBytes = allocatedBytes;

		var processAllocated = GC.GetTotalAllocatedBytes( false );
		ProcessBytesAllocated = Math.Max( 0L, processAllocated - _prevProcessAllocatedBytes );
		_prevProcessAllocatedBytes = processAllocated;

		var pauseTicks = GC.GetTotalPauseDuration().Ticks;
		GcPause = pauseTicks - _prevPauseTime;
		_prevPauseTime = pauseTicks;

		Timings.GcPause.AddMilliseconds( TimeSpan.FromTicks( GcPause ).TotalMilliseconds );

		var gen0 = GC.CollectionCount( 0 );
		var gen1 = GC.CollectionCount( 1 );
		var gen2 = GC.CollectionCount( 2 );
		PerformanceStats.Gen0Collections = gen0 - _prevGen0;
		PerformanceStats.Gen1Collections = gen1 - _prevGen1;
		PerformanceStats.Gen2Collections = gen2 - _prevGen2;
		_prevGen0 = gen0;
		_prevGen1 = gen1;
		_prevGen2 = gen2;

		// Exact detail of the most recent collection, only when one happened: which generation,
		// how long it stopped the world, what it promoted. Gen0 counts include gen1/gen2 runs, so the
		// count delta alone cannot say which kind of pause landed in this frame.
		GcInfoUpdatesThisFrame = 0;
		if ( PerformanceStats.Gen0Collections > 0 )
		{
			var info = GC.GetGCMemoryInfo( GCKind.Any );
			if ( info.Index != LastGcIndex )
			{
				GcInfoUpdatesThisFrame = (int)Math.Min( int.MaxValue, Math.Max( 1L, info.Index - LastGcIndex ) );
				LastGcIndex = info.Index;
				LastGcGeneration = info.Generation;
				var pause = 0.0;
				foreach ( var d in info.PauseDurations ) pause += d.TotalMilliseconds;
				LastGcPauseMs = pause;
				LastGcPromotedBytes = info.PromotedBytes;
				LastGcHeapSizeBytes = info.HeapSizeBytes;
				LastGcCompacted = info.Compacted;
				LastGcConcurrent = info.Concurrent;
			}
		}

		PerformanceStats.ApproximateProcessMemoryUsage = NativeEngine.EngineGlue.ApproximateProcessMemoryUsage();

		var processCpuTime = CurrentProcess.TotalProcessorTime;
		ProcessCpuTimeMs = Math.Max( 0.0, (processCpuTime - _prevProcessCpuTime).TotalMilliseconds );
		_prevProcessCpuTime = processCpuTime;

		Timings.FlipAll();

		// how many exceptions happened between now and the last one
		Exceptions = Application.ExceptionCount - _exceptions;
		if ( Exceptions < 0 ) Exceptions = 0;
		_exceptions = Application.ExceptionCount;


		_history.Add( new Block
		{
			FrameAvg = frameMs,
			ByteAlloc = BytesAllocated,
			Gc0 = PerformanceStats.Gen0Collections,
			Gc1 = PerformanceStats.Gen1Collections,
			Gc2 = PerformanceStats.Gen2Collections,
			GcPause = GcPause
		} );

		var second = RealTime.Now.FloorToInt();
		if ( _lastSecond == second )
			return false;

		_lastSecond = second;

		var ls = new Block();
		ls.FrameAvg = _history.Average( x => x.FrameAvg );
		ls.FrameMin = _history.Min( x => x.FrameAvg );
		ls.FrameMax = _history.Max( x => x.FrameAvg );
		ls.ByteAlloc = _history.Sum( x => x.ByteAlloc );
		ls.Gc0 = _history.Sum( x => x.Gc0 );
		ls.Gc1 = _history.Sum( x => x.Gc1 );
		ls.Gc2 = _history.Sum( x => x.Gc2 );
		ls.GcPause = _history.Sum( x => x.GcPause );

		LastSecond = ls;

		_history.Clear();
		secondTimer.Restart();

		ulong poolUsed = 0, poolLimit = 0, poolNonEvictable = 0;
		g_pRenderDevice.GetTexturePoolStats( out poolUsed, out poolLimit, out poolNonEvictable );
		FrameStats._current = new FrameStats(
			NativeEngine.CSceneSystem.GetPerFrameStats(),
			NativeEngine.CSceneSystem.GetNumUnbatchableMaterials(),
			g_pRenderDevice.GetGpuStatsSummary(),
			NativeEngine.g_pResourceSystem.GetNumPendingStreamingRequests(),
			poolUsed, poolLimit, poolNonEvictable );

		return true;
	}

	public record struct PeriodMetric( float Min, float Max, float Avg, int Calls );
}


internal static class ObjectPool<T> where T : class, new()
{
	public static T Get()
	{
		if ( _pool.TryDequeue( out var o ) )
			return o;

		return new T();
	}

	public static void Return( T obj )
	{
		_pool.Enqueue( obj );
	}

	static ConcurrentQueue<T> _pool = new();
}
