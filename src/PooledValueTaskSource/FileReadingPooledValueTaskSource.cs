using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace PooledValueTaskSource
{
    public class FileReadingPooledValueTaskSource : IValueTaskSource<string>
    {
        // Sentinel object used to indicate that the operation has completed prior to OnCompleted being called.
        private static readonly Action<object> s_callbackCompleted = _ => Debug.Fail("Should not be invoked");

        private Action<object> _continuation;
        private string _result;
        private Exception _exception;
        /// <summary>Current token value given to a ValueTask and then verified against the value it passes back to us.</summary>
        /// <remarks>
        /// This is not meant to be a completely reliable mechanism, doesn't require additional synchronization, etc.
        /// It's purely a best effort attempt to catch misuse, including awaiting for a value task twice and after
        /// it's already being reused by someone else.
        /// </remarks>
        private short _token;
        private object _state;

        private ObjectPool<FileReadingPooledValueTaskSource> _pool;
        private ExecutionContext _executionContext;
        private object _scheduler;

        public string GetResult(short token)
        {
            if (token != _token)
            {
                ThrowMultipleContinuations();
            }

            Console.WriteLine("GetResult");
            Exception exception = _exception;
            string result = this.ResetAndReleaseOperation();
            if (exception != null)
            {
                throw exception;
            }
            return result;
        }

        public ValueTaskSourceStatus GetStatus(short token)
        {
            if (token != _token)
            {
                ThrowMultipleContinuations();
            }

            Console.Write("GetStatus:");
            if (_result == null)
            {
                Console.WriteLine("pending");
                return ValueTaskSourceStatus.Pending;
            }
            Console.WriteLine("completed: succeeded or faulted");
            return _exception == null ? ValueTaskSourceStatus.Succeeded : ValueTaskSourceStatus.Faulted;
        }

        /// <summary>Called on awaiting so:
        /// - if operation has not yet completed - queues the provided continuation to be executed once the operation is completed
        /// - if operation has completed -
        /// </summary>
        public void OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            Console.WriteLine("." + token);
            if (token != _token)
            {
                ThrowMultipleContinuations();
            }

            if ((flags & ValueTaskSourceOnCompletedFlags.FlowExecutionContext) != 0)
            {
                _executionContext = ExecutionContext.Capture();
            }

            if ((flags & ValueTaskSourceOnCompletedFlags.UseSchedulingContext) != 0)
            {
                SynchronizationContext sc = SynchronizationContext.Current;
                if (sc != null && sc.GetType() != typeof(SynchronizationContext))
                {
                    _scheduler = sc;
                }
                else
                {
                    TaskScheduler ts = TaskScheduler.Current;
                    if (ts != TaskScheduler.Default)
                    {
                        _scheduler = ts;
                    }
                }
            }

            // Remember current state
            _state = state;
            // Remember continuation to be executed on completed (if not already completed, in case of which
            // continuation will be set to CallbackCompleted)
            Action<object> previousContinuation = Interlocked.CompareExchange(ref _continuation, continuation, null);
            if (previousContinuation != null)
            {
                if (!ReferenceEquals(previousContinuation, s_callbackCompleted))
                {
                    ThrowMultipleContinuations();
                }

                // Lost the race condition and the operation has now already completed.
                // We need to invoke the continuation, but it must be asynchronously to
                // avoid a stack dive.  However, since all of the queueing mechanisms flow
                // ExecutionContext, and since we're still in the same context where we
                // captured it, we can just ignore the one we captured.
                _executionContext = null;
                _state = null; // we have the state in "state"; no need for the one in UserToken
                this.InvokeContinuation(continuation, state, forceAsync: true);
            }
        }

        public ValueTask<string> RunAsync(string filename, ObjectPool<FileReadingPooledValueTaskSource> pool)
        {
            Debug.Assert(Volatile.Read(ref _continuation) == null, "Expected null continuation to indicate reserved for use");
            _pool = pool;

            // Start async op
            bool isCompleted = this.FireAsyncWorkWithSyncReturnPossible(filename);
            if (!isCompleted)
            {
                // Opearation not yet completed. Return ValueTask wrapping us.
                Console.WriteLine("Asynchronous path.");
                return new ValueTask<string>(this, _token);
            }

            // OMG so happy, we catch up! Just return ValueTask wrapping the result.
            Console.WriteLine("Synchronous path.");
            string result = this.ResetAndReleaseOperation();
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
            _result = data;
            _exception = exception;

            // Mark operation as completed
            Action<object> previousContinuation = Interlocked.CompareExchange(ref _continuation, s_callbackCompleted, null);
            if (previousContinuation != null)
            {
                // Async work completed, continue with... continuation
                ExecutionContext ec = _executionContext;
                if (ec == null)
                {
                    this.InvokeContinuation(previousContinuation, _state, forceAsync: false);
                }
                else
                {
                    // This case should be relatively rare, as the async Task/ValueTask method builders
                    // use the awaiter's UnsafeOnCompleted, so this will only happen with code that
                    // explicitly uses the awaiter's OnCompleted instead.
                    _executionContext = null;
                    ExecutionContext.Run(ec, runState =>
                    {
                        var t = (Tuple<FileReadingPooledValueTaskSource, Action<object>, object>)runState;
                        t.Item1.InvokeContinuation(t.Item2, t.Item3, forceAsync: false);
                    }, Tuple.Create(this, previousContinuation, _state));
                }
            }
        }

        private void InvokeContinuation(Action<object> continuation, object state, bool forceAsync)
        {
            if (continuation == null)
                return;

            object scheduler = _scheduler;
            _scheduler = null;
            if (scheduler != null)
            {
                if (scheduler is SynchronizationContext sc)
                {
                    sc.Post(s =>
                    {
                        var t = (Tuple<Action<object>, object>)s;
                        t.Item1(t.Item2);
                    }, Tuple.Create(continuation, state));
                }
                else
                {
                    Debug.Assert(scheduler is TaskScheduler, $"Expected TaskScheduler, got {scheduler}");
                    Task.Factory.StartNew(continuation, state, CancellationToken.None, TaskCreationOptions.DenyChildAttach, (TaskScheduler)scheduler);
                }
            }
            else if (forceAsync)
            {
                ThreadPool.QueueUserWorkItem(continuation, state, preferLocal: true);
            }
            else
            {
                continuation(state);
            }
        }

        public static void ThrowMultipleContinuations()
        {
            throw new InvalidOperationException("Multiple awaiters are not allowed");
        }

        private string ResetAndReleaseOperation()
        {
            string result = _result;
            _token++;
            _result = null;
            _exception = null;
            _state = null;
            _continuation = null;
            _pool.Return(this);
            return result;
        }
    }
}
