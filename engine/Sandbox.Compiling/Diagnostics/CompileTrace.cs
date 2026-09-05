using System.Diagnostics;
using System.Text.Json;
using System.Threading;

namespace Sandbox;

/// <summary>
/// Structured local timing events for startup compilation, compiler stages, assembly loading,
/// and resource loading. This is intentionally independent of telemetry so startup evidence
/// remains available in the local log.
/// </summary>
internal static class CompileTrace
{
	internal const int SchemaVersion = 1;
	internal const string LogPrefix = "[compile-trace]";

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	private static long _sequence;

	/// <summary>
	/// Unit-test observation seam. Exceptions are isolated so diagnostics can never break a build.
	/// </summary>
	internal static Action<CompileTraceEvent> Observer { get; set; }

	internal static CompileTraceScope Begin(
		string name,
		string compiler = null,
		string group = null,
		string restartPath = null,
		string cacheMode = "off",
		string cacheDecision = "disabled" )
	{
		return new CompileTraceScope( name, compiler, group, restartPath, cacheMode, cacheDecision );
	}

	internal static CompileTraceEvent Emit(
		string name,
		double elapsedMilliseconds,
		string outcome,
		string compiler = null,
		string group = null,
		string restartPath = null,
		string cacheMode = "off",
		string cacheDecision = "disabled",
		string detail = null )
	{
		var item = new CompileTraceEvent
		{
			Schema = SchemaVersion,
			Sequence = Interlocked.Increment( ref _sequence ),
			TimestampUtc = DateTimeOffset.UtcNow,
			Name = name,
			ElapsedMilliseconds = Math.Round( elapsedMilliseconds, 3 ),
			Outcome = outcome,
			Compiler = compiler,
			Group = group,
			RestartPath = restartPath,
			CacheMode = cacheMode,
			CacheDecision = cacheDecision,
			Detail = detail
		};

		try
		{
			Observer?.Invoke( item );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"{LogPrefix} observer failed: {ex.Message}" );
		}

		try
		{
			Log.Info( $"{LogPrefix} {Serialize( item )}" );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"{LogPrefix} serialization failed for '{name}': {ex.Message}" );
		}

		return item;
	}

	internal static string Serialize( CompileTraceEvent item )
	{
		return JsonSerializer.Serialize( item, JsonOptions );
	}

	internal static string FormatCounts( IReadOnlyDictionary<string, int> counts )
	{
		return string.Join( ",", counts
			.OrderBy( x => x.Key, StringComparer.OrdinalIgnoreCase )
			.Select( x => $"{x.Key}:{x.Value}" ) );
	}

	internal sealed class CompileTraceScope : IDisposable
	{
		private readonly long _startedAt = Stopwatch.GetTimestamp();
		private readonly string _name;
		private readonly string _compiler;
		private readonly string _group;
		private readonly string _restartPath;
		private readonly string _cacheMode;
		private readonly string _cacheDecision;
		private string _outcome = "incomplete";
		private string _detail;
		private bool _disposed;

		internal CompileTraceScope(
			string name,
			string compiler,
			string group,
			string restartPath,
			string cacheMode,
			string cacheDecision )
		{
			_name = name;
			_compiler = compiler;
			_group = group;
			_restartPath = restartPath;
			_cacheMode = cacheMode;
			_cacheDecision = cacheDecision;
		}

		internal void Complete( string outcome, string detail = null )
		{
			_outcome = outcome;
			_detail = detail;
		}

		public void Dispose()
		{
			if ( _disposed ) return;
			_disposed = true;

			Emit(
				_name,
				Stopwatch.GetElapsedTime( _startedAt ).TotalMilliseconds,
				_outcome,
				compiler: _compiler,
				group: _group,
				restartPath: _restartPath,
				cacheMode: _cacheMode,
				cacheDecision: _cacheDecision,
				detail: _detail );
		}
	}
}

internal sealed record CompileTraceEvent
{
	public int Schema { get; init; }
	public long Sequence { get; init; }
	public DateTimeOffset TimestampUtc { get; init; }
	public string Name { get; init; }
	public double ElapsedMilliseconds { get; init; }
	public string Outcome { get; init; }
	public string Compiler { get; init; }
	public string Group { get; init; }
	public string RestartPath { get; init; }
	public string CacheMode { get; init; }
	public string CacheDecision { get; init; }
	public string Detail { get; init; }
}
