using System.Diagnostics;
using DriveMonitor.Core.Models;
using DriveMonitor.Core.Tracing;

namespace DriveMonitor.Tests.Tracing;

/// <summary>
/// Matching FileIo initiations with FileIo_OpEnd completions that may arrive in any order and on
/// recycled IRP addresses, and releasing the operations in initiation order.
/// </summary>
/// <remarks>"qpc" is when the kernel logged an event; "tick" is when the consumer received it.</remarks>
public sealed class IoCorrelatorTests
{
    private const ulong Irp = 0xFFFF_9A00_1234_5600;
    private const ulong OtherIrp = 0xFFFF_9A00_1234_7800;
    private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);

    [Fact]
    public void Complete_AfterBegin_MatchesTheOperation()
    {
        var correlator = new IoCorrelator();
        PendingIo op = Op(Irp, qpc: 100);
        correlator.Begin(op, nowTick: 5);

        bool matched = correlator.Complete(Irp, timestampQpc: 150, info: 4096, status: 0, nowTick: 6);

        Assert.True(matched);
        Assert.True(op.Completed);
        Assert.Equal(0, op.NtStatus);
        Assert.Equal(4096UL, op.CompletionInfo);
        Assert.Equal(5L, op.ReceivedTick);
        Assert.Equal(1, correlator.PendingCount); // still queued until released
        Assert.Equal(0, correlator.EarlyCompletionCount);

        Assert.Same(op, Assert.Single(Release(correlator, nowTick: 6)));
        Assert.Equal(0, correlator.PendingCount);
    }

    [Fact]
    public void Complete_CopiesTheFailureStatus()
    {
        var correlator = new IoCorrelator();
        PendingIo op = Op(Irp, qpc: 100);
        correlator.Begin(op, nowTick: 0);

        Assert.True(correlator.Complete(Irp, 150, info: 0, status: unchecked((int)0xC0000022), nowTick: 0));

        Assert.Equal(unchecked((int)0xC0000022), op.NtStatus);
    }

    [Theory]
    [InlineData(100L)]
    [InlineData(101L)]
    [InlineData(long.MaxValue)]
    public void Complete_LoggedAtOrAfterTheBegin_Matches(long completionQpc)
    {
        var correlator = new IoCorrelator();
        PendingIo op = Op(Irp, qpc: 100);
        correlator.Begin(op, nowTick: 0);

        Assert.True(correlator.Complete(Irp, completionQpc, info: 1, status: 0, nowTick: 0));
        Assert.True(op.Completed);
    }

    [Fact]
    public void Complete_LoggedBeforeTheBegin_DoesNotMatch()
    {
        // A completion of an earlier use of the same IRP address that was delivered late.
        var correlator = new IoCorrelator();
        PendingIo op = Op(Irp, qpc: 200);
        correlator.Begin(op, nowTick: 0);

        Assert.False(correlator.Complete(Irp, timestampQpc: 150, info: 7, status: 0, nowTick: 0));

        Assert.False(op.Completed);
        Assert.Equal(0UL, op.CompletionInfo);
        Assert.Equal(1, correlator.EarlyCompletionCount);
        Assert.Empty(Release(correlator, nowTick: 0));
    }

    [Fact]
    public void Complete_ForAnUnknownIrp_IsKeptAsEarlyCompletion()
    {
        var correlator = new IoCorrelator();
        PendingIo op = Op(Irp, qpc: 100);
        correlator.Begin(op, nowTick: 0);

        Assert.False(correlator.Complete(OtherIrp, 150, info: 1, status: 0, nowTick: 0));

        Assert.False(op.Completed);
        Assert.Equal(1, correlator.EarlyCompletionCount);
    }

    [Theory]
    [InlineData(100L)]
    [InlineData(150L)]
    public void EarlyCompletion_LoggedAtOrAfterTheBegin_MatchesWhenTheBeginArrives(long completionQpc)
    {
        // Per-CPU buffers: the completion can be delivered before its initiation.
        var correlator = new IoCorrelator();

        Assert.False(correlator.Complete(Irp, completionQpc, info: 512, status: 0, nowTick: 0));
        Assert.Equal(1, correlator.EarlyCompletionCount);

        PendingIo op = Op(Irp, qpc: 100);
        correlator.Begin(op, nowTick: 1);

        Assert.True(op.Completed);
        Assert.Equal(512UL, op.CompletionInfo);
        Assert.Equal(0, correlator.EarlyCompletionCount); // the used completion leaves the store at once
        Assert.Same(op, Assert.Single(Release(correlator, nowTick: 1)));
    }

    [Fact]
    public void EarlyCompletion_LoggedBeforeTheBegin_IsNotMatched()
    {
        // The IRP address was reused: the stored completion belongs to the previous operation.
        var correlator = new IoCorrelator();
        Assert.False(correlator.Complete(Irp, timestampQpc: 50, info: 1, status: 0, nowTick: 0));

        PendingIo op = Op(Irp, qpc: 100);
        correlator.Begin(op, nowTick: 1);

        Assert.False(op.Completed);
        Assert.Empty(Release(correlator, nowTick: 1));

        Assert.True(correlator.Complete(Irp, timestampQpc: 120, info: 2, status: 0, nowTick: 2));
        Assert.Equal(2UL, op.CompletionInfo);
    }

    [Fact]
    public void EarlyCompletions_TheEarliestOneAtOrAfterTheBeginWins()
    {
        var correlator = new IoCorrelator();
        correlator.Complete(Irp, timestampQpc: 300, info: 3, status: 0, nowTick: 0);
        correlator.Complete(Irp, timestampQpc: 100, info: 1, status: 0, nowTick: 0);
        correlator.Complete(Irp, timestampQpc: 500, info: 5, status: 0, nowTick: 0);

        PendingIo middle = Op(Irp, qpc: 200);
        correlator.Begin(middle, nowTick: 1);
        PendingIo first = Op(Irp, qpc: 50);
        correlator.Begin(first, nowTick: 1);
        PendingIo last = Op(Irp, qpc: 400);
        correlator.Begin(last, nowTick: 1);

        Assert.Equal(3UL, middle.CompletionInfo);
        Assert.Equal(1UL, first.CompletionInfo);
        Assert.Equal(5UL, last.CompletionInfo);
    }

    [Fact]
    public void TwoPendingBeginsOnOneIrp_ACompletionGoesToTheLatestBeginNotAfterIt()
    {
        var correlator = new IoCorrelator();
        PendingIo older = Op(Irp, qpc: 100);
        PendingIo newer = Op(Irp, qpc: 300);
        correlator.Begin(older, nowTick: 0);
        correlator.Begin(newer, nowTick: 0);

        Assert.True(correlator.Complete(Irp, timestampQpc: 200, info: 1, status: 0, nowTick: 0));

        Assert.True(older.Completed);
        Assert.Equal(1UL, older.CompletionInfo);
        Assert.False(newer.Completed);

        Assert.True(correlator.Complete(Irp, timestampQpc: 400, info: 2, status: 0, nowTick: 0));

        Assert.True(newer.Completed);
        Assert.Equal(2UL, newer.CompletionInfo);
        Assert.Equal(new[] { older, newer }, Release(correlator, nowTick: 0));
    }

    [Fact]
    public void TwoPendingBeginsOnOneIrp_CompletionsArrivingNewestFirst_StillPairCorrectly()
    {
        var correlator = new IoCorrelator();
        PendingIo older = Op(Irp, qpc: 100);
        PendingIo newer = Op(Irp, qpc: 300);
        correlator.Begin(older, nowTick: 0);
        correlator.Begin(newer, nowTick: 0);

        Assert.True(correlator.Complete(Irp, timestampQpc: 500, info: 2, status: 0, nowTick: 0));
        Assert.True(correlator.Complete(Irp, timestampQpc: 150, info: 1, status: 0, nowTick: 0));

        Assert.Equal(1UL, older.CompletionInfo);
        Assert.Equal(2UL, newer.CompletionInfo);
    }

    [Fact]
    public void TwoPendingBeginsOnOneIrp_CompletionOlderThanBoth_MatchesNeither()
    {
        var correlator = new IoCorrelator();
        PendingIo older = Op(Irp, qpc: 100);
        PendingIo newer = Op(Irp, qpc: 300);
        correlator.Begin(older, nowTick: 0);
        correlator.Begin(newer, nowTick: 0);

        Assert.False(correlator.Complete(Irp, timestampQpc: 50, info: 1, status: 0, nowTick: 0));

        Assert.False(older.Completed);
        Assert.False(newer.Completed);
        Assert.Equal(1, correlator.EarlyCompletionCount);
    }

    [Fact]
    public void Release_KeepsInitiationOrder()
    {
        var correlator = new IoCorrelator(holdMs: 1000);
        PendingIo first = Op(Irp, qpc: 100);
        PendingIo second = Op(OtherIrp, qpc: 110);
        correlator.Begin(first, nowTick: 0);
        correlator.Begin(second, nowTick: 10);

        Assert.True(correlator.Complete(OtherIrp, timestampQpc: 120, info: 0, status: 0, nowTick: 20));
        Assert.Empty(Release(correlator, nowTick: 20)); // the completed second waits behind the first

        Assert.True(correlator.Complete(Irp, timestampQpc: 130, info: 0, status: 0, nowTick: 30));
        Assert.Equal(new[] { first, second }, Release(correlator, nowTick: 30));
    }

    [Fact]
    public void Release_CompletedOperationWaitsBehindAnIncompleteOneUntilTheHoldTimeExpires()
    {
        var correlator = new IoCorrelator(holdMs: 1000);
        PendingIo first = Op(Irp, qpc: 100);
        PendingIo second = Op(OtherIrp, qpc: 110);
        correlator.Begin(first, nowTick: 0);
        correlator.Begin(second, nowTick: 10);
        correlator.Complete(OtherIrp, timestampQpc: 120, info: 0, status: 0, nowTick: 20);

        Assert.Empty(Release(correlator, nowTick: 999));

        Assert.Equal(new[] { first, second }, Release(correlator, nowTick: 1000));
        Assert.False(first.Completed);
        Assert.True(second.Completed);
        Assert.Equal(0, correlator.PendingCount);
    }

    [Fact]
    public void Release_OperationWithoutIrp_IsNotHeldBack()
    {
        var correlator = new IoCorrelator();
        PendingIo op = Op(irp: 0, qpc: 100);

        correlator.Begin(op, nowTick: 0);

        Assert.Same(op, Assert.Single(Release(correlator, nowTick: 0)));
        Assert.False(op.Completed);
    }

    [Fact]
    public void Complete_WithoutIrp_IsIgnored()
    {
        var correlator = new IoCorrelator();

        Assert.False(correlator.Complete(0, timestampQpc: 100, info: 1, status: 0, nowTick: 0));

        Assert.Equal(0, correlator.EarlyCompletionCount);
    }

    [Fact]
    public void Release_OperationWithoutIrp_StillWaitsBehindAnEarlierOperation()
    {
        var correlator = new IoCorrelator(holdMs: 1000);
        PendingIo waiting = Op(Irp, qpc: 100);
        PendingIo noIrp = Op(irp: 0, qpc: 110);
        correlator.Begin(waiting, nowTick: 0);
        correlator.Begin(noIrp, nowTick: 0);

        Assert.Empty(Release(correlator, nowTick: 0));
        Assert.Equal(new[] { waiting, noIrp }, Release(correlator, nowTick: 1000));
    }

    [Fact]
    public void HoldExpiry_ReleasesAsNotCompleted_AndALateCompletionCannotCorruptAnything()
    {
        var correlator = new IoCorrelator(holdMs: 1000);
        PendingIo expired = Op(Irp, qpc: 100);
        correlator.Begin(expired, nowTick: 0);

        Assert.Same(expired, Assert.Single(Release(correlator, nowTick: 1000)));
        Assert.False(expired.Completed);
        Assert.Null(expired.NextSameIrp);

        // The completion turns up after the operation was published: it is just an unmatched completion now.
        Assert.False(correlator.Complete(Irp, timestampQpc: 150, info: 9, status: 0, nowTick: 1001));
        Assert.False(expired.Completed);
        Assert.Equal(0UL, expired.CompletionInfo);
        Assert.Equal(1, correlator.EarlyCompletionCount);

        // A newer operation on the recycled IRP does not pick up that stale completion...
        PendingIo next = Op(Irp, qpc: 300);
        correlator.Begin(next, nowTick: 1002);
        Assert.False(next.Completed);

        // ...but does get its own.
        Assert.True(correlator.Complete(Irp, timestampQpc: 350, info: 11, status: 0, nowTick: 1003));
        Assert.Equal(11UL, next.CompletionInfo);
    }

    [Fact]
    public void HoldExpiry_OfTheOlderOfTwoBeginsOnOneIrp_LeavesTheNewerOneMatchable()
    {
        var correlator = new IoCorrelator(holdMs: 1000);
        PendingIo older = Op(Irp, qpc: 100);
        PendingIo newer = Op(Irp, qpc: 300);
        correlator.Begin(older, nowTick: 0);
        correlator.Begin(newer, nowTick: 500);

        Assert.Same(older, Assert.Single(Release(correlator, nowTick: 1000)));
        Assert.Null(older.NextSameIrp);

        // The older operation's late completion was logged before the newer began, so it is not the newer's.
        Assert.False(correlator.Complete(Irp, timestampQpc: 200, info: 1, status: 0, nowTick: 1001));
        Assert.False(newer.Completed);

        Assert.True(correlator.Complete(Irp, timestampQpc: 350, info: 2, status: 0, nowTick: 1002));
        Assert.Equal(2UL, newer.CompletionInfo);
        Assert.Same(newer, Assert.Single(Release(correlator, nowTick: 1002)));
    }

    [Fact]
    public void Release_FlushAll_ReleasesEverythingAndForgetsEarlyCompletions()
    {
        var correlator = new IoCorrelator();
        PendingIo first = Op(Irp, qpc: 100);
        PendingIo second = Op(OtherIrp, qpc: 110);
        correlator.Begin(first, nowTick: 0);
        correlator.Begin(second, nowTick: 0);
        const ulong thirdIrp = 0xFFFF_9A00_1234_9A00;
        correlator.Complete(thirdIrp, timestampQpc: 50, info: 3, status: 0, nowTick: 0);

        Assert.Equal(new[] { first, second }, Release(correlator, nowTick: 0, flushAll: true));

        Assert.False(first.Completed);
        Assert.False(second.Completed);
        Assert.Equal(0, correlator.PendingCount);
        Assert.Equal(0, correlator.EarlyCompletionCount);

        // Nothing is still waiting for a completion...
        Assert.False(correlator.Complete(Irp, timestampQpc: 150, info: 1, status: 0, nowTick: 1));
        Assert.False(first.Completed);

        // ...and the stored completion is gone (it would otherwise match this begin).
        PendingIo third = Op(thirdIrp, qpc: 40);
        correlator.Begin(third, nowTick: 1);
        Assert.False(third.Completed);
    }

    [Fact]
    public void Release_BeyondMaxPending_ReleasesTheOldestWithoutWaiting()
    {
        var correlator = new IoCorrelator(maxPending: 2);
        PendingIo first = Op(1, qpc: 100);
        PendingIo second = Op(2, qpc: 110);
        PendingIo third = Op(3, qpc: 120);
        correlator.Begin(first, nowTick: 0);
        correlator.Begin(second, nowTick: 0);
        correlator.Begin(third, nowTick: 0);

        Assert.Same(first, Assert.Single(Release(correlator, nowTick: 0)));

        Assert.False(first.Completed);
        Assert.Equal(2, correlator.PendingCount);
        Assert.False(correlator.Complete(1, timestampQpc: 150, info: 0, status: 0, nowTick: 0)); // no longer waiting
        Assert.True(correlator.Complete(2, timestampQpc: 150, info: 0, status: 0, nowTick: 0));
    }

    [Fact]
    public void EarlyCompletion_IsKeptForEarlyKeepMs()
    {
        var correlator = new IoCorrelator(earlyKeepMs: 1000);
        correlator.Complete(Irp, timestampQpc: 100, info: 1, status: 0, nowTick: 0);

        Assert.Empty(Release(correlator, nowTick: 999));
        Assert.Equal(1, correlator.EarlyCompletionCount);

        PendingIo op = Op(Irp, qpc: 50);
        correlator.Begin(op, nowTick: 999);

        Assert.True(op.Completed);
    }

    [Fact]
    public void EarlyCompletion_ExpiresAfterEarlyKeepMs()
    {
        var correlator = new IoCorrelator(earlyKeepMs: 1000);
        correlator.Complete(Irp, timestampQpc: 100, info: 1, status: 0, nowTick: 0);

        Release(correlator, nowTick: 1000);

        Assert.Equal(0, correlator.EarlyCompletionCount);
        PendingIo op = Op(Irp, qpc: 50);
        correlator.Begin(op, nowTick: 1001);
        Assert.False(op.Completed);
    }

    [Fact]
    public void EarlyCompletions_BeyondMaxEarly_DropTheOldest()
    {
        var correlator = new IoCorrelator(maxEarly: 2);
        correlator.Complete(1, timestampQpc: 100, info: 1, status: 0, nowTick: 0);
        correlator.Complete(2, timestampQpc: 100, info: 2, status: 0, nowTick: 0);
        correlator.Complete(3, timestampQpc: 100, info: 3, status: 0, nowTick: 0);

        Assert.Equal(2, correlator.EarlyCompletionCount);

        PendingIo first = Op(1, qpc: 50);
        PendingIo second = Op(2, qpc: 50);
        PendingIo third = Op(3, qpc: 50);
        correlator.Begin(first, nowTick: 0);
        correlator.Begin(second, nowTick: 0);
        correlator.Begin(third, nowTick: 0);

        Assert.False(first.Completed);
        Assert.Equal(2UL, second.CompletionInfo);
        Assert.Equal(3UL, third.CompletionInfo);
    }

    [Fact]
    public void EarlyCompletion_IsUsedOnlyOnce()
    {
        var correlator = new IoCorrelator();
        correlator.Complete(Irp, timestampQpc: 100, info: 1, status: 0, nowTick: 0);
        PendingIo first = Op(Irp, qpc: 50);
        PendingIo second = Op(Irp, qpc: 60);

        correlator.Begin(first, nowTick: 0);
        correlator.Begin(second, nowTick: 0);

        Assert.True(first.Completed);
        Assert.False(second.Completed);
    }

    [Fact]
    public void EarlyCompletions_ArrivingOutOfTimestampOrder_StillGoToTheEarliestAtOrAfterEachBegin()
    {
        var correlator = new IoCorrelator();
        foreach (long qpc in new long[] { 500, 100, 300, 200, 400 })
            correlator.Complete(Irp, qpc, info: (ulong)qpc, status: 0, nowTick: 0);

        foreach ((long begin, ulong expected) in new[] { (250L, 300UL), (50L, 100UL), (150L, 200UL), (450L, 500UL), (350L, 400UL) })
        {
            PendingIo op = Op(Irp, begin);
            correlator.Begin(op, nowTick: 0);
            Assert.Equal(expected, op.CompletionInfo);
        }

        Assert.Equal(0, correlator.EarlyCompletionCount);
    }

    [Fact]
    public void EarlyCompletion_TakenFromTheMiddleOfAChain_LeavesTheRestUsable()
    {
        var correlator = new IoCorrelator(earlyKeepMs: 1000);
        correlator.Complete(Irp, timestampQpc: 100, info: 1, status: 0, nowTick: 0);
        correlator.Complete(Irp, timestampQpc: 200, info: 2, status: 0, nowTick: 0);
        correlator.Complete(Irp, timestampQpc: 300, info: 3, status: 0, nowTick: 10);

        PendingIo middle = Op(Irp, qpc: 150);
        correlator.Begin(middle, nowTick: 10);
        Assert.Equal(2UL, middle.CompletionInfo);
        Assert.Equal(2, correlator.EarlyCompletionCount);

        PendingIo last = Op(Irp, qpc: 250);
        correlator.Begin(last, nowTick: 10);
        Assert.Equal(3UL, last.CompletionInfo);

        Release(correlator, nowTick: 1000); // the first completion expires; it is the only one left
        Assert.Equal(0, correlator.EarlyCompletionCount);
        PendingIo first = Op(Irp, qpc: 50);
        correlator.Begin(first, nowTick: 1000);
        Assert.False(first.Completed);
    }

    [Fact]
    public void EarlyCompletions_ExpireInArrivalOrder_WhateverTheirTimestamps()
    {
        var correlator = new IoCorrelator(earlyKeepMs: 1000);
        correlator.Complete(Irp, timestampQpc: 300, info: 3, status: 0, nowTick: 0);
        correlator.Complete(Irp, timestampQpc: 100, info: 1, status: 0, nowTick: 500);
        correlator.Complete(OtherIrp, timestampQpc: 200, info: 2, status: 0, nowTick: 600);

        Release(correlator, nowTick: 1000);

        Assert.Equal(2, correlator.EarlyCompletionCount);
        PendingIo afterBoth = Op(Irp, qpc: 250);
        correlator.Begin(afterBoth, nowTick: 1000);
        Assert.False(afterBoth.Completed); // 300 arrived first and expired first
        PendingIo early = Op(Irp, qpc: 50);
        correlator.Begin(early, nowTick: 1000);
        Assert.Equal(1UL, early.CompletionInfo);

        Release(correlator, nowTick: 1600);
        Assert.Equal(0, correlator.EarlyCompletionCount);
    }

    [Fact]
    public void EarlyCompletions_BeyondTheCapPerAddress_DropTheEarliestLogged()
    {
        var correlator = new IoCorrelator(maxEarlyPerIrp: 2);
        correlator.Complete(Irp, timestampQpc: 200, info: 2, status: 0, nowTick: 0);
        correlator.Complete(Irp, timestampQpc: 300, info: 3, status: 0, nowTick: 0);
        correlator.Complete(OtherIrp, timestampQpc: 50, info: 9, status: 0, nowTick: 0);
        correlator.Complete(Irp, timestampQpc: 100, info: 1, status: 0, nowTick: 0); // the earliest: dropped at once
        correlator.Complete(Irp, timestampQpc: 400, info: 4, status: 0, nowTick: 0); // drops 200

        Assert.Equal(3, correlator.EarlyCompletionCount);
        PendingIo first = Op(Irp, qpc: 50);
        correlator.Begin(first, nowTick: 0);
        Assert.Equal(3UL, first.CompletionInfo);
        PendingIo other = Op(OtherIrp, qpc: 10);
        correlator.Begin(other, nowTick: 0);
        Assert.Equal(9UL, other.CompletionInfo); // other addresses keep theirs
    }

    [Fact]
    public void PendingBeginsOnOneIrp_LeavingFromAnyPosition_KeepTheChainIntact()
    {
        var correlator = new IoCorrelator();
        PendingIo[] ops = [Op(Irp, qpc: 100), Op(Irp, qpc: 300), Op(Irp, qpc: 200), Op(Irp, qpc: 400)]; // one arrives out of order
        foreach (PendingIo op in ops)
            correlator.Begin(op, nowTick: 0);

        // Middle, latest, earliest, then the last one left.
        Assert.True(correlator.Complete(Irp, timestampQpc: 250, info: 200, status: 0, nowTick: 0));
        Assert.True(correlator.Complete(Irp, timestampQpc: 450, info: 400, status: 0, nowTick: 0));
        Assert.True(correlator.Complete(Irp, timestampQpc: 150, info: 100, status: 0, nowTick: 0));
        Assert.True(correlator.Complete(Irp, timestampQpc: 350, info: 300, status: 0, nowTick: 0));

        Assert.All(ops, op => Assert.Equal((ulong)op.TimestampQpc, op.CompletionInfo));
        Assert.All(ops, op => Assert.Null(op.NextSameIrp));
        Assert.All(ops, op => Assert.Null(op.PrevSameIrp));
        Assert.False(correlator.Complete(Irp, timestampQpc: 500, info: 0, status: 0, nowTick: 0)); // nothing left waiting
        Assert.Equal(ops, Release(correlator, nowTick: 0));
    }

    [Fact]
    public void HoldExpiry_OfTheOldestOfManyBeginsOnOneIrp_LeavesTheOthersMatchable()
    {
        var correlator = new IoCorrelator(holdMs: 1000);
        PendingIo oldest = Op(Irp, qpc: 100);
        PendingIo middle = Op(Irp, qpc: 200);
        PendingIo newest = Op(Irp, qpc: 300);
        correlator.Begin(oldest, nowTick: 0);
        correlator.Begin(middle, nowTick: 500);
        correlator.Begin(newest, nowTick: 500);

        Assert.Same(oldest, Assert.Single(Release(correlator, nowTick: 1000)));

        Assert.True(correlator.Complete(Irp, timestampQpc: 250, info: 2, status: 0, nowTick: 1000));
        Assert.True(correlator.Complete(Irp, timestampQpc: 350, info: 3, status: 0, nowTick: 1000));
        Assert.Equal(2UL, middle.CompletionInfo);
        Assert.Equal(3UL, newest.CompletionInfo);
        Assert.False(oldest.Completed);
    }

    // ---- other operations that reuse the address (starts reported through ForeignBegin) ----

    [Fact]
    public void ForeignStart_BetweenABeginAndACompletion_KeepsThatCompletionFromIt()
    {
        // A read on E: is pending; an open on C: reuses its address and fails, and that failure is
        // delivered before the read's own completion, which sits in another CPU's buffer.
        var correlator = new IoCorrelator();
        PendingIo read = Op(Irp, qpc: 110);
        correlator.Begin(read, nowTick: 0);
        correlator.ForeignBegin(Irp, timestampQpc: 130);

        Assert.False(correlator.Complete(Irp, timestampQpc: 140, info: 0, status: StatusObjectNameNotFound, nowTick: 0));
        Assert.False(read.Completed);

        Assert.True(correlator.Complete(Irp, timestampQpc: 120, info: 4096, status: 0, nowTick: 0));
        Assert.Equal(0, read.NtStatus);
        Assert.Equal(4096UL, read.CompletionInfo);
    }

    [Fact]
    public void ForeignStart_AfterALostCompletion_LeavesTheOperationUnknown()
    {
        var correlator = new IoCorrelator(holdMs: 1000);
        PendingIo write = Op(Irp, qpc: 110);
        write.Op = RawFileOp.Write;
        write.IoSize = 65536;
        correlator.Begin(write, nowTick: 0);
        correlator.ForeignBegin(Irp, timestampQpc: 10_100); // the write's own completion was lost

        Assert.False(correlator.Complete(Irp, timestampQpc: 10_110, info: 0, status: StatusObjectNameNotFound, nowTick: 0));

        PendingIo released = Assert.Single(Release(correlator, nowTick: 1000));
        FileActivityEvent? ev = FileIoClassifier.Classify(released, sequence: 1);
        Assert.NotNull(ev);
        Assert.Equal(IoStatus.Unknown, ev.Status);
        Assert.Equal(65536L, ev.Bytes);
    }

    [Fact]
    public void LateBegin_DoesNotTakeTheStoredCompletionOfAForeignReuse()
    {
        // The reuse's start and completion are delivered first; the read and its own completion sit in
        // a buffer flushed later.
        var correlator = new IoCorrelator();
        correlator.ForeignBegin(Irp, timestampQpc: 250);
        Assert.False(correlator.Complete(Irp, timestampQpc: 300, info: 24, status: 0, nowTick: 0));

        PendingIo read = Op(Irp, qpc: 100);
        correlator.Begin(read, nowTick: 0);
        Assert.False(read.Completed);

        Assert.True(correlator.Complete(Irp, timestampQpc: 150, info: 65536, status: 0, nowTick: 0));
        Assert.Equal(65536UL, read.CompletionInfo);
    }

    [Fact]
    public void ForeignStarts_ArrivingOutOfOrder_TheEarliestAfterTheBeginCounts()
    {
        var correlator = new IoCorrelator();
        PendingIo read = Op(Irp, qpc: 100);
        correlator.Begin(read, nowTick: 0);
        correlator.ForeignBegin(Irp, timestampQpc: 300);
        correlator.ForeignBegin(Irp, timestampQpc: 150); // from another CPU's buffer, delivered later

        Assert.False(correlator.Complete(Irp, timestampQpc: 200, info: 2, status: 0, nowTick: 0)); // the 150 operation's
        Assert.True(correlator.Complete(Irp, timestampQpc: 120, info: 1, status: 0, nowTick: 0));
        Assert.Equal(1UL, read.CompletionInfo);
    }

    [Fact]
    public void ForeignStarts_LoggedBeforeTheBeginOrAfterTheCompletion_DoNotGetInTheWay()
    {
        var correlator = new IoCorrelator();
        correlator.ForeignBegin(Irp, timestampQpc: 50);
        PendingIo read = Op(Irp, qpc: 100);
        correlator.Begin(read, nowTick: 0);
        correlator.ForeignBegin(Irp, timestampQpc: 300); // the next use, delivered before this completion

        Assert.True(correlator.Complete(Irp, timestampQpc: 200, info: 7, status: 0, nowTick: 0));
        Assert.Equal(7UL, read.CompletionInfo);
    }

    [Fact]
    public void LaterMonitoredStart_KeepsAStrayCompletionFromTheEarlierOperation()
    {
        // The first read's completion is late; the second read on the address completes; then comes a
        // completion of a third use of the address whose start has not arrived yet.
        var correlator = new IoCorrelator();
        PendingIo first = Op(Irp, qpc: 100);
        PendingIo second = Op(Irp, qpc: 300);
        correlator.Begin(first, nowTick: 0);
        correlator.Begin(second, nowTick: 0);
        Assert.True(correlator.Complete(Irp, timestampQpc: 350, info: 2, status: 0, nowTick: 0));

        Assert.False(correlator.Complete(Irp, timestampQpc: 450, info: 3, status: 0, nowTick: 0));
        Assert.False(first.Completed);

        Assert.True(correlator.Complete(Irp, timestampQpc: 150, info: 1, status: 0, nowTick: 0));
        Assert.Equal(1UL, first.CompletionInfo);
        Assert.Equal(2UL, second.CompletionInfo);
    }

    [Fact]
    public void LateBegin_IsLimitedByALaterBeginThatIsStillWaiting()
    {
        // The second read (300) arrived first and still waits. Two foreign operations followed it, 320 and
        // 400, delivered out of order, and the completion of the 320 one is stored. The latest start on
        // the address (400) is not enough to keep the first read from taking it; the second read's start is.
        var correlator = new IoCorrelator();
        PendingIo second = Op(Irp, qpc: 300);
        correlator.Begin(second, nowTick: 0);
        correlator.ForeignBegin(Irp, timestampQpc: 400);
        correlator.ForeignBegin(Irp, timestampQpc: 320);
        Assert.False(correlator.Complete(Irp, timestampQpc: 350, info: 3, status: 0, nowTick: 0));

        PendingIo first = Op(Irp, qpc: 100);
        correlator.Begin(first, nowTick: 0);
        Assert.False(first.Completed);

        Assert.True(correlator.Complete(Irp, timestampQpc: 150, info: 1, status: 0, nowTick: 0));
        Assert.Equal(1UL, first.CompletionInfo);
        Assert.False(second.Completed);
    }

    [Fact]
    public void ForeignStart_BetweenARenameAndAPath_KeepsThatPathFromIt()
    {
        var correlator = new IoCorrelator();
        PendingIo rename = Rename(irp: 0x10, qpc: 100);
        correlator.Begin(rename, nowTick: 0);
        correlator.ForeignBegin(0x10, timestampQpc: 130); // a rename on C: reuses the address

        Assert.False(correlator.Annotate(0x10, 140, @"C:\Users\someone\renamed.txt", nowTick: 0));
        Assert.True(rename.AwaitingPath);

        Assert.True(correlator.Annotate(0x10, 120, @"E:\new.bin", nowTick: 0));
        Assert.Equal(@"E:\new.bin", rename.TargetPath);
    }

    [Fact]
    public void ForeignStart_KeepsAPathFromAnOperationThatWasNotWaitingForOne()
    {
        var correlator = new IoCorrelator();
        PendingIo op = Op(irp: 0x10, qpc: 100);
        correlator.Begin(op, nowTick: 0);
        correlator.ForeignBegin(0x10, timestampQpc: 130);

        Assert.False(correlator.Annotate(0x10, 140, @"C:\other.txt", nowTick: 0));

        Assert.Null(op.TargetPath);
        Assert.Equal(1, correlator.EarlyPathCount);
    }

    [Fact]
    public void LateRename_DoesNotTakeTheStoredPathOfAForeignReuse()
    {
        var correlator = new IoCorrelator();
        correlator.ForeignBegin(0x10, timestampQpc: 250);
        Assert.False(correlator.Annotate(0x10, 300, @"C:\other.txt", nowTick: 0));

        PendingIo rename = Rename(irp: 0x10, qpc: 100);
        correlator.Begin(rename, nowTick: 0);
        Assert.Null(rename.TargetPath);
        Assert.True(rename.AwaitingPath);

        Assert.True(correlator.Annotate(0x10, 150, @"E:\new.bin", nowTick: 0));
        Assert.Equal(@"E:\new.bin", rename.TargetPath);
    }

    [Fact]
    public void StartTable_StaysBoundedAndKeepsTheLatestAddresses()
    {
        var correlator = new IoCorrelator(maxStartAddresses: 4);
        for (ulong n = 1; n <= 100; n++)
            correlator.ForeignBegin(n * 0x100, timestampQpc: 1000 + (long)n);

        Assert.InRange(correlator.StartAddressCount, 4, 8);

        // The last address is still known: a late begin there does not take the reuse's completion.
        Assert.False(correlator.Complete(100 * 0x100, timestampQpc: 1200, info: 9, status: 0, nowTick: 0));
        PendingIo late = Op(100 * 0x100, qpc: 50);
        correlator.Begin(late, nowTick: 0);
        Assert.False(late.Completed);
    }

    [Fact]
    public void ForeignBegin_WithoutIrp_IsIgnored_AndFlushAllForgetsStarts()
    {
        var correlator = new IoCorrelator();
        correlator.ForeignBegin(0, timestampQpc: 100);
        Assert.Equal(0, correlator.StartAddressCount);

        correlator.ForeignBegin(Irp, timestampQpc: 250);
        correlator.Begin(Op(OtherIrp, qpc: 260), nowTick: 0);
        Assert.Equal(2, correlator.StartAddressCount);

        Release(correlator, nowTick: 0, flushAll: true);
        Assert.Equal(0, correlator.StartAddressCount);
    }

    // ---- cost on recycled request addresses ----

    [Fact]
    public void EarlyCompletions_ManyOnOneAddress_AreStoredAndPurgedInLinearTime()
    {
        // Completions of unmonitored I/O pile up on a few recycled addresses. Purging them used to
        // walk the address's whole chain for each one: 200,000 over 8 addresses took about 15 s.
        const int count = 200_000;
        var correlator = new IoCorrelator(earlyKeepMs: 1000, maxEarly: count, maxEarlyPerIrp: count);
        var watch = Stopwatch.StartNew();

        for (int i = 0; i < count; i++)
            correlator.Complete(Irp, timestampQpc: i, info: 1, status: 0, nowTick: i / 1000);
        Assert.Equal(count, correlator.EarlyCompletionCount);
        Release(correlator, nowTick: 1099); // the first half, received at ticks 0-99, has expired
        Assert.Equal(100_000, correlator.EarlyCompletionCount);
        Release(correlator, nowTick: 1200);

        watch.Stop();
        Assert.Equal(0, correlator.EarlyCompletionCount);
        AssertFast(watch);
    }

    [Fact]
    public void MonitoredBegins_OnAnAddressFullOfStoredCompletions_DoNotWalkThemAll()
    {
        // A copy from C: to a pendrive can use one address for both; every begin on the pendrive used
        // to walk all the C: completions kept for that address.
        const int stored = 200_000;
        const int begins = 50_000;
        var correlator = new IoCorrelator(maxEarlyPerIrp: stored);
        for (int i = 0; i < stored; i++)
            correlator.Complete(Irp, timestampQpc: i, info: 1, status: 0, nowTick: 0);

        var watch = Stopwatch.StartNew();
        for (int i = 0; i < begins; i++)
        {
            long qpc = stored + (2L * i);
            PendingIo op = Op(Irp, qpc);
            correlator.Begin(op, nowTick: 0);
            Assert.False(op.Completed); // all stored completions were logged before it began
            Assert.True(correlator.Complete(Irp, qpc + 1, info: 2, status: 0, nowTick: 0));
        }

        watch.Stop();
        Assert.Equal(begins, Release(correlator, nowTick: 0).Count);
        Assert.Equal(stored, correlator.EarlyCompletionCount);
        AssertFast(watch);
    }

    [Fact]
    public void PendingOperations_ManyOnOneAddress_ExpireInLinearTime()
    {
        // Lost completions leave operations waiting on a recycled address; expiring the oldest used
        // to walk the whole chain from the newest.
        const int count = 150_000;
        var correlator = new IoCorrelator(holdMs: 1000, maxPending: count);
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
            correlator.Begin(Op(Irp, qpc: i), nowTick: 0);

        List<PendingIo> released = Release(correlator, nowTick: 1000);

        watch.Stop();
        Assert.Equal(count, released.Count);
        Assert.All(released, op => Assert.False(op.Completed));
        Assert.Equal(0, correlator.PendingCount);
        Assert.False(correlator.Complete(Irp, timestampQpc: count, info: 1, status: 0, nowTick: 1000)); // nothing left waiting
        AssertFast(watch);
    }

    [Fact]
    public void ForeignStarts_OnAnAddressWithManyWaitingOperations_DoNotWalkThem()
    {
        // Lost completions can leave many operations waiting on a hot address while other I/O keeps
        // reusing it; each foreign start only has to limit the latest of them.
        const int waiting = 100_000;
        const int foreign = 100_000;
        var correlator = new IoCorrelator(maxPending: waiting);
        for (int i = 0; i < waiting; i++)
            correlator.Begin(Op(Irp, qpc: 2L * i), nowTick: 0);

        var watch = Stopwatch.StartNew();
        for (int i = 0; i < foreign; i++)
        {
            long qpc = (2L * waiting) + (2L * i);
            correlator.ForeignBegin(Irp, qpc);
            Assert.False(correlator.Complete(Irp, qpc + 1, info: 0, status: 0, nowTick: 0)); // the foreign operation's own
        }

        watch.Stop();
        Assert.Equal(waiting, correlator.PendingCount);
        AssertFast(watch);
    }

    // ---- path annotations (rename targets) ----

    [Fact]
    public void Annotate_AttachesThePathToAnOperationWaitingForIt()
    {
        var correlator = new IoCorrelator();
        PendingIo rename = Rename(irp: 0x10, qpc: 100);
        correlator.Begin(rename, nowTick: 0);
        correlator.Complete(0x10, 110, info: 0, status: 0, nowTick: 0);

        Assert.Empty(Release(correlator, nowTick: 1)); // complete, but still waiting for its path

        Assert.True(correlator.Annotate(0x10, 105, @"E:\new.bin", nowTick: 1));

        PendingIo released = Assert.Single(Release(correlator, nowTick: 2));
        Assert.Same(rename, released);
        Assert.Equal(@"E:\new.bin", released.TargetPath);
        Assert.False(released.AwaitingPath);
    }

    [Fact]
    public void Annotate_ArrivingBeforeTheBegin_IsMatchedWhenTheBeginArrives()
    {
        var correlator = new IoCorrelator();

        Assert.False(correlator.Annotate(0x10, 120, @"E:\new.bin", nowTick: 0));
        Assert.Equal(1, correlator.EarlyPathCount);

        PendingIo rename = Rename(irp: 0x10, qpc: 100);
        correlator.Begin(rename, nowTick: 0);

        Assert.Equal(@"E:\new.bin", rename.TargetPath);
        Assert.False(rename.AwaitingPath);
    }

    [Fact]
    public void Annotate_LoggedBeforeTheBegin_IsNotMatched()
    {
        var correlator = new IoCorrelator();
        correlator.Annotate(0x10, 50, @"E:\stale.bin", nowTick: 0); // an older use of the same address

        PendingIo rename = Rename(irp: 0x10, qpc: 100);
        correlator.Begin(rename, nowTick: 0);

        Assert.Null(rename.TargetPath);
        Assert.True(rename.AwaitingPath);
    }

    [Fact]
    public void Annotate_OperationNotWaitingForAPath_StillTakesItWhilePending()
    {
        var correlator = new IoCorrelator();
        PendingIo op = Op(irp: 0x10, qpc: 100);
        correlator.Begin(op, nowTick: 0);

        Assert.True(correlator.Annotate(0x10, 105, @"E:\new.bin", nowTick: 0));
        Assert.Equal(@"E:\new.bin", op.TargetPath);
    }

    [Fact]
    public void AwaitingPath_ExpiresAfterTheHoldTime_AndALatePathCannotCorruptAnything()
    {
        var correlator = new IoCorrelator(holdMs: 100);
        PendingIo rename = Rename(irp: 0x10, qpc: 100);
        correlator.Begin(rename, nowTick: 0);
        correlator.Complete(0x10, 110, info: 0, status: 0, nowTick: 0);

        Assert.Empty(Release(correlator, nowTick: 99));
        PendingIo released = Assert.Single(Release(correlator, nowTick: 100));
        Assert.Null(released.TargetPath);
        Assert.False(released.AwaitingPath);

        Assert.False(correlator.Annotate(0x10, 120, @"E:\late.bin", nowTick: 101));
        Assert.Null(released.TargetPath);
        Assert.Equal(0, correlator.PendingCount);
    }

    [Fact]
    public void Begin_WithoutIrp_NeverWaitsForAPath()
    {
        var correlator = new IoCorrelator();
        PendingIo rename = Rename(irp: 0, qpc: 100);

        correlator.Begin(rename, nowTick: 0);

        Assert.False(rename.AwaitingPath);
        Assert.Same(rename, Assert.Single(Release(correlator, nowTick: 0)));
    }

    [Fact]
    public void Release_FlushAll_ForgetsEarlyPaths()
    {
        var correlator = new IoCorrelator();
        correlator.Annotate(0x10, 120, @"E:\new.bin", nowTick: 0);

        Release(correlator, nowTick: 0, flushAll: true);

        Assert.Equal(0, correlator.EarlyPathCount);
    }

    [Fact]
    public void TwoRenamesOnOneIrp_EachTakesThePathLoggedDuringIt()
    {
        var correlator = new IoCorrelator(holdMs: 1000);
        PendingIo older = Rename(irp: 0x10, qpc: 100);
        PendingIo newer = Rename(irp: 0x10, qpc: 300);
        correlator.Begin(older, nowTick: 0);
        correlator.Begin(newer, nowTick: 0);

        Assert.True(correlator.Annotate(0x10, 350, @"E:\second.bin", nowTick: 0));
        Assert.True(correlator.Annotate(0x10, 150, @"E:\first.bin", nowTick: 0));

        Assert.Equal(@"E:\first.bin", older.TargetPath);
        Assert.Equal(@"E:\second.bin", newer.TargetPath);
        Assert.Null(older.NextAwaitingPath);
        Assert.Null(newer.PrevAwaitingPath);
    }

    [Fact]
    public void AwaitingPath_ExpiryOfTheOlderOfTwoRenamesOnOneIrp_LeavesTheNewerOneWaiting()
    {
        var correlator = new IoCorrelator(holdMs: 1000);
        PendingIo older = Rename(irp: 0x10, qpc: 100);
        PendingIo newer = Rename(irp: 0x10, qpc: 300);
        correlator.Begin(older, nowTick: 0);
        correlator.Begin(newer, nowTick: 500);

        Assert.Same(older, Assert.Single(Release(correlator, nowTick: 1000)));

        Assert.False(correlator.Annotate(0x10, 150, @"E:\late.bin", nowTick: 1000)); // logged before the newer began
        Assert.True(correlator.Annotate(0x10, 320, @"E:\new.bin", nowTick: 1000));
        Assert.Null(older.TargetPath);
        Assert.Equal(@"E:\new.bin", newer.TargetPath);
    }

    private static PendingIo Rename(ulong irp, long qpc)
    {
        PendingIo io = Op(irp, qpc);
        io.Op = RawFileOp.Rename;
        io.AwaitingPath = true;
        return io;
    }

    private static PendingIo Op(ulong irp, long qpc) => new()
    {
        Op = RawFileOp.Read,
        Irp = irp,
        TimestampQpc = qpc,
        Path = @"E:\data.bin",
        Drive = 'E',
    };

    private static List<PendingIo> Release(IoCorrelator correlator, long nowTick, bool flushAll = false)
    {
        var released = new List<PendingIo>();
        correlator.Release(nowTick, released.Add, flushAll);
        return released;
    }

    /// <summary>Generous on purpose: linear work takes milliseconds, the old per-entry chain walks took 10 s or more.</summary>
    private static void AssertFast(Stopwatch watch) =>
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed.TotalMilliseconds:N0} ms");
}
