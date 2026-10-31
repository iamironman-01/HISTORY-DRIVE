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
        Assert.Same(op, Assert.Single(Release(correlator, nowTick: 1)));
        Assert.Equal(0, correlator.EarlyCompletionCount); // the used completion is purged on release
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
}
