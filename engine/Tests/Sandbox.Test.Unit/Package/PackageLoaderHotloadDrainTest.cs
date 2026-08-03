using System;
using System.Collections.Generic;

namespace Sandbox;

[TestClass]
public class PackageLoaderHotloadDrainTest
{
	[TestMethod]
	public void RegistersInitialEntriesAndRepairsOnce()
	{
		var first = new object();
		var second = new object();
		var pending = new List<object> { first, second };
		var registered = new List<object>();
		var repairs = 0;

		var result = PackageLoader.DrainHotloadRegistrations( pending, registered.Add, () => repairs++, 8 );

		CollectionAssert.AreEqual( new[] { first, second }, registered );
		Assert.AreEqual( 2, result.ProcessedCount );
		Assert.IsFalse( result.LimitReached );
		Assert.AreEqual( 1, repairs );
	}

	[TestMethod]
	public void DrainsEntriesAppendedDuringRegistration()
	{
		var first = new object();
		var appended = new object();
		var pending = new List<object> { first };
		var registered = new List<object>();
		var repairs = 0;

		var result = PackageLoader.DrainHotloadRegistrations( pending, entry =>
		{
			registered.Add( entry );
			if ( ReferenceEquals( entry, first ) ) pending.Add( appended );
		}, () => repairs++, 8 );

		CollectionAssert.AreEqual( new[] { first, appended }, registered );
		Assert.AreEqual( 2, result.ProcessedCount );
		Assert.AreEqual( 1, repairs );
	}

	[TestMethod]
	public void DeduplicatesByReferenceIdentity()
	{
		var same = new EqualByValue( 1 );
		var equalButDistinct = new EqualByValue( 1 );
		var pending = new List<EqualByValue> { same, same, equalButDistinct };
		var registered = new List<EqualByValue>();

		var result = PackageLoader.DrainHotloadRegistrations( pending, registered.Add, () => { }, 8 );

		CollectionAssert.AreEqual( new[] { same, equalButDistinct }, registered );
		Assert.AreEqual( 2, result.ProcessedCount );
	}

	[TestMethod]
	public void RegistrationFailureDoesNotBlockLaterEntriesOrRepair()
	{
		var first = new object();
		var second = new object();
		var pending = new List<object> { first, second };
		var registered = new List<object>();
		var repairs = 0;

		var result = PackageLoader.DrainHotloadRegistrations( pending, entry =>
		{
			if ( ReferenceEquals( entry, first ) ) throw new InvalidOperationException( "expected" );
			registered.Add( entry );
		}, () => repairs++, 8 );

		CollectionAssert.AreEqual( new[] { second }, registered );
		Assert.AreEqual( 2, result.ProcessedCount );
		Assert.AreEqual( 1, result.FailureCount );
		Assert.AreEqual( 1, repairs );
	}

	[TestMethod]
	public void RepairRunsOnceWhenItThrows()
	{
		var repairs = 0;

		Assert.ThrowsException<InvalidOperationException>( () =>
			PackageLoader.DrainHotloadRegistrations( new List<object> { new() }, _ => { }, () =>
			{
				repairs++;
				throw new InvalidOperationException( "expected" );
			}, 8 ) );

		Assert.AreEqual( 1, repairs );
	}

	[TestMethod]
	public void RecursiveAppendStopsAtCapAndRepairsOnce()
	{
		var pending = new List<object> { new() };
		var registered = 0;
		var repairs = 0;

		var result = PackageLoader.DrainHotloadRegistrations( pending, _ =>
		{
			registered++;
			pending.Add( new object() );
		}, () => repairs++, 4 );

		Assert.AreEqual( 4, registered );
		Assert.AreEqual( 4, result.ProcessedCount );
		Assert.IsTrue( result.LimitReached );
		Assert.AreEqual( 1, repairs );
	}

	private sealed record EqualByValue( int Value );
}
