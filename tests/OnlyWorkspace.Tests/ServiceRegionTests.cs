using System.Text.Json;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Core.Privacy;

namespace OnlyWorkspace.Tests;

public sealed class ServiceRegionTests
{
    private static LlmProfileConfig Cloud(ServiceRegion region) => new()
    {
        Id = "cloud", DisplayName = "Cloud", Type = "openai", BaseUrl = "https://provider.invalid/v1", Model = "m",
        Exposure = ExposureLevel.Standard, ServiceRegion = region
    };

    [Fact]
    public void Region_DefaultsToUnspecified_AndSurvivesTheJsonRoundtrip()
    {
        Assert.Equal(ServiceRegion.Unspecified, new LlmProfileConfig().ServiceRegion);
        var back = JsonSerializer.Deserialize<LlmProfileConfig>(JsonSerializer.Serialize(Cloud(ServiceRegion.Eu)))!;
        Assert.Equal(ServiceRegion.Eu, back.ServiceRegion);
        Assert.Contains("\"Eu\"", JsonSerializer.Serialize(Cloud(ServiceRegion.Eu)));
    }

    [Fact]
    public void Region_RenewsTheAuthorizationAndOpensANewConversation()
    {
        var eu = Cloud(ServiceRegion.Eu);
        var other = Cloud(ServiceRegion.NonEu);
        Assert.NotEqual(DataEgress.AuthorizationPolicyKey(eu), DataEgress.AuthorizationPolicyKey(other));
        Assert.NotEqual(ConversationPolicy.Key(eu), ConversationPolicy.Key(other));
    }

    [Fact]
    public void Region_AppearsInTheProfileSummaryAndTheReport()
    {
        Assert.Contains("Area del servizio: UE", DataEgress.ProfileSummary(Cloud(ServiceRegion.Eu)));
        Assert.Contains("Area del servizio: Non specificata", DataEgress.ProfileSummary(Cloud(ServiceRegion.Unspecified)));
        var config = TestEgress.Accepted(new WorkspaceConfig { DefaultProfile = "cloud", Profiles = new() { Cloud(ServiceRegion.NonEu) } });
        string html = DataEgressReport.RenderHtml(config, new DataEgressWorkstation());
        Assert.Contains("<th>Area</th>", html);
        Assert.Contains("<td>Non UE</td>", html);
    }

    [Theory]
    [InlineData(ServiceRegion.Unspecified, true)]
    [InlineData(ServiceRegion.Eu, false)]
    public void ConsentForm_AsksAnExplicitConfirmation_OnlyForAnUnspecifiedArea(ServiceRegion region, bool needsConfirmation)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new OnlyWorkspace.ConfigTool.Forms.DataEgressConsentForm(Array.Empty<DataEgressProvider>(), null, new[] { Cloud(region) });
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var read = (System.Windows.Forms.CheckBox)typeof(OnlyWorkspace.ConfigTool.Forms.DataEgressConsentForm).GetField("_read", flags)!.GetValue(form)!;
                var accept = (System.Windows.Forms.Button)typeof(OnlyWorkspace.ConfigTool.Forms.DataEgressConsentForm).GetField("_accept", flags)!.GetValue(form)!;
                var confirm = Descendants(form).OfType<System.Windows.Forms.CheckBox>().FirstOrDefault(c => c.Text.StartsWith("Confermo", StringComparison.Ordinal));
                Assert.Equal(needsConfirmation, confirm != null);
                read.Checked = true;
                Assert.Equal(!needsConfirmation, accept.Enabled);
                if (confirm != null) { confirm.Checked = true; Assert.True(accept.Enabled); confirm.Checked = false; Assert.False(accept.Enabled); }
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static IEnumerable<System.Windows.Forms.Control> Descendants(System.Windows.Forms.Control root)
    {
        foreach (System.Windows.Forms.Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    [Fact]
    public void Notice_IsVersionSixAndSaysSensitiveDataAreNotAnonymized()
    {
        Assert.Equal(6, DataEgressNotice.Version);
        Assert.Contains("viene inviato al servizio", DataEgressNotice.Text);
        Assert.Contains("UE, non UE o non specificata", DataEgressNotice.Text);
    }
}
