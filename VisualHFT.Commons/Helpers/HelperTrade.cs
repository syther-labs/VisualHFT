using VisualHFT.Model;

namespace VisualHFT.Helpers
{
    public class HelperTrade
    {
        private List<Action<Trade>> _subscribers = new List<Action<Trade>>();
        private readonly ReaderWriterLockSlim _lockObj = new ReaderWriterLockSlim();

        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly HelperTrade instance = new HelperTrade();
        public static HelperTrade Instance => instance;

        public event Action<VisualHFT.Commons.Model.ErrorEventArgs> OnException;


        public void Subscribe(Action<Trade> processor)
        {
            _lockObj.EnterWriteLock();
            try
            {
                _subscribers.Add(processor);
            }
            finally
            {
                _lockObj.ExitWriteLock();
            }
        }

        public void Unsubscribe(Action<Trade> processor)
        {
            _lockObj.EnterWriteLock();
            try
            {
                _subscribers.Remove(processor);
            }
            finally
            {
                _lockObj.ExitWriteLock();
            }
        }

        public void Reset()
        {
            _lockObj.EnterWriteLock();
            try
            {
                _subscribers.Clear();
            }
            finally
            {
                _lockObj.ExitWriteLock();
            }
        }

        private void DispatchToSubscribers(Trade trade)
        {
            _lockObj.EnterReadLock();
            try
            {
                foreach (var subscriber in _subscribers)
                {
                    try
                    {
                        subscriber(trade);
                    }
                    catch (Exception ex)
                    {
                        // This runs on the market connector's producer thread, at a call site that
                        // does not guard itself. An unhandled exception on a non-UI thread
                        // terminates the process, and an escaping exception would also unwind this
                        // loop, so one faulting subscriber would starve every subscriber after it
                        // of trade data.
                        //
                        // A subscriber throwing is a normal operating condition on a hot path, not
                        // grounds for killing the host. Each one is isolated and dispatch continues
                        // to the next. Isolating must not mean hiding a data outage, so the fault is
                        // logged and published on OnException, carrying the subscriber that raised
                        // it so a listener can tell whose fault it was.
                        Task.Run(() =>
                        {
                            log.Error(ex);
                            OnException?.Invoke(new VisualHFT.Commons.Model.ErrorEventArgs(ex, subscriber.Target));
                        });
                        // deliberately NO rethrow - continue to the next subscriber.
                    }
                }
            }
            finally
            {
                _lockObj.ExitReadLock();
            }
        }

        public void UpdateData(Trade trade)
        {
            DispatchToSubscribers(trade);
        }

        public void UpdateData(IEnumerable<Trade> trades)
        {
            foreach (var e in trades)
            {
                DispatchToSubscribers(e);
            }
        }
    }
}