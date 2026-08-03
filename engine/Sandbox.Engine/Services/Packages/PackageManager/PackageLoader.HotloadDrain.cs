namespace Sandbox;

internal sealed partial class PackageLoader
{
	internal readonly record struct HotloadDrainResult( int ProcessedCount, int FailureCount, bool LimitReached );

	internal static HotloadDrainResult DrainHotloadRegistrations<T>(
		List<T> pending,
		Action<T> register,
		Action repair,
		int maxEntries,
		Action<Exception> onFailure = null ) where T : class
	{
		ArgumentNullException.ThrowIfNull( pending );
		ArgumentNullException.ThrowIfNull( register );
		ArgumentNullException.ThrowIfNull( repair );
		ArgumentOutOfRangeException.ThrowIfLessThan( maxEntries, 1 );

		var seen = new HashSet<T>( ReferenceEqualityComparer.Instance );
		var examined = 0;
		var processed = 0;
		var failures = 0;

		try
		{
			while ( examined < pending.Count && examined < maxEntries )
			{
				var entry = pending[examined++];
				if ( entry is null || !seen.Add( entry ) ) continue;

				processed++;
				try
				{
					register( entry );
				}
				catch ( Exception exception )
				{
					failures++;
					try { onFailure?.Invoke( exception ); }
					catch { }
				}
			}

			return new HotloadDrainResult( processed, failures, examined < pending.Count );
		}
		finally
		{
			repair();
		}
	}
}
