using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using OnlyWorkspace.ConfigTool.Forms;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Configuration;

using static OnlyWorkspace.Tests.FormFields;

namespace OnlyWorkspace.Tests;

[Collection(WinFormsCollection.Name)]
public sealed class SelectableUiTests
{
    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "UI test did not complete.");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Fact]
    public void LockedConfigurator_KeepsDiagnosticsSelectableAndInputsDisabled()
    {
        OnSta(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "OnlyWorkspace-selectable-" + Guid.NewGuid());
            try
            {
                new FileConfigurationService(directory, directory).SaveConfiguration(new WorkspaceConfig
                {
                    DefaultProfile = "test",
                    Profiles = new() { new() { Id = "test", Type = "openai", BaseUrl = "https://example.invalid", Model = "" } }
                });
                using var form = new MainConfigForm(directory, null, directory);
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                var type = typeof(MainConfigForm);
                var status = Field<Label>(form, "_lblWebSupport");
                string error = "Errore di verifica: " + new string('x', 500);
                status.Text = error;
                type.GetMethod("SetLocked", flags)!.Invoke(form, new object[] { true });
                var log = Field<TextBox>(form, "_txtLog");
                var endpoint = Field<TextBox>(form, "_txtEndpoint");
                var token = Field<TextBox>(form, "_txtToken");
                Assert.True(log.Enabled);
                Assert.True(log.ReadOnly);
                Assert.False(endpoint.Enabled);
                Assert.False(token.Enabled);
                var edit = Assert.Single(status.Controls.OfType<TextBox>());
                Assert.True(edit.Enabled);
                edit.CreateControl();
                edit.SelectAll();
                Assert.Equal(error, edit.SelectedText);
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, form.ClientRectangle);
                Assert.InRange(form.Width, 820, 1400);
                type.GetMethod("SetLocked", flags)!.Invoke(form, new object[] { false });
                Assert.True(endpoint.Enabled);
            }
            finally { if (Directory.Exists(directory)) TestArtifacts.DeleteDirectory(directory, true); }
        });
    }

    [Fact]
    public void InformationalLabel_RetainsAndSelectsCompleteLongError()
    {
        OnSta(() =>
        {
            var type = typeof(MainConfigForm).Assembly.GetType("OnlyWorkspace.ConfigTool.Forms.SelectableLabel", true)!;
            using var label = (Label)Activator.CreateInstance(type)!;
            label.Size = new Size(220, 24);
            label.Text = "QwenCloud HTTP 429: " + new string('x', 600);
            var edit = Assert.Single(label.Controls.OfType<TextBox>());
            edit.CreateControl();
            Assert.True(edit.ReadOnly);
            Assert.True(edit.Enabled);
            edit.SelectAll();
            Assert.Equal(label.Text, edit.SelectedText);
            edit.Select(10, 8);
            Assert.Equal(label.Text.Substring(10, 8), edit.SelectedText);
            label.ForeColor = Color.DarkRed;
            label.BackColor = Color.White;
            Assert.Equal(label.ForeColor, edit.ForeColor);
            Assert.Equal(label.BackColor, edit.BackColor);
            label.Text = "Nuovo errore";
            edit.SelectAll();
            Assert.Equal(label.Text, edit.SelectedText);
            Assert.Contains(edit.ContextMenuStrip!.Items.Cast<ToolStripItem>(), item => item.Text == "Copia tutto");
        });
    }

    [Fact]
    public void InformationalLabel_PlacesTextInsideItsPadding()
    {
        OnSta(() =>
        {
            var type = typeof(MainConfigForm).Assembly.GetType("OnlyWorkspace.ConfigTool.Forms.SelectableLabel", true)!;
            using var label = (Label)Activator.CreateInstance(type)!;
            // The setup wizard header: docked label with side padding that lines it up with the step text.
            label.Size = new Size(400, 34);
            label.Padding = new Padding(20, 10, 20, 0);
            label.Text = "Passo 1 di 5 · Benvenuto";
            label.PerformLayout();
            var edit = Assert.Single(label.Controls.OfType<TextBox>());
            Assert.Equal(new Rectangle(20, 10, 360, 24), edit.Bounds);
        });
    }

    [Fact]
    public void CopyableConfirmation_ExposesFullTextAndPreservesNoResult()
    {
        OnSta(() =>
        {
            var type = typeof(MainConfigForm).Assembly.GetType("OnlyWorkspace.ConfigTool.Forms.CopyableMessageBox", true)!;
            var show = type.GetMethod("Show", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string), typeof(string), typeof(MessageBoxButtons), typeof(MessageBoxIcon) }, null)!;
            string caption = "Synthetic confirmation " + Guid.NewGuid();
            string message = "Errore dettagliato\n" + new string('x', 600);
            Exception? error = null;
            using var timer = new System.Windows.Forms.Timer { Interval = 50 };
            timer.Tick += (_, _) =>
            {
                var dialog = Application.OpenForms.Cast<Form>().SingleOrDefault(form => form.Text == caption);
                if (dialog == null) return;
                timer.Stop();
                try
                {
                    var content = dialog.Controls.OfType<Panel>().SelectMany(panel => panel.Controls.OfType<TextBox>()).Single();
                    content.SelectAll();
                    Assert.Equal(message.Replace("\n", "\r\n"), content.SelectedText);
                    Assert.True(content.ReadOnly);
                    var buttons = dialog.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().ToList();
                    Assert.Equal(DialogResult.None, buttons.Single(button => button.Text == "Copia messaggio").DialogResult);
                    buttons.Single(button => button.DialogResult == DialogResult.No).PerformClick();
                }
                catch (Exception ex) { error = ex; dialog.Close(); }
            };
            timer.Start();
            var result = show.Invoke(null, new object[] { message, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Warning });
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
            Assert.Equal(DialogResult.No, result);
        });
    }
}
