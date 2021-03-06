using System;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;

namespace Foundatio.Lock {
    internal class DisposableLock : ILock {
        private readonly ILockProvider _lockProvider;
        private readonly string _name;
        private readonly ILogger _logger;

        public DisposableLock(string name, ILockProvider lockProvider, ILogger logger) {
            _logger = logger;
            _name = name;
            _lockProvider = lockProvider;
        }

        public async void Dispose() {
            _logger.Trace("Disposing lock: {0}", _name);
            await _lockProvider.ReleaseAsync(_name).AnyContext();
            _logger.Trace("Disposed lock: {0}", _name);
        }

        public async Task RenewAsync(TimeSpan? lockExtension = null) {
            _logger.Trace("Renewing lock: {0}", _name);
            await _lockProvider.RenewAsync(_name, lockExtension).AnyContext();
            _logger.Trace("Renewing lock: {0}", _name);
        }
    }
}