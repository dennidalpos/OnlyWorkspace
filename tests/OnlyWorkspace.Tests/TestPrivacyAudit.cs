using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Infrastructure.Security;

namespace OnlyWorkspace.Tests;

internal static class TestPrivacyAudit
{
    // The ordinary tests never write the user's production ledger.
    public static IPseudonymizationAuditStore Store => new PseudonymizationAuditStore(Path.Combine(
        Environment.GetEnvironmentVariable("ONLYWORKSPACE_TEST_ARTIFACTS") ?? Path.GetTempPath(), "ow-privacy-tests", Guid.NewGuid().ToString("N")));
}
