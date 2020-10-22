using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace PooledValueTaskSource
{
    public class FileReadingPooledValueTaskSource2 : IValueTaskSource<string>
    {
        private ManualResetValueTaskSourceCore<string> _mrvts = new ManualResetValueTaskSourceCore<string>();
        private string _result;
        private ObjectPool<FileReadingPooledValueTaskSource2> _pool;

        public string GetResult(short token)
        {
            string result = _mrvts.GetResult(token);
            _mrvts.Reset();
            _pool.Return(this);

            return result;
        }

        public ValueTaskSourceStatus GetStatus(short token) => _mrvts.GetStatus(token);

        public void OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _mrvts.OnCompleted(continuation, state, token, flags);

        public ValueTask<string> RunAsync(string filename, ObjectPool<FileReadingPooledValueTaskSource2> pool)
        {
            _pool = pool;

            // Start async op
            bool isCompleted = this.FireAsyncWorkWithSyncReturnPossible(filename);
            if (!isCompleted)
            {
                // Opearation not yet completed. Return ValueTask wrapping us.
                Console.WriteLine("Asynchronous path.");
                return new ValueTask<string>(this, _mrvts.Version);
            }

            // OMG so happy, we catch up! Just return ValueTask wrapping the result.
            Console.WriteLine("Synchronous path.");
            string result = _result;
            _pool.Return(this);
            return new ValueTask<string>(result);
        }

        private bool FireAsyncWorkWithSyncReturnPossible(string filename)
        {
            if (filename == @"c:\dummy.txt")
            {
                // Simulate sync path
                _result = filename;
                return true;
            }
            // Simulate some low-level, unmanaged, asynchronous work. This normally:
            // - would call an OS-level API with callback registered
            // - after some time registered callback would be triggered (with NotifyAsyncWorkCompletion call inside)
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(1000);
                string data = File.ReadAllText(filename);
                this.NotifyAsyncWorkCompletion(data);
            });
            return false;
        }

        private void NotifyAsyncWorkCompletion(string data, Exception exception = null)
        {
            if (exception == null)
            {
                _mrvts.SetResult(data);
            }
            else
            {
                _mrvts.SetException(exception);
            }
        }

        public static void ThrowMultipleContinuations()
        {
            throw new InvalidOperationException("Multiple awaiters are not allowed");
        }
    }
}
