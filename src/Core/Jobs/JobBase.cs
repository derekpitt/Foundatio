﻿using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Lock;
using Foundatio.Logging;
using Foundatio.Utility;

namespace Foundatio.Jobs {
    public abstract class JobBase : IDisposable {
        protected readonly ILogger _logger;
        private readonly string _jobName;
        private bool _runningContinuous = false;

        public JobBase(ILoggerFactory loggerFactory = null) {
            _jobName = TypeHelper.GetTypeDisplayName(GetType());
            _logger = loggerFactory?.CreateLogger(_jobName) ?? NullLogger.Instance;
            JobId = Guid.NewGuid().ToString("N").Substring(0, 10);
        }

        protected virtual Task<ILock> GetJobLockAsync() {
            return Task.FromResult(Disposable.EmptyLock);
        }

        public string JobId { get; private set; }

        public async Task<JobResult> RunAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            IDisposable scope = new EmptyDisposable();
            if (!_runningContinuous)
                scope = _logger.BeginScope(s => s.Property("job", _jobName));

            using (scope) {
                _logger.Trace("Job run \"{0}\" starting...", _jobName);

                using (var lockValue = await GetJobLockAsync().AnyContext()) {
                    if (lockValue == null)
                        return JobResult.SuccessWithMessage("Unable to acquire job lock.");

                    var result = await TryRunAsync(new JobRunContext(lockValue, cancellationToken)).AnyContext();
                    if (result != null) {
                        if (!result.IsSuccess)
                            _logger.Error(result.Error, "Job run \"{0}\" failed: {1}", GetType().Name, result.Message);
                        else if (!String.IsNullOrEmpty(result.Message))
                            _logger.Info("Job run \"{0}\" succeeded: {1}", GetType().Name, result.Message);
                        else
                            _logger.Trace("Job run \"{0}\" succeeded.", GetType().Name);
                    } else {
                        _logger.Error("Null job run result for \"{0}\".", GetType().Name);
                    }

                    return result;
                }
            }
        }

        private async Task<JobResult> TryRunAsync(JobRunContext context) {
            try {
                return await RunInternalAsync(context).AnyContext();
            } catch (Exception ex) {
                return JobResult.FromException(ex);
            }
        }

        protected abstract Task<JobResult> RunInternalAsync(JobRunContext context);
        
        public async Task RunContinuousAsync(TimeSpan? interval = null, int iterationLimit = -1, CancellationToken cancellationToken = default(CancellationToken), Func<Task<bool>> continuationCallback = null) {
            int iterations = 0;

            using (_logger.BeginScope(s => s.Property("job", _jobName))) {
                _logger.Info("Starting continuous job type \"{0}\" on machine \"{1}\"...", GetType().Name, Environment.MachineName);
                _runningContinuous = true;

                while (!cancellationToken.IsCancellationRequested && (iterationLimit < 0 || iterations < iterationLimit)) {
                    try {
                        var result = await RunAsync(cancellationToken).AnyContext();
                        iterations++;

                        if (result.Error != null)
                            await Task.Delay(Math.Max(interval?.Milliseconds ?? 0, 100), cancellationToken).AnyContext();
                        else if (interval.HasValue)
                            await Task.Delay(interval.Value, cancellationToken).AnyContext();
                        else if (iterations % 1000 == 0) // allow for cancellation token to get set
                            await Task.Delay(1).AnyContext();
                    } catch (TaskCanceledException) { }

                    if (continuationCallback == null)
                        continue;

                    try {
                        if (!await continuationCallback().AnyContext())
                            break;
                    } catch (Exception ex) {
                        _logger.Error(ex, "Error in continuation callback: {0}", ex.Message);
                    }
                }

                _logger.Info("Stopping continuous job type \"{0}\" on machine \"{1}\"...", GetType().Name, Environment.MachineName);

                if (cancellationToken.IsCancellationRequested)
                    _logger.Trace("Job cancellation requested.");

                _runningContinuous = false;
                await Task.Delay(1).AnyContext(); // allow events to process
            }
        }
        
        public virtual void Dispose() {}
    }

    public class JobRunContext {
        public JobRunContext(ILock jobLock, CancellationToken cancellationToken = default(CancellationToken)) {
            JobLock = jobLock;
            CancellationToken = cancellationToken;
        }

        public ILock JobLock { get; private set; }

        public CancellationToken CancellationToken { get; private set; }
    }
}