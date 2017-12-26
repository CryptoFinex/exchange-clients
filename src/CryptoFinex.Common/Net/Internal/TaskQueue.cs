using System;
using System.Threading.Tasks;

namespace CryptoFinex.Common.Net.Internal
{
    // Allows serial queuing of Task instances
    // The tasks are not called on the current synchronization context

    public sealed class TaskQueue
    {
        private readonly object lockObj = new object();
        private Task lastQueuedTask;
        private volatile bool drained;

        public TaskQueue()
            : this(Task.CompletedTask)
        { }

        public TaskQueue(Task initialTask)
        {
            lastQueuedTask = initialTask;
        }

        public bool IsDrained
        {
            get { return drained; }
        }

        public Task Enqueue(Func<Task> taskFunc)
        {
            return Enqueue(s => taskFunc(), null);
        }

        public Task Enqueue(Func<object, Task> taskFunc, object state)
        {
            lock (lockObj)
            {
                if (drained)
                {
                    return lastQueuedTask;
                }

                var newTask = lastQueuedTask.ContinueWith((t, s1) =>
                {
                    if (t.IsFaulted || t.IsCanceled)
                    {
                        return t;
                    }

                    return taskFunc(s1) ?? Task.CompletedTask;
                },
                state).Unwrap();
                lastQueuedTask = newTask;
                return newTask;
            }
        }

        public Task Drain()
        {
            lock (lockObj)
            {
                drained = true;

                return lastQueuedTask;
            }
        }
    }
}
