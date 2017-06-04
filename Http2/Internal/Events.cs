using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Http2.Internal
{
    public class AsyncManualResetEvent : ICriticalNotifyCompletion
    {
        private static readonly Action isSet = () => { };
        private static readonly Action isReset = () => { };

        private volatile Action _state = isReset;

        public bool IsCompleted => _state == isSet;
        public bool IsReset => _state == isReset;

        public AsyncManualResetEvent(bool signaled)
        {
            if (signaled) _state = isSet;
        }

        public void UnsafeOnCompleted(Action continuation) => OnCompleted(continuation);

        public void OnCompleted(Action continuation)
        {
            if (continuation == null) return;

            var previous = Interlocked.CompareExchange(ref _state, continuation, isReset);
            if (previous == isSet)
            {
                continuation();
            }
        }

        public void GetResult()
        {
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _state, isReset);
        }

        public void Set()
        {
            var completion = Interlocked.Exchange(ref _state, isSet);
            if (completion != isSet && completion != isReset)
            {
                Task.Run(completion);
            }
        }

        public AsyncManualResetEvent GetAwaiter() => this;
    }
}
