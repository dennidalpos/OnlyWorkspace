using System.Threading;
using System.Windows.Forms;

namespace OnlyWorkspace.Outlook.UI;

internal static class UiSync
{
    /// <summary>
    /// Office may run callbacks without a WinForms SynchronizationContext; without it, await
    /// continuations land on thread-pool threads and touch the Outlook Object Model off the UI thread.
    /// Must be called on the Outlook UI thread.
    /// </summary>
    public static void Ensure()
    {
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        }
    }
}
