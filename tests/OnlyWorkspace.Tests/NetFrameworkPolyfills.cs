#if NETFRAMEWORK
// Test-only stand-ins for APIs net48 lacks, so the same tests run on the shipped runtime.
namespace System.Runtime.CompilerServices
{
    /// <summary>Lets records and init accessors compile.</summary>
    internal static class IsExternalInit { }
}

namespace System
{
    /// <summary>Enough of System.Index for the <c>[^n]</c> indexer pattern.</summary>
    internal readonly struct Index
    {
        private readonly int _value;

        public Index(int value, bool fromEnd = false)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _value = fromEnd ? ~value : value;
        }

        public int Value => _value < 0 ? ~_value : _value;
        public bool IsFromEnd => _value < 0;
        public int GetOffset(int length) => IsFromEnd ? length - Value : Value;
        public static implicit operator Index(int value) => new(value);
    }
}

namespace OnlyWorkspace.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;

    internal static class NetFrameworkPolyfills
    {
        /// <summary>Same contract as .NET's Task.WaitAsync: TimeoutException when the task is still running.</summary>
        public static async Task WaitAsync(this Task task, TimeSpan timeout)
        {
            if (await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false) != task) throw new TimeoutException();
            await task.ConfigureAwait(false);
        }

        public static async Task<T> WaitAsync<T>(this Task<T> task, TimeSpan timeout)
        {
            if (await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false) != task) throw new TimeoutException();
            return await task.ConfigureAwait(false);
        }

        public static TValue? GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key) where TKey : notnull =>
            dictionary.TryGetValue(key, out var value) ? value : default;
    }
}
#endif
