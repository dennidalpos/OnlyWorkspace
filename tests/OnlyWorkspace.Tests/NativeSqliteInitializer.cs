using System.Runtime.CompilerServices;
using OnlyWorkspace.Infrastructure.Archive;

#if NET48
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}
#endif

namespace OnlyWorkspace.Tests
{
/// <summary>
/// Tests open a SqliteConnection directly, without ArchiveDb.Open. On net48 the native sqlite3mc.dll is found only
/// after ArchiveDb preloads it by path, and a failed SqliteConnection type initializer stays failed for the whole
/// process: without this, a run that starts with such a test fails every later archive test (#263).
/// </summary>
internal static class NativeSqliteInitializer
{
    [ModuleInitializer]
    internal static void Run() => ArchiveDb.EnsureNativeLoaded();
}
}
