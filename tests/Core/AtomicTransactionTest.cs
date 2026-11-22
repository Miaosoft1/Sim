namespace Miaosoft.Sim.Core.Tests;

[TestClass]
public sealed class AtomicTransactionTest
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void StaleReadRejectsAllStagedWrites()
    {
        var dependency = new AtomicCell<int>(1);
        var first = new AtomicCell<int>(10);
        var second = new AtomicCell<int>(20);
        var transaction = new AtomicTransaction();
        Assert.AreEqual(1, transaction.Read(dependency));
        transaction.Stage(first, 11);
        transaction.Stage(second, 21);
        dependency.Set(2);

        Assert.ThrowsExactly<InvalidOperationException>(transaction.Commit);
        Assert.AreEqual(10, first.Value);
        Assert.AreEqual(20, second.Value);
        Assert.AreEqual(2, dependency.Value);
        TestContext.WriteLine("Stale dependency rejected both staged writes without a partial update.");
    }

    [TestMethod]
    public void UpdatesComposeWithoutPublishingBeforeCommit()
    {
        var cell = new AtomicCell<int>(3);
        var transaction = new AtomicTransaction();
        transaction.Update(cell, value => value + 4);
        transaction.Update(cell, value => value * 2);

        Assert.AreEqual(3, cell.Value);
        Assert.AreEqual(14, transaction.Read(cell));
        transaction.Commit();
        Assert.AreEqual(14, cell.Value);
        TestContext.WriteLine("Both updates were composed privately and published as one cell version.");
    }

    [TestMethod]
    public void FailedUpdateAndStaleCommitCannotPublishPartialResults()
    {
        var cell = new AtomicCell<int>(3);
        var other = new AtomicCell<int>(5);
        var transaction = new AtomicTransaction();
        transaction.Update(cell, value => value + 4);
        Assert.ThrowsExactly<OverflowException>(() => transaction.Update<int>(cell, _ => throw new OverflowException()));
        Assert.AreEqual(7, transaction.Read(cell));
        transaction.Stage(other, 6);
        cell.Set(9);

        Assert.ThrowsExactly<InvalidOperationException>(transaction.Commit);
        Assert.AreEqual(9, cell.Value);
        Assert.AreEqual(5, other.Value);
        TestContext.WriteLine("A failed updater preserved staged state, and a stale commit published nothing.");
    }

    [TestMethod]
    public void UpdaterCannotMutateOrCommitItsTransactionReentrantly()
    {
        var cell = new AtomicCell<int>(3);
        var transaction = new AtomicTransaction();
        transaction.Stage(cell, 4);
        Assert.ThrowsExactly<InvalidOperationException>(() => transaction.Update(cell, value =>
        {
            transaction.Commit();
            return value + 1;
        }));
        Assert.ThrowsExactly<InvalidOperationException>(() => transaction.Update(cell, value =>
        {
            transaction.Stage(cell, value + 1);
            return value + 2;
        }));
        Assert.ThrowsExactly<InvalidOperationException>(() => transaction.Update(cell, value =>
            value + transaction.Read(new AtomicCell<int>(1))));
        Assert.AreEqual(4, transaction.Read(cell));
        transaction.Commit();
        Assert.AreEqual(4, cell.Value);
        TestContext.WriteLine("Reentrant writes and commits were rejected without changing the prior staged value.");
    }

    [TestMethod]
    public void ScopeCancellationIsVisibleToEveryPostCommitCallback()
    {
        using var context = new Context();
        using EventScope first = context.CreateEventScope();
        using EventScope second = context.CreateEventScope();
        bool callbackRan = false;
        using CancellationTokenRegistration registration = first.Token.Register(() =>
        {
            callbackRan = true;
            Assert.IsTrue(second.IsCancellationRequested);
        });

        var transaction = new AtomicTransaction();
        first.StageCancellation(transaction);
        second.StageCancellation(transaction);
        transaction.Commit();
        Assert.IsTrue(first.IsCancellationRequested);
        Assert.IsTrue(second.IsCancellationRequested);
        Assert.IsFalse(callbackRan);
        first.Cancel();
        second.Cancel();
        Assert.IsTrue(callbackRan);
        TestContext.WriteLine("Both scopes were logically canceled before either cancellation callback.");
    }

    [TestMethod]
    public async Task CompetingTransactionsCannotBothCommitTheSameSnapshot()
    {
        var cell = new AtomicCell<int>(0);
        using var barrier = new Barrier(2);

        Task<bool> Attempt() => Task.Run(() =>
        {
            var transaction = new AtomicTransaction();
            int current = transaction.Read(cell);
            transaction.Stage(cell, current + 1);
            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Both transactions did not reach the commit barrier.");
            try
            {
                transaction.Commit();
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        });

        bool[] results = await Task.WhenAll(Attempt(), Attempt());
        Assert.AreEqual(1, results.Count(result => result));
        Assert.AreEqual(1, cell.Value);
        TestContext.WriteLine("One of two competing transactions committed; the other was rejected.");
    }
}
