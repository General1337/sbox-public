using System.Runtime.CompilerServices;

namespace Sandbox.Tasks;

internal static partial class SyncContext
{
	public static class FrameStage
	{
		public static FrameStageAwaiter Update = new FrameStageAwaiter( "update" );
		public static FrameStageAwaiter FixedUpdate = new FrameStageAwaiter( "fixed" );
		public static FrameStageAwaiter PreRender = new FrameStageAwaiter( "prerender" );

		public class FrameStageAwaiter
		{
			readonly string _name;
			readonly string _continuationCategory;

			public FrameStageAwaiter( string name )
			{
				_name = name;
				_continuationCategory = $"async.continuation.{name}";
			}

			public ulong Value { get; private set; }

			public Action Queue { get; set; }

			public void Trigger()
			{
				var triggerTail = Sandbox.Diagnostics.PerformanceTailAttribution.Begin();
				try
				{
					Value++;

					var q = Queue;
					Queue = default;

					if ( q is not null )
					{
						foreach ( var d in q.GetInvocationList() )
						{
							var childTail = Sandbox.Diagnostics.PerformanceTailAttribution.Begin();
							try
							{
								d.DynamicInvoke();
							}
							finally
							{
								Sandbox.Diagnostics.PerformanceTailAttribution.End( childTail, _continuationCategory, Sandbox.Diagnostics.PerformanceTailAttribution.OwnerForDelegate( d ) );
							}
						}
					}
				}
				finally
				{
					Sandbox.Diagnostics.PerformanceTailAttribution.End( triggerTail, "async.trigger", _name );
				}
			}

			public async Task Await() => await new UpdateAwaiter( this, Value );
		}

		struct UpdateAwaiter : INotifyCompletion
		{
			ulong startValue;
			FrameStageAwaiter source;

			internal UpdateAwaiter( FrameStageAwaiter source, ulong startValue )
			{
				this.source = source;
				this.startValue = startValue;
			}

			public void OnCompleted( Action continuation )
			{
				source.Queue += continuation;
			}

			internal bool IsCompleted => startValue != source.Value;
			internal UpdateAwaiter GetAwaiter() => this;
			public void GetResult() { }
		}

	}
}
