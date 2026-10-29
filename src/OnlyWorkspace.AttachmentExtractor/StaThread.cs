using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace OnlyWorkspace.AttachmentExtractor;

/// <summary>Work that needs its own STA thread (RichTextBox): an exception left on that thread would end the process.</summary>
internal static class StaThread
{
    /// <summary>Runs <paramref name="work"/> on a new STA thread and waits; its exception is rethrown to the caller.</summary>
    public static T Run<T>(Func<T> work)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }
}
