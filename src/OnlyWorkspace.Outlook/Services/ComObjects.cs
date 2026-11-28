using System.Runtime.InteropServices;

namespace OnlyWorkspace.Outlook.Services;

/// <summary>The one place that releases a COM reference; null and non-COM objects are ignored.</summary>
internal static class ComObjects
{
    internal static void Release(object? comObject)
    {
        if (comObject != null && Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject);
    }
}
