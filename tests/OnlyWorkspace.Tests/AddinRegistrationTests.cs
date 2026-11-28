using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows.Forms;
using OnlyWorkspace.ConfigTool.Forms;
using OnlyWorkspace.ConfigTool.Services;
using OnlyWorkspace.Core.Models;

using static OnlyWorkspace.Tests.FormFields;

namespace OnlyWorkspace.Tests;

[Collection(WinFormsCollection.Name)]
public sealed class AddinRegistrationTests
{
    [Fact]
    public void Evaluate_ReportsWhyOutlookWouldNotLoadTheAddin()
    {
        Assert.Equal(AddinState.NotRegistered, AddinRegistration.Evaluate(new AddinSnapshot()).State);
        Assert.Equal(AddinState.Ready, AddinRegistration.Evaluate(new AddinSnapshot { MachineLoadBehavior64 = 3 }).State);
        Assert.Equal(AddinState.Ready, AddinRegistration.Evaluate(new AddinSnapshot { MachineLoadBehavior32 = 3 }).State);
        // The user value wins over the machine one.
        Assert.Equal(AddinState.DisabledByUser, AddinRegistration.Evaluate(new AddinSnapshot { MachineLoadBehavior64 = 3, UserLoadBehavior = 2 }).State);
        Assert.Equal(AddinState.Ready, AddinRegistration.Evaluate(new AddinSnapshot { MachineLoadBehavior64 = 2, UserLoadBehavior = 3 }).State);
        Assert.Equal(AddinState.DisabledByOutlook, AddinRegistration.Evaluate(new AddinSnapshot { MachineLoadBehavior64 = 3, InDisabledItems = true }).State);
    }

    [Fact]
    public void DisabledItemsBlob_IsMatchedOnTheUtf16AddinPath()
    {
        var blob = new byte[] { 1, 0, 0, 0, 80, 0, 0, 0 }.Concat(Encoding.Unicode.GetBytes(@"C:\Program Files\OnlyWorkspace\OnlyWorkspace.Outlook.dll")).ToArray();
        Assert.True(AddinRegistration.MentionsAddin(blob));
        Assert.False(AddinRegistration.MentionsAddin(Encoding.Unicode.GetBytes(@"C:\Other\addin.dll")));
        Assert.False(AddinRegistration.MentionsAddin(null));
    }

    [Fact]
    public void SetupWizard_SummarisesTheChoicesWithoutApplyingThem()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var profile = new LlmProfileConfig { Id = "ollama-lan", DisplayName = "Ollama locale", Model = "synthetic" };
                using var wizard = new SetupWizardForm(profile, null, archiveEnabled: true, indexAttachments: false);
                var next = typeof(SetupWizardForm).GetMethod("Next", BindingFlags.NonPublic | BindingFlags.Instance)!;
                for (int i = 0; i < 4; i++) next.Invoke(wizard, null);
                var summary = Field<Label>(wizard, "_lblSummary");
                Assert.Contains("synthetic", summary.Text);
                Assert.Contains("locale", summary.Text);
                Assert.Contains("senza il testo degli allegati", summary.Text);
                Assert.Equal(DialogResult.None, wizard.DialogResult);
                Assert.Equal("synthetic", profile.Model); // the caller's profile is untouched until applied
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "UI test did not complete.");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    public void SetupWizard_ExplainsLocalIndexingAndNoticeForInteractiveCloudActions(bool archive, bool attachments, bool cloud)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
                var profile = new LlmProfileConfig
                {
                    Id = "synthetic",
                    Type = cloud ? "openai" : "ollama",
                    Model = "synthetic-model",
                    BaseUrl = cloud ? "https://provider.example/v1" : "http://127.0.0.1:11434"
                };
                var original = System.Text.Json.JsonSerializer.Serialize(profile);
                using var wizard = new SetupWizardForm(profile, cloud ? "synthetic-key" : null, archive, attachments);
                var pages = Field<List<Control>>(wizard, "_steps");
                var next = typeof(SetupWizardForm).GetMethod("Next", flags)!;
                // Notes are a visible summary plus a «?» whose AccessibleDescription holds the full text, one level down.
                static IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(All(c)));
                var text = string.Join("\n", pages.SelectMany(All).Select(c => c.Text + "\n" + c.AccessibleDescription));
                Assert.Contains("corpo integrale acquisito", text);
                Assert.Contains("testo degli allegati leggibili", text);
                Assert.Contains("file allegati originali non vengono archiviati", text);
                Assert.Contains("255 caratteri", text);
                Assert.Contains("informativa accettata", text);
                Assert.Contains("La raccolta e la ricerca sono locali e non usano modelli IA", text);
                Assert.DoesNotContain("consenso per cassetta", text);
                Assert.DoesNotContain("non copia il corpo completo", text);
                Assert.DoesNotContain("non invia nulla a servizi IA", text);
                Assert.DoesNotContain("una volta, per ogni profilo", text);

                var note = Field<Label>(wizard, "_lblModelNote").Text;
                Assert.Contains("Privacy e dati", note);
                Assert.Contains(cloud ? "fatti locali con segnaposto" : "senza Web Search", note);
                var tips = Field<ToolTip>(wizard, "_tips");
                var attachmentChoice = Field<Control>(wizard, "_chkAttachments");
                Assert.Contains("Conserva nell'archivio cifrato il testo", tips.GetToolTip(attachmentChoice));
                Assert.Contains("Non autorizza da solo invii cloud", tips.GetToolTip(attachmentChoice));

                for (int i = 0; i < 4; i++) next.Invoke(wizard, null);
                var summary = Field<Label>(wizard, "_lblSummary").Text;
                Assert.Contains(archive ? "corpo integrale acquisito" : "Archivio: non consentito", summary);
                if (archive) Assert.Contains(attachments ? ", con il testo degli allegati." : ", senza il testo degli allegati.", summary);
                Assert.Contains("Gli invii esterni delle azioni IA e di Web Search richiedono l'informativa accettata", summary);
                Assert.DoesNotContain("consenso per cassetta", summary);
                Assert.Equal(archive, wizard.ArchiveEnabled);
                Assert.Equal(attachments, wizard.IndexAttachments);
                Assert.Equal(DialogResult.None, wizard.DialogResult);
                Assert.Equal(original, System.Text.Json.JsonSerializer.Serialize(profile));
                wizard.Close();
                Assert.NotEqual(DialogResult.OK, wizard.DialogResult);
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "UI test did not complete.");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Fact]
    public void SetupWizard_InformativePagesWrapAtMinimumSize()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
                using var wizard = new SetupWizardForm(new LlmProfileConfig { Id = "synthetic", Model = "synthetic" }, null, true, true);
                wizard.Show();
                wizard.Size = wizard.MinimumSize;
                var pages = Field<List<Control>>(wizard, "_steps");
                foreach (int step in new[] { 0, 2, 3, 4 })
                {
                    typeof(SetupWizardForm).GetMethod("ShowStep", flags)!.Invoke(wizard, new object[] { step });
                    Application.DoEvents();
                    var page = (ScrollableControl)pages[step];
                    Assert.False(page.HorizontalScroll.Visible, $"Informative page {step} requires horizontal scrolling.");
                    foreach (var label in page.Controls.OfType<Label>())
                        Assert.True(label.Right <= page.ClientSize.Width, $"Text on page {step} extends past the viewport.");
                }
                wizard.Close();
                Assert.NotEqual(DialogResult.OK, wizard.DialogResult);
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "UI test did not complete.");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
