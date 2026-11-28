using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OnlyWorkspace.PiiRecognizer;

/// <summary>The recognizer's pipe: one instance, only the current Windows user, never a network client.</summary>
internal static class PiiPipeServer
{
    internal static NamedPipeServerStream Create(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException("Windows user identity unavailable.");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
#if NETFRAMEWORK
        return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, security);
#else
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, security);
#endif
    }

    internal static void ValidateClient(NamedPipeServerStream pipe)
    {
        string expected;
        using (var identity = WindowsIdentity.GetCurrent()) expected = identity.User?.Value ?? string.Empty;
        bool valid = false;
        pipe.RunAsClient(() =>
        {
            using var client = WindowsIdentity.GetCurrent(true);
            valid = client?.User?.Value == expected && !(client.Groups?.Any(g => g.Value == "S-1-5-2") ?? true);
        });
        if (!valid) throw new UnauthorizedAccessException("Only a local client of this Windows user may use the recognizer.");
    }
}
