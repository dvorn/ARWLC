namespace ARWLC
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Defines a lock that supports single writers and multiple readers.
    /// </summary>
    public class AsyncReaderWriterLock
    {
        private readonly Task<Releaser> readerReleaser;
        private readonly Task<Releaser> writerReleaser;

        private readonly List<Waiter> waitingWriters = new List<Waiter>();
        private readonly List<Waiter> waitingReaders = new List<Waiter>();

        private int status; // -1 writer locked, 0 idle, n > 0 number of reader locks

        /// <summary>
        /// Initializes a new instance of the <see cref="AsyncReaderWriterLock" /> class.
        /// </summary>
        public AsyncReaderWriterLock()
        {
            TaskCompletionSource<Releaser> tcs = new TaskCompletionSource<Releaser>();
            tcs.SetResult(new Releaser(this, false));
            this.readerReleaser = tcs.Task;

            tcs = new TaskCompletionSource<Releaser>();
            tcs.SetResult(new Releaser(this, true));
            this.writerReleaser = tcs.Task;
        }

        public Task<Releaser> ReaderLockAsync()
        {
            return this.ReaderLockAsync(CancellationToken.None);
        }

        /// <summary>
        /// ReaderLockAsync is used when a new reader wants in.
        /// </summary>
        /// <param name="token">The token.</param>
        /// <returns></returns>
        public Task<Releaser> ReaderLockAsync(CancellationToken token)
        {
            lock (this.waitingWriters)
            {
                if (this.status >= 0 && this.waitingWriters.Count == 0)
                {
                    ++this.status;
                    return this.readerReleaser;
                }

                Waiter waiter = new Waiter { Tcs = new TaskCompletionSource<Releaser>() };
                waiter.Registration = token.Register(() => this.OnReaderCancel(waiter));

                this.waitingReaders.Add(waiter);

                return waiter.Tcs.Task;
            }
        }

        public Task<Releaser> WriterLockAsync()
        {
            return this.WriterLockAsync(CancellationToken.None);
        }

        /// <summary>
        /// WriterLockAsync is used when a new writer wants in.
        /// </summary>
        /// <param name="token">The token.</param>
        /// <returns></returns>
        public Task<Releaser> WriterLockAsync(CancellationToken token)
        {
            lock (this.waitingWriters)
            {
                if (this.status == 0)
                {
                    this.status = -1;
                    return this.writerReleaser;
                }

                Waiter waiter = new Waiter { Tcs = new TaskCompletionSource<Releaser>() };
                waiter.Registration = token.Register(() => this.OnWriterCancel(waiter));

                this.waitingWriters.Add(waiter);

                return waiter.Tcs.Task;
            }
        }

        /// <summary>
        /// Called when an active reader completes its work.
        /// </summary>
        private void ReaderRelease()
        {
            Waiter toWake = null;

            lock (this.waitingWriters)
            {
                --this.status;
                if (this.status == 0 && this.waitingWriters.Count > 0)
                {
                    this.status = -1;

                    toWake = this.waitingWriters[0];
                    this.waitingWriters.RemoveAt(0);
                }
            }

            if (toWake != null)
            {
                toWake.Tcs.SetResult(new Releaser(this, true));
                toWake.Registration.Dispose();
            }
        }

        /// <summary>
        /// Called when an active writer completes its work.
        /// </summary>
        private void WriterRelease()
        {
            Waiter writerToWake = null;
            IEnumerable<Waiter> readersToWake = null;

            lock (this.waitingWriters)
            {
                if (this.waitingWriters.Count > 0)
                {
                    writerToWake = this.waitingWriters[0];
                    this.waitingWriters.RemoveAt(0);
                }
                else if (this.waitingReaders.Count > 0)
                {
                    this.status = this.waitingReaders.Count;

                    readersToWake = this.waitingReaders.ToArray();
                    this.waitingReaders.Clear();
                }
                else
                {
                    this.status = 0;
                }
            }

            if (writerToWake != null)
            {
                writerToWake.Tcs.SetResult(new Releaser(this, true));
                writerToWake.Registration.Dispose();
            }

            if (readersToWake != null)
            {
                foreach (var toWake in readersToWake)
                {
                    toWake.Tcs.SetResult(new Releaser(this, false));
                    toWake.Registration.Dispose();
                }
            }
        }

        private void OnReaderCancel(Waiter waiter)
        {
            lock (this.waitingWriters)
            {
                if (this.waitingReaders.Count > 0)
                {
                    this.waitingReaders.Remove(waiter);
                }
            }

            waiter.Tcs.TrySetCanceled();
            waiter.Registration.Dispose();
        }

        private void OnWriterCancel(Waiter waiter)
        {
            IEnumerable<Waiter> readersToWake = null;

            lock (this.waitingWriters)
            {
                if (this.waitingWriters.Count > 0)
                {
                    this.waitingWriters.Remove(waiter);
                }

                if (this.status > 0 && this.waitingWriters.Count == 0 && this.waitingReaders.Count > 0)
                {
                    this.status += this.waitingReaders.Count;
                    readersToWake = this.waitingReaders.ToArray();
                    this.waitingReaders.Clear();
                }
            }

            waiter.Tcs.TrySetCanceled();
            waiter.Registration.Dispose();

            if (readersToWake != null)
            {
                foreach (Waiter toWake in readersToWake)
                {
                    toWake.Tcs.TrySetResult(new Releaser(this, false));
                    toWake.Registration.Dispose();
                }
            }
        }

        /// <summary>
        /// Disposable Releaser to make it easy to use AsyncReaderWriterLock in a scoped manner with a using block.
        /// </summary>
        public struct Releaser : IDisposable
        {
            private readonly AsyncReaderWriterLock toRelease;
            private readonly bool writer;

            internal Releaser(AsyncReaderWriterLock toRelease, bool writer)
            {
                this.toRelease = toRelease;
                this.writer = writer;
            }

            /// <inheritdoc />
            public void Dispose()
            {
                if (this.toRelease != null)
                {
                    if (this.writer)
                    {
                        this.toRelease.WriterRelease();
                    }
                    else
                    {
                        this.toRelease.ReaderRelease();
                    }
                }
            }
        }

        private class Waiter
        {
            public TaskCompletionSource<Releaser> Tcs { get; set; }

            public CancellationTokenRegistration Registration { get; set; }
        }
    }
}
