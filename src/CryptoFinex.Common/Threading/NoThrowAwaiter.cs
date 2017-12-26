using System;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;

namespace CryptoFinex.Common.Threading
{
    public static class NoThrowTaskExtensions
    {
        public static async Task NoThrow(this Task task)
        {
            await new NoThrowAwaiter(task);
        }
    }

    public struct NoThrowAwaiter : ICriticalNotifyCompletion
    {
        private readonly Task task;

        public NoThrowAwaiter(Task task)
        {
            this.task = task;
        }

        public NoThrowAwaiter GetAwaiter() => this;

        public bool IsCompleted => task.IsCompleted;

        // Observe exception
        public void GetResult()
        {
            _ = task.Exception;
        }

        public void OnCompleted(Action continuation) => task.GetAwaiter().OnCompleted(continuation);

        public void UnsafeOnCompleted(Action continuation) => OnCompleted(continuation);
    }
}
