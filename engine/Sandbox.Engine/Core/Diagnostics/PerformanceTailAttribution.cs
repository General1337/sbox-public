using System.Diagnostics;
using System.Reflection;

namespace Sandbox.Diagnostics;

/// <summary>
/// Default-off, frame-indexed diagnostic attribution for the engine containers that ordinary
/// <see cref="PerformanceStats"/> rows intentionally aggregate. The recorder uses a fixed record
/// array and persistent owner states so the measured window does not manufacture one managed
/// object per component call or per frame. It is diagnostic evidence, never a shipped perf lever.
/// </summary>
public static class PerformanceTailAttribution
{
	// Allocation-aware named scopes add roughly 300 owner rows per output in the full capital battle.
	// 250k overflowed by 2,509 rows in a 14-second/841-output capture, which makes a "complete" p95
	// ledger self-contradictory. This is a default-off Developer-only flight buffer; 500k preserves
	// the full window with comfortable headroom while remaining bounded.
	const int MaxRecords = 500_000;
	const string Prefix = "[PERF-TAIL]";

	[ConVar( "perf_tail_attribution" )]
	public static bool Enabled { get; set; }

	/// <summary>
	/// Opaque start state for a diagnostic sub-scope. Public only so Developer-build game-side
	/// instrumentation can add line-level owners to this same frame ledger; it is inert while the
	/// recorder is disabled.
	/// </summary>
	public readonly record struct Token( long Started, long Allocated, long GcPauseTicks, ulong Frame );
	readonly record struct OwnerKey( string Category, string Owner );
	readonly record struct DelegateKey( Type TargetType, MethodInfo Method );

	sealed class OwnerState
	{
		public int Id;
		public string Category;
		public string Owner;
		public ulong Frame = ulong.MaxValue;
		public int Calls;
		public long WorkTicks;
		public long WallTicks;
		public long GcPauseTicks;
		public long AllocatedBytes;
		public long MaxWallTicks;
		public string WorstObject;
	}

	struct FrameOwnerRecord
	{
		public ulong Frame;
		public int OwnerId;
		public int Calls;
		public long WorkTicks;
		public long WallTicks;
		public long GcPauseTicks;
		public long AllocatedBytes;
		public long MaxWallTicks;
		public string WorstObject;
	}

	sealed class SummaryState
	{
		public int OwnerId;
		public int Frames;
		public long Calls;
		public long WorkTicks;
		public long WallTicks;
		public long GcPauseTicks;
		public long AllocatedBytes;
		public long MaxWallTicks;
		public string WorstObject;
	}

	static readonly Dictionary<OwnerKey, OwnerState> Owners = new( 256 );
	static readonly List<OwnerState> OwnersById = new( 256 );
	static readonly Dictionary<Type, string> TypeNames = new( 256 );
	static readonly Dictionary<DelegateKey, string> DelegateNames = new( 128 );
	static readonly FrameOwnerRecord[] Records = new FrameOwnerRecord[MaxRecords];
	static int _recordCount;
	static int _dropped;
	static ulong _startedFrame;
	static ulong _stoppedFrame;

	/// <summary>
	/// One exact owner on one engine loop. This is the public, allocation-at-report-time view of the
	/// recorder's fixed internal buffer; the hot path continues to use structs and persistent owner
	/// states, so exposing the data does not add per-component garbage.
	/// </summary>
	public readonly record struct SnapshotRow(
		ulong Frame,
		string Category,
		string Owner,
		int Calls,
		double WorkMs,
		double WallMs,
		double GcMs,
		double AllocKb,
		double MaxCallMs,
		string WorstObject );

	/// <summary>Records discarded because the fixed flight-recorder buffer filled.</summary>
	public static int DroppedRecords => _dropped;

	/// <summary>
	/// Materialize the retained frame/owner ledger for a diagnostic report. Call after
	/// <see cref="Stop"/> for a stable window; calling while armed first flushes current aggregates.
	/// </summary>
	public static List<SnapshotRow> SnapshotRows()
	{
		if ( Enabled ) FlushAll();
		var result = new List<SnapshotRow>( _recordCount );
		for ( var i = 0; i < _recordCount; i++ )
		{
			var record = Records[i];
			var owner = OwnersById[record.OwnerId];
			result.Add( new SnapshotRow(
				record.Frame,
				owner.Category,
				owner.Owner,
				record.Calls,
				ToMs( record.WorkTicks ),
				ToMs( record.WallTicks ),
				TimeSpan.FromTicks( record.GcPauseTicks ).TotalMilliseconds,
				record.AllocatedBytes / 1024.0,
				ToMs( record.MaxWallTicks ),
				record.WorstObject ) );
		}
		return result;
	}

	/// <summary>Begin an allocation/time/GC-attributed diagnostic scope.</summary>
	public static Token Begin()
	{
		if ( !Enabled ) return default;
		return new Token(
			Stopwatch.GetTimestamp(),
			GC.GetAllocatedBytesForCurrentThread(),
			GC.GetTotalPauseDuration().Ticks,
			Application.FrameCount );
	}

	/// <summary>
	/// Close a diagnostic scope into the frame-indexed owner ledger. Nested scopes are intentionally
	/// inclusive, matching profiler timing semantics; callers must not sum parent and child rows.
	/// </summary>
	public static void End( in Token token, string category, string owner, string objectName = null )
	{
		if ( token.Started == 0 ) return;

		var ended = Stopwatch.GetTimestamp();
		var wallTicks = Math.Max( 0L, ended - token.Started );
		var pauseTicks = Math.Max( 0L, GC.GetTotalPauseDuration().Ticks - token.GcPauseTicks );
		var pauseStopwatchTicks = (long)(pauseTicks * (double)Stopwatch.Frequency / TimeSpan.TicksPerSecond);
		var workTicks = Math.Max( 0L, wallTicks - pauseStopwatchTicks );
		var allocated = Math.Max( 0L, GC.GetAllocatedBytesForCurrentThread() - token.Allocated );

		var state = GetOwner( category, owner );
		if ( state.Frame != token.Frame )
		{
			Flush( state );
			state.Frame = token.Frame;
		}

		state.Calls++;
		state.WorkTicks += workTicks;
		state.WallTicks += wallTicks;
		state.GcPauseTicks += pauseTicks;
		state.AllocatedBytes += allocated;
		if ( wallTicks > state.MaxWallTicks )
		{
			state.MaxWallTicks = wallTicks;
			state.WorstObject = objectName;
		}
	}

	internal static string OwnerForType( Type type )
	{
		if ( type is null ) return "(null)";
		if ( TypeNames.TryGetValue( type, out var name ) ) return name;
		name = type.FullName ?? type.Name;
		TypeNames[type] = name;
		return name;
	}

	internal static string OwnerForDelegate( Delegate callback )
	{
		if ( callback is null ) return "(null)";
		var targetType = callback.Target?.GetType() ?? callback.Method.DeclaringType;
		var key = new DelegateKey( targetType, callback.Method );
		if ( DelegateNames.TryGetValue( key, out var name ) ) return name;
		name = $"{OwnerForType( targetType )}.{callback.Method.Name}";
		DelegateNames[key] = name;
		return name;
	}

	static OwnerState GetOwner( string category, string owner )
	{
		var key = new OwnerKey( category ?? "(none)", owner ?? "(none)" );
		if ( Owners.TryGetValue( key, out var state ) ) return state;
		state = new OwnerState
		{
			Id = OwnersById.Count,
			Category = key.Category,
			Owner = key.Owner,
		};
		Owners[key] = state;
		OwnersById.Add( state );
		return state;
	}

	static void Flush( OwnerState state )
	{
		if ( state.Calls == 0 ) return;
		if ( _recordCount >= Records.Length )
		{
			_dropped++;
			ResetAggregate( state );
			return;
		}

		Records[_recordCount++] = new FrameOwnerRecord
		{
			Frame = state.Frame,
			OwnerId = state.Id,
			Calls = state.Calls,
			WorkTicks = state.WorkTicks,
			WallTicks = state.WallTicks,
			GcPauseTicks = state.GcPauseTicks,
			AllocatedBytes = state.AllocatedBytes,
			MaxWallTicks = state.MaxWallTicks,
			WorstObject = state.WorstObject,
		};
		ResetAggregate( state );
	}

	static void ResetAggregate( OwnerState state )
	{
		state.Calls = 0;
		state.WorkTicks = 0;
		state.WallTicks = 0;
		state.GcPauseTicks = 0;
		state.AllocatedBytes = 0;
		state.MaxWallTicks = 0;
		state.WorstObject = null;
	}

	static void FlushAll()
	{
		foreach ( var state in OwnersById ) Flush( state );
	}

	[ConCmd( "perf.tail_start", Help = "Start the default-off frame-indexed component/listener/async/editor tail attribution recorder." )]
	public static void Start()
	{
		Enabled = false;
		_recordCount = 0;
		_dropped = 0;
		_startedFrame = Application.FrameCount;
		_stoppedFrame = 0;
		foreach ( var state in OwnersById )
		{
			ResetAggregate( state );
			state.Frame = ulong.MaxValue;
		}
		Enabled = true;
		Log.Info( $"{Prefix} START frame={_startedFrame} capacity={Records.Length}" );
	}

	[ConCmd( "perf.tail_stop", Help = "Stop tail attribution and retain its frame-indexed records for reports." )]
	public static void Stop()
	{
		Enabled = false;
		_stoppedFrame = Application.FrameCount;
		FlushAll();
		Log.Info( $"{Prefix} STOP frames={_startedFrame}..{_stoppedFrame} records={_recordCount} owners={OwnersById.Count} dropped={_dropped}" );
	}

	[ConCmd( "perf.tail_status", Help = "Show tail-attribution recorder state." )]
	public static void Status()
	{
		Log.Info( $"{Prefix} STATUS enabled={Enabled} frames={_startedFrame}..{(_stoppedFrame == 0 ? Application.FrameCount : _stoppedFrame)} records={_recordCount} owners={OwnersById.Count} dropped={_dropped}" );
	}

	[ConCmd( "perf.tail_frame", Help = "Print every attributed owner on one engine frame, sorted by work time." )]
	public static void ReportFrame( long frame, int limit = 120 )
	{
		if ( Enabled ) FlushAll();
		var selected = Records.Take( _recordCount )
			.Where( x => x.Frame == (ulong)Math.Max( 0, frame ) )
			.OrderByDescending( x => x.WorkTicks )
			.ThenByDescending( x => x.AllocatedBytes )
			.Take( Math.Clamp( limit, 1, 500 ) );
		var count = 0;
		foreach ( var record in selected )
		{
			count++;
			Print( record );
		}
		Log.Info( $"{Prefix} FRAME-END frame={frame} rows={count}" );
	}

	[ConCmd( "perf.tail_summary", Help = "Aggregate the retained tail-attribution window by exact owner." )]
	public static void ReportSummary( int limit = 160 )
	{
		if ( Enabled ) FlushAll();
		var sums = new Dictionary<int, SummaryState>();
		for ( var i = 0; i < _recordCount; i++ )
		{
			var record = Records[i];
			if ( !sums.TryGetValue( record.OwnerId, out var sum ) )
			{
				sum = new SummaryState { OwnerId = record.OwnerId };
				sums[record.OwnerId] = sum;
			}
			sum.Frames++;
			sum.Calls += record.Calls;
			sum.WorkTicks += record.WorkTicks;
			sum.WallTicks += record.WallTicks;
			sum.GcPauseTicks += record.GcPauseTicks;
			sum.AllocatedBytes += record.AllocatedBytes;
			if ( record.MaxWallTicks > sum.MaxWallTicks )
			{
				sum.MaxWallTicks = record.MaxWallTicks;
				sum.WorstObject = record.WorstObject;
			}
		}

		foreach ( var sum in sums.Values
			.OrderByDescending( x => x.WorkTicks )
			.ThenByDescending( x => x.AllocatedBytes )
			.Take( Math.Clamp( limit, 1, 500 ) ) )
		{
			var owner = OwnersById[sum.OwnerId];
			Log.Info( $"{Prefix} SUMMARY category={owner.Category} owner={owner.Owner} frames={sum.Frames} calls={sum.Calls} workMs={ToMs( sum.WorkTicks ):F3} wallMs={ToMs( sum.WallTicks ):F3} gcMs={TimeSpan.FromTicks( sum.GcPauseTicks ).TotalMilliseconds:F3} allocKB={sum.AllocatedBytes / 1024.0:F1} maxCallMs={ToMs( sum.MaxWallTicks ):F3} worstObject={sum.WorstObject ?? "-"}" );
		}
		Log.Info( $"{Prefix} SUMMARY-END rows={Math.Min( sums.Count, Math.Clamp( limit, 1, 500 ) )} records={_recordCount} dropped={_dropped}" );
	}

	static void Print( in FrameOwnerRecord record )
	{
		var owner = OwnersById[record.OwnerId];
		Log.Info( $"{Prefix} FRAME frame={record.Frame} category={owner.Category} owner={owner.Owner} calls={record.Calls} workMs={ToMs( record.WorkTicks ):F3} wallMs={ToMs( record.WallTicks ):F3} gcMs={TimeSpan.FromTicks( record.GcPauseTicks ).TotalMilliseconds:F3} allocKB={record.AllocatedBytes / 1024.0:F1} maxCallMs={ToMs( record.MaxWallTicks ):F3} worstObject={record.WorstObject ?? "-"}" );
	}

	static double ToMs( long ticks ) => ticks * (1_000.0 / Stopwatch.Frequency);
}
