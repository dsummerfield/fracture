﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Linq;

#if !XBOX
using System.Linq.Expressions;
using Squared.Util.Bind;
#endif

namespace Squared.Task {
    public delegate void OnComplete(IFuture future);
    public delegate void OnDispose(IFuture future);

    [Serializable]
    public class FutureException : Exception {
        public FutureException (string message, Exception innerException)
            : base(message, innerException) {
        }
    }

    [Serializable]
    public class FutureAlreadyHasResultException : InvalidOperationException {
        public readonly IFuture Future;

        public FutureAlreadyHasResultException (IFuture future)
            : base("Future already has a result") {
            Future = future;
        }
    }

    [Serializable]
    public class FutureHasNoResultException : InvalidOperationException {
        public readonly IFuture Future;

        public FutureHasNoResultException (IFuture future)
            : base("Future does not yet have a result") {
            Future = future;
        }
    }

    [Serializable]
    public class FutureDisposedException : InvalidOperationException {
        public readonly IFuture Future;

        public FutureDisposedException (IFuture future)
            : base("Future is disposed") {
            Future = future;
        }
    }

    [Serializable]
    public class FutureHandlerException : Exception {
        public readonly IFuture Future;
        public readonly Delegate Handler;

        public FutureHandlerException (IFuture future, Delegate handler, Exception innerException) 
            : base("One of the Future's handlers threw an uncaught exception", innerException) {
            Future = future;
            Handler = handler;
        }
    }

    internal static class FutureHelpers {
        public static WaitCallback RunInThreadHelper;

        static FutureHelpers () {
            RunInThreadHelper = _RunInThreadHelper;
        }

        internal static void _RunInThreadHelper (object state) {
            var thunk = (RunInThreadThunk)state;
            try {
                thunk.Invoke();
            } catch (System.Reflection.TargetInvocationException ex) {
                thunk.Fail(ex.InnerException);
            } catch (Exception ex) {
                thunk.Fail(ex);
            }
        }
    }

    internal abstract class RunInThreadThunk {
        public abstract void Fail (Exception e);
        public abstract void Invoke ();
    }

    internal class ActionRunInThreadThunk : RunInThreadThunk {
        public SignalFuture Future = new SignalFuture();
        public Action WorkItem;

        public override void Invoke () {
            WorkItem();
            Future.Complete();
        }

        public override void Fail (Exception ex) {
            Future.Fail(ex);
        }
    }

    internal class FuncRunInThreadThunk<T> : RunInThreadThunk {
        public Future<T> Future = new Future<T>();
        public Func<T> WorkItem;

        public override void Invoke () {
            Future.Complete(WorkItem());
        }

        public override void Fail (Exception ex) {
            Future.Fail(ex);
        }
    }

    internal class DynamicRunInThreadThunk : RunInThreadThunk {
        public Future Future = new Future();
        public object[] Arguments;
        public Delegate WorkItem;

        public override void Invoke () {
#if XBOX
            Future.Complete(WorkItem.Method.Invoke(WorkItem.Target, Arguments));
#else
            Future.Complete(WorkItem.DynamicInvoke(Arguments));
#endif
        }

        public override void Fail (Exception ex) {
            Future.Fail(ex);
        }
    }

    public interface IFuture : IDisposable {
        bool Failed {
            get;
        }
        bool Completed {
            get;
        }
        bool Disposed {
            get;
        }
        object Result {
            get;
        }
        Exception Error {
            get;
        }

        bool GetResult (out object result, out Exception error);
        void SetResult (object result, Exception error);
        void RegisterOnComplete (OnComplete handler);
        void RegisterOnDispose (OnDispose handler);
        void RegisterOnErrorCheck (Action handler);
    }

    public class NoneType {
        private NoneType () {
        }

        public static NoneType None = new NoneType();
    }

    public class Future : Future<object> {
        public Future () 
            : base() {
        }

        public Future (object value)
            : base(value) {
        }

        public static IFuture New<T> () {
            if (typeof(T).Equals(typeof(object)))
                return new Future();
            else
                return new Future<T>();
        }
    }

    public class SignalFuture : Future<NoneType> {
        public SignalFuture ()
            : base() {
        }

        public SignalFuture (bool signaled) 
            : base(signaled ? NoneType.None : null) {
        }
    }

    public class Future<T> : IDisposable, IFuture {
        private const int State_Empty = 0;
        private const int State_Indeterminate = 1;
        private const int State_CompletedWithValue = 2;
        private const int State_CompletedWithError = 3;
        private const int State_Disposed = 4;

        private static int ProcessorCount;

        private int _State = State_Empty;
        private OnComplete _OnComplete = null;
        private OnDispose _OnDispose = null;
        private Exception _Error = null;
        private Action _OnErrorChecked = null;
        private T _Result = default(T);

        public override string ToString () {
            int state = _State;
            var result = _Result;
            var error = _Error;
            Thread.MemoryBarrier();
            string stateText = "??";
            switch (state) {
                case State_Empty:
                    stateText = "Empty";
                    break;
                case State_Indeterminate:
                    stateText = "Indeterminate";
                    break;
                case State_CompletedWithValue:
                    stateText = "CompletedWithValue";
                    break;
                case State_CompletedWithError:
                    stateText = "CompletedWithError";
                    break;
                case State_Disposed:
                    stateText = "Disposed";
                    break;
            }
            return String.Format("<Future<{3}> ({0}) r={1} e={2}>", stateText, result, error, typeof(T).Name);
        }

        public Future () {
        }

        public Future (T value) {
            this.SetResult(value, null);
        }

        static Future () {
            ProcessorCount = Environment.ProcessorCount;
        }

        private static void SpinWait (int iterationCount) {
#if !XBOX
            if ((iterationCount < 5) && (ProcessorCount > 1)) {
                Thread.SpinWait(7 * iterationCount);
            } else if (iterationCount < 7) {
#else
            if (iterationCount < 3) {
#endif
                Thread.Sleep(0);
            } else {
                Thread.Sleep(1);
            }
        }

        private void InvokeOnDispose (OnDispose handler) {
            if (handler == null)
                return;

            try {
                handler(this);
            } catch (Exception ex) {
                throw new FutureHandlerException(this, handler, ex);
            }
        }

        private void InvokeOnComplete (OnComplete handler) {
            if (handler == null)
                return;

            try {
                handler(this);
            } catch (Exception ex) {
                throw new FutureHandlerException(this, handler, ex);
            }
        }

        void OnErrorCheck () {
            var ec = _OnErrorChecked;
            if (ec != null) {
                ec = Interlocked.Exchange(ref _OnErrorChecked, null);
                if (ec != null)
                    ec();
            }
        }

        public void RegisterOnErrorCheck (Action handler) {
            Action newHandler;
            int iterations = 1;

            while (true) {
                var oldHandler = _OnErrorChecked;
                if (oldHandler != null) {
                    newHandler = () => {
                        oldHandler();
                        handler();
                    };
                } else {
                    newHandler = handler;
                }

                if (Interlocked.CompareExchange(ref _OnErrorChecked, newHandler, oldHandler) == oldHandler)
                    break;

                SpinWait(iterations);
                iterations += 1;
            }
        }

        public void RegisterOnComplete (OnComplete handler) {
            OnComplete newOnComplete;
            int iterations = 1;
            int state = 0;

            while (true) {
                state = Interlocked.CompareExchange(ref _State, State_Indeterminate, State_Empty);

                if (state != State_Indeterminate)
                    break;

                SpinWait(iterations++);
            }

            if (state == State_Empty) {
                var oldOnComplete = _OnComplete;
                if (oldOnComplete != null) {
                    newOnComplete = (f) => {
                        oldOnComplete(f);
                        handler(f);
                    };
                } else {
                    newOnComplete = handler;
                }

                _OnComplete = newOnComplete;

                if (Interlocked.CompareExchange(ref _State, State_Empty, State_Indeterminate) != State_Indeterminate)
                    throw new ThreadStateException();
            } else if (state == State_CompletedWithValue) {
                InvokeOnComplete(handler);
            } else if (state == State_CompletedWithError) {
                InvokeOnComplete(handler);
            }
        }

        public void RegisterOnDispose (OnDispose handler) {
            OnDispose newOnDispose;
            int iterations = 1;
            int state = 0;

            while (true) {
                state = Interlocked.CompareExchange(ref _State, State_Indeterminate, State_Empty);

                if (state != State_Indeterminate)
                    break;

                SpinWait(iterations++);
            }

            if (state == State_Empty) {
                var oldOnDispose = _OnDispose;
                if (oldOnDispose != null) {
                    newOnDispose = (f) => {
                        oldOnDispose(f);
                        handler(f);
                    };
                } else {
                    newOnDispose = handler;
                }

                _OnDispose = newOnDispose;

                if (Interlocked.CompareExchange(ref _State, State_Empty, State_Indeterminate) != State_Indeterminate)
                    throw new ThreadStateException();
            } else if (state == State_Disposed) {
                InvokeOnDispose(handler);
            }
        }

        public bool Disposed {
            get {
                return _State == State_Disposed;
            }
        }

        public bool Completed {
            get {
                int state = _State;
                return (state == State_CompletedWithValue) || (state == State_CompletedWithError);
            }
        }

        public bool Failed {
            get {
                OnErrorCheck();
                return _State == State_CompletedWithError;
            }
        }

        object IFuture.Result {
            get {
                return this.Result;
            }
        }

        public Exception Error {
            get {
                int state = _State;
                Thread.MemoryBarrier();
                if (state == State_CompletedWithValue) {
                    return null;
                } else if (state == State_CompletedWithError) {
                    OnErrorCheck();
                    return _Error;
                } else
                    throw new FutureHasNoResultException(this);
            }
        }

        public T Result {
            get {
                int state = _State;
                Thread.MemoryBarrier();
                if (state == State_CompletedWithValue) {
                    return _Result;
                } else if (state == State_CompletedWithError) {
                    OnErrorCheck();
                    throw new FutureException("Future's result was an error", (Exception)_Error);
                } else
                    throw new FutureHasNoResultException(this);
            }
        }

        void IFuture.SetResult (object result, Exception error) {
            if ((error != null) && (result != null)) {
                throw new FutureException("Cannot complete a future with both a result and an error.", error);
            }

            if (result == null)
                SetResult(default(T), error);
            else
                SetResult((T)result, error);
        }

        public void SetResult (T result, Exception error) {
            int iterations = 1;

            while (true) {
                int oldState = Interlocked.CompareExchange(ref _State, State_Indeterminate, State_Empty);

                if (oldState == State_Disposed) {
                    return;
                } else if ((oldState == State_CompletedWithValue) || (oldState == State_CompletedWithError))
                    throw new FutureAlreadyHasResultException(this);
                else if (oldState == State_Empty)
                    break;

                SpinWait(iterations++);
            }

            Thread.MemoryBarrier();
            int newState = (error != null) ? State_CompletedWithError : State_CompletedWithValue;
            _Result = result;
            _Error = error;
            OnComplete handler = _OnComplete;
            Thread.MemoryBarrier();
            _OnDispose = null;
            _OnComplete = null;

            if (Interlocked.Exchange(ref _State, newState) != State_Indeterminate)
                throw new ThreadStateException();

            if (newState == State_CompletedWithValue)
                InvokeOnComplete(handler);
            else if (newState == State_CompletedWithError)
                InvokeOnComplete(handler);
        }

        public void Dispose () {
            int iterations = 1;

            while (true) {
                int oldState = Interlocked.CompareExchange(ref _State, State_Indeterminate, State_Empty);

                if ((oldState == State_Disposed) || (oldState == State_CompletedWithValue) || (oldState == State_CompletedWithError)) {
                    return;
                } else if (oldState == State_Empty)
                    break;

                SpinWait(iterations++);
            }

            Thread.MemoryBarrier();
            OnDispose handler = _OnDispose;
            _OnDispose = null;
            _OnComplete = null;

            if (Interlocked.Exchange(ref _State, State_Disposed) != State_Indeterminate)
                throw new ThreadStateException();

            InvokeOnDispose(handler);
        }

        bool IFuture.GetResult (out object result, out Exception error) {
            T temp;
            bool retval = this.GetResult(out temp, out error);

            if (error != null)
                result = null;
            else
                result = temp;

            return retval;
        }

        public bool GetResult (out T result) {
            int state = _State;

            Thread.MemoryBarrier();
            if (state == State_CompletedWithValue) {
                result = _Result;
                return true;
            }

            result = default(T);
            return false;
        }

        public bool GetResult (out T result, out Exception error) {
            int state = _State;

            Thread.MemoryBarrier();
            if (state == State_CompletedWithValue) {
                result = _Result;
                error = null;
                return true;
            } else if (state == State_CompletedWithError) {
                OnErrorCheck();
                result = default(T);
                error = (Exception)_Error;
                return true;
            }

            result = default(T);
            error = null;
            return false;
        }

        public static IFuture WaitForFirst (IEnumerable<IFuture> futures) {
            return WaitForFirst(futures.ToArray());
        }

        public static IFuture WaitForFirst (params IFuture[] futures) {
            return WaitForX(futures, futures.Length);
        }

        public static IFuture WaitForAll (IEnumerable<IFuture> futures) {
            return WaitForAll(futures.ToArray());
        }

        public static IFuture WaitForAll (params IFuture[] futures) {
            return WaitForX(futures, 1);
        }

        public static Future<U> RunInThread<U> (Func<U> workItem) {
            var thunk = new FuncRunInThreadThunk<U> {
                WorkItem = workItem,
            };
            ThreadPool.QueueUserWorkItem(FutureHelpers.RunInThreadHelper, thunk);
            return thunk.Future;
        }

        public static SignalFuture RunInThread (Action workItem) {
            var thunk = new ActionRunInThreadThunk {
                WorkItem = workItem,
            };
            ThreadPool.QueueUserWorkItem(FutureHelpers.RunInThreadHelper, thunk);
            return thunk.Future;
        }

        public static Future RunInThread (Delegate workItem, params object[] arguments) {
            var thunk = new DynamicRunInThreadThunk {
                WorkItem = workItem,
                Arguments = arguments
            };
            ThreadPool.QueueUserWorkItem(FutureHelpers.RunInThreadHelper, thunk);
            return thunk.Future;
        }

        private class WaitHandler {
            public IFuture Composite;
            public List<IFuture> State = new List<IFuture>();
            public int Trigger;

            public void OnComplete (IFuture f) {
                bool completed = false;
                lock (State) {
                    if (State.Count == Trigger) {
                        completed = true;
                        State.Clear();
                    } else {
                        State.Remove(f);
                    }
                }

                if (completed) {
                    Composite.Complete(f);
                }
            }

            public void OnDispose (IFuture f) {
                bool completed = false;
                lock (State) {
                    if (State.Count == Trigger) {
                        completed = true;
                        State.Clear();
                    } else {
                        State.Remove(f);
                    }
                }

                if (completed)
                    Composite.Dispose();
            }
        }

        private static IFuture WaitForX (IFuture[] futures, int x) {
            if ((futures == null) || (futures.Length == 0))
                throw new ArgumentException("Must specify at least one future to wait on", "futures");

            Future f = new Future();
            var h = new WaitHandler();
            h.Composite = f;
            h.State.AddRange(futures);
            h.Trigger = x;
            OnComplete oc = h.OnComplete;
            OnDispose od = h.OnDispose;

            foreach (IFuture _ in futures) {
                _.RegisterOnComplete(oc);
                _.RegisterOnDispose(od);
            }

            return f;
        }
    }
    
    public static class FutureExtensionMethods {
        /// <summary>
        /// Causes this future to become completed when the specified future is completed.
        /// </summary>
        public static void Bind (this IFuture future, IFuture target) {
            OnComplete handler = (f) => {
                object result;
                Exception error;
                f.GetResult(out result, out error);
                future.SetResult(result, error);
            };
            target.RegisterOnComplete(handler);
        }

#if !XBOX
        public static Future<T> Bind<T> (this Future<T> future, Expression<Func<T>> target) {
            var member = BoundMember.New(target);

            future.RegisterOnComplete((_) => {
                T result;
                if (future.GetResult(out result))
                    member.Value = result;
            });
            
            return future;
        }
#endif

        public static void AssertSucceeded (this IFuture future) {
            var temp = future.Result;
        }

        public static void Complete (this IFuture future) {
            future.SetResult(null, null);
        }

        public static void Complete (this IFuture future, object result) {
            future.SetResult(result, null);
        }

        public static void Complete<T> (this Future<T> future, T result) {
            future.SetResult(result, null);
        }

        public static void Fail (this IFuture future, Exception error) {
            future.SetResult(null, error);
        }

        public static void Fail<T> (this Future<T> future, Exception error) {
            future.SetResult(default(T), error);
        }

        public static bool CheckForFailure (this IFuture future, params Type[] failureTypes) {
            object result;
            Exception error;
            if (future.GetResult(out result, out error)) {
                if (error != null) {
                    foreach (Type type in failureTypes)
                        if (type.IsInstanceOfType(error))
                            return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Creates a ManualResetEvent that will become set when this future is completed.
        /// </summary>
        public static ManualResetEvent GetCompletionEvent (this IFuture future) {
            ManualResetEvent evt = new ManualResetEvent(false);
            OnComplete handler = (f) => evt.Set();
            future.RegisterOnComplete(handler);
            return evt;
        }

        public static WaitWithTimeout WaitWithTimeout (this IFuture future, double timeout) {
            return new WaitWithTimeout(future, timeout);
        }
    }
}
