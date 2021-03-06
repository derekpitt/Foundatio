﻿using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Lock;
using Foundatio.Logging;
using Foundatio.Queues;
using Foundatio.Utility;

namespace Foundatio.Jobs {
    public abstract class QueueProcessorJobBase<T> : JobBase, IQueueProcessorJob where T : class {
        protected readonly IQueue<T> _queue;
        protected readonly string _queueEntryName = typeof(T).Name;

        public QueueProcessorJobBase(IQueue<T> queue, ILoggerFactory loggerFactory) : base(loggerFactory) {
            _queue = queue;
            AutoComplete = true;
        }

        protected bool AutoComplete { get; set; }

        protected sealed override Task<ILock> GetJobLockAsync() {
            return base.GetJobLockAsync();
        }

        protected override async Task<JobResult> RunInternalAsync(JobRunContext context) {
            var linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, TimeSpan.FromSeconds(30).ToCancellationToken());

            IQueueEntry<T> queueEntry;
            try {
                queueEntry = await _queue.DequeueAsync(linkedCancellationToken.Token).AnyContext();
            } catch (Exception ex) {
                return JobResult.FromException(ex, $"Error trying to dequeue message: {ex.Message}");
            }

            if (queueEntry == null)
                return JobResult.Success;

            if (context.CancellationToken.IsCancellationRequested) {
                _logger.Info("Job was cancelled. Abandoning queue item: {queueEntryId}", queueEntry.Id);
                await queueEntry.AbandonAsync().AnyContext();
                return JobResult.Cancelled;
            }

            using (var lockValue = await GetQueueEntryLockAsync(queueEntry, context.CancellationToken).AnyContext()) {
                if (lockValue == null) {
                    await queueEntry.AbandonAsync().AnyContext();
                    return JobResult.SuccessWithMessage("Unable to acquire queue item lock.");
                }

                _logger.Info("Processing {0} queue entry ({1}).", _queueEntryName, queueEntry.Id);

                try {
                    var result = await ProcessQueueEntryAsync(new JobQueueEntryContext<T>(queueEntry, lockValue, context.CancellationToken)).AnyContext();

                    if (!AutoComplete)
                        return result;

                    if (result.IsSuccess) {
                        await queueEntry.CompleteAsync().AnyContext();
                        _logger.Info("Completed {0} queue entry ({1}).", _queueEntryName, queueEntry.Id);
                    } else {
                        await queueEntry.AbandonAsync().AnyContext();
                        _logger.Warn("Abandoned {0} queue entry ({1}).", _queueEntryName, queueEntry.Id);
                    }

                    return result;
                } catch (Exception ex) {
                    await queueEntry.AbandonAsync().AnyContext();
                    _logger.Error(ex, "Error processing {0} queue entry ({1}).", _queueEntryName, queueEntry.Id);

                    throw;
                }
            }
        }

        protected virtual Task<ILock> GetQueueEntryLockAsync(IQueueEntry<T> queueEntry, CancellationToken cancellationToken = default(CancellationToken)) {
            return Task.FromResult(Disposable.EmptyLock);
        }
        
        public async Task RunUntilEmptyAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            await RunContinuousAsync(cancellationToken: cancellationToken, interval: TimeSpan.FromMilliseconds(1), continuationCallback: async () => {
                var stats = await _queue.GetQueueStatsAsync().AnyContext();

                _logger.Trace("RunUntilEmpty continuation: queue: {Queued} working={Working}", stats.Queued, stats.Working);

                return stats.Queued + stats.Working > 0;
            }).AnyContext();
        }

        protected abstract Task<JobResult> ProcessQueueEntryAsync(JobQueueEntryContext<T> context);
    }
    
    public class JobQueueEntryContext<T> where T : class {
        public JobQueueEntryContext(IQueueEntry<T> queueEntry, ILock queueEntryLock, CancellationToken cancellationToken = default(CancellationToken)) {
            QueueEntry = queueEntry;
            QueueEntryLock = queueEntryLock;
            CancellationToken = cancellationToken;
        }

        public IQueueEntry<T> QueueEntry { get; private set; }
        public CancellationToken CancellationToken { get; private set; }
        public ILock QueueEntryLock { get; private set; }
    }

    public interface IQueueProcessorJob : IDisposable {
        Task RunUntilEmptyAsync(CancellationToken cancellationToken = default(CancellationToken));
    }
}
