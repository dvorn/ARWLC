namespace ARWLC.Tests
{
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using System.Linq;
    using System.Threading;
    using System.Diagnostics.CodeAnalysis;

    using ARWLC;

    [ExcludeFromCodeCoverage]
    [TestClass]
    public class AsyncReaderWriterLockUnitTests
    {
        [TestMethod]
        public async Task Unlocked_PermitsWriterLock()
        {
            var rwl = new AsyncReaderWriterLock();
            await rwl.WriterLockAsync();
            var task = rwl.WriterLockAsync();
            await AssertEx.NeverCompletesAsync(task);
        }

        [TestMethod]
        public async Task Unlocked_PermitsMultipleReaderLocks()
        {
            var rwl = new AsyncReaderWriterLock();
            await rwl.ReaderLockAsync();
            await rwl.ReaderLockAsync();
        }

        [TestMethod]
        public async Task WriteLocked_PreventsAnotherWriterLock()
        {
            var rwl = new AsyncReaderWriterLock();
            await rwl.WriterLockAsync();
            var task = rwl.WriterLockAsync();
            await AssertEx.NeverCompletesAsync(task);
        }

        [TestMethod]
        public async Task WriteLocked_PreventsReaderLock()
        {
            var rwl = new AsyncReaderWriterLock();
            await rwl.WriterLockAsync();
            var task = rwl.ReaderLockAsync();
            await AssertEx.NeverCompletesAsync(task);
        }

        [TestMethod]
        public async Task WriteLocked_Unlocked_PermitsAnotherWriterLock()
        {
            var rwl = new AsyncReaderWriterLock();
            var firstWriteLockTaken = new TaskCompletionSource<object>();
            var releaseFirstWriteLock = new TaskCompletionSource<object>();
            var task = Task.Run(async () =>
            {
                using (await rwl.WriterLockAsync())
                {
                    firstWriteLockTaken.SetResult(null);
                    await releaseFirstWriteLock.Task;
                }
            });
            await firstWriteLockTaken.Task;
            var lockTask = rwl.WriterLockAsync();
            Assert.IsFalse(lockTask.IsCompleted);
            releaseFirstWriteLock.SetResult(null);
            await lockTask;
        }

        [TestMethod]
        public async Task ReadLocked_PreventsWriterLock()
        {
            var rwl = new AsyncReaderWriterLock();
            await rwl.ReaderLockAsync();
            var task = rwl.WriterLockAsync();
            await AssertEx.NeverCompletesAsync(task);
        }

        [TestMethod]
        public void WriterLock_PreCancelled_LockAvailable_SynchronouslyTakesLock()
        {
            var rwl = new AsyncReaderWriterLock();
            var token = new CancellationToken(true);

            var task = rwl.WriterLockAsync(token);

            Assert.IsTrue(task.IsCompleted);
            Assert.IsFalse(task.IsCanceled);
            Assert.IsFalse(task.IsFaulted);
        }

        [TestMethod]
        public void WriterLock_PreCancelled_LockNotAvailable_SynchronouslyCancels()
        {
            var rwl = new AsyncReaderWriterLock();
            var token = new CancellationToken(true);
            rwl.WriterLockAsync();

            var task = rwl.WriterLockAsync(token);

            Assert.IsTrue(task.IsCompleted);
            Assert.IsTrue(task.IsCanceled);
            Assert.IsFalse(task.IsFaulted);
        }

        [TestMethod]
        public void ReaderLock_PreCancelled_LockAvailable_SynchronouslyTakesLock()
        {
            var rwl = new AsyncReaderWriterLock();
            var token = new CancellationToken(true);

            var task = rwl.ReaderLockAsync(token);

            Assert.IsTrue(task.IsCompleted);
            Assert.IsFalse(task.IsCanceled);
            Assert.IsFalse(task.IsFaulted);
        }

        [TestMethod]
        public void ReaderLock_PreCancelled_LockNotAvailable_SynchronouslyCancels()
        {
            var rwl = new AsyncReaderWriterLock();
            var token = new CancellationToken(true);
            rwl.WriterLockAsync();

            var task = rwl.ReaderLockAsync(token);

            Assert.IsTrue(task.IsCompleted);
            Assert.IsTrue(task.IsCanceled);
            Assert.IsFalse(task.IsFaulted);
        }

        [TestMethod]
        public async Task WriteLocked_WriterLockCancelled_DoesNotTakeLockWhenUnlocked()
        {
            var rwl = new AsyncReaderWriterLock();
            using (await rwl.WriterLockAsync())
            {
                var cts = new CancellationTokenSource();
                var task = rwl.WriterLockAsync(cts.Token);
                cts.Cancel();
                await AssertEx.ThrowsExceptionAsync<OperationCanceledException>(() => task);
            }

            await rwl.WriterLockAsync();
        }

        [TestMethod]
        public async Task WriteLocked_ReaderLockCancelled_DoesNotTakeLockWhenUnlocked()
        {
            var rwl = new AsyncReaderWriterLock();
            using (await rwl.WriterLockAsync())
            {
                var cts = new CancellationTokenSource();
                var task = rwl.ReaderLockAsync(cts.Token);
                cts.Cancel();
                await AssertEx.ThrowsExceptionAsync<OperationCanceledException>(() => task);
            }

            await rwl.ReaderLockAsync();
        }

        [TestMethod]
        public async Task LockReleased_WriteTakesPriorityOverRead()
        {
            var rwl = new AsyncReaderWriterLock();
            Task writeLock, readLock;
            using (await rwl.WriterLockAsync())
            {
                readLock = rwl.ReaderLockAsync();
                writeLock = rwl.WriterLockAsync();
            }

            await writeLock;
            await AssertEx.NeverCompletesAsync(readLock);
        }

        [TestMethod]
        public async Task ReaderLocked_ReaderReleased_ReaderAndWriterWaiting_DoesNotReleaseReaderOrWriter()
        {
            var rwl = new AsyncReaderWriterLock();
            Task readLock, writeLock;
            await rwl.ReaderLockAsync();
            using (await rwl.ReaderLockAsync())
            {
                writeLock = rwl.WriterLockAsync();
                readLock = rwl.ReaderLockAsync();
            }

            await Task.WhenAll(AssertEx.NeverCompletesAsync(writeLock),
                AssertEx.NeverCompletesAsync(readLock));
        }

#if (false)
        [TestMethod]
        public void LoadTest()
        {
            Test.Async(async () =>
            {
                var rwl = new AsyncReaderWriterLock();
                var readKeys = new List<IDisposable>();
                for (int i = 0; i != 1000; ++i)
                    readKeys.Add(rwl.ReaderLock());
                var writeTask = Task.Run(() => { rwl.WriterLock().Dispose(); });
                var readTasks = new List<Task>();
                for (int i = 0; i != 100; ++i)
                    readTasks.Add(Task.Run(() => rwl.ReaderLock().Dispose()));
                await Task.Delay(1000);
                foreach (var readKey in readKeys)
                    readKey.Dispose();
                await writeTask;
                foreach (var readTask in readTasks)
                    await readTask;
            });
        }
#endif
    }
}
