using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using OnlyWorkspace.ConfigTool.Forms;
using OnlyWorkspace.Core.Models;
using OnlyWorkspace.Infrastructure.Configuration;

namespace OnlyWorkspace.Tests;

[Collection(WinFormsCollection.Name)]
public sealed class NavigationAccessibilityTests
{
    [Fact]
    public void Sidebar_ExposesSelectedTab_AndSwitchesPagesWithVerticalArrows()
    {
        OnSta(() => WithConfigurator(form =>
        {
            var tabs = Descendants(form).OfType<Button>()
                .Where(button => button.Parent is TableLayoutPanel && button.AccessibilityObject.Role == AccessibleRole.PageTab).ToList();
            Assert.Equal(6, tabs.Count);
            AssertSelection(tabs, 0);
            Assert.True(tabs[0].Focus());

            PressArrow(tabs[0], Keys.Down);
            Assert.True(tabs[1].Focused);
            AssertSelection(tabs, 1);
            Assert.Contains(Descendants(form).OfType<Label>(), label => label.Visible && label.Text == "Generale");
            PressArrow(tabs[1], Keys.Up);
            PressArrow(tabs[0], Keys.Up);
            Assert.True(tabs[5].Focused);
            AssertSelection(tabs, 5);
            Assert.Contains(Descendants(form).OfType<Label>(), label => label.Visible && label.Text == "Informazioni");

            tabs[0].AccessibilityObject.DoDefaultAction();
            AssertSelection(tabs, 0);
            tabs[1].AccessibilityObject.Select(AccessibleSelection.TakeFocus | AccessibleSelection.TakeSelection);
            Assert.True(tabs[1].Focused);
            AssertSelection(tabs, 1);
        }));
    }

    [Fact]
    public void Sidebar_ArrowsAndAccessibleActions_SkipUnavailableTabs()
    {
        OnSta(() => WithConfigurator(form =>
        {
            var tabs = Descendants(form).OfType<Button>()
                .Where(button => button.Parent is TableLayoutPanel && button.AccessibilityObject.Role == AccessibleRole.PageTab).ToList();
            tabs[1].Enabled = false;
            tabs[3].Visible = false;
            Assert.True(tabs[0].Focus());

            PressArrow(tabs[0], Keys.Down);
            Assert.True(tabs[2].Focused);
            AssertSelection(tabs, 2);
            PressArrow(tabs[2], Keys.Down);
            Assert.True(tabs[4].Focused);
            AssertSelection(tabs, 4);

            Assert.True(tabs[1].AccessibilityObject.State.HasFlag(AccessibleStates.Unavailable));
            tabs[1].AccessibilityObject.DoDefaultAction();
            tabs[3].AccessibilityObject.Select(AccessibleSelection.TakeFocus | AccessibleSelection.TakeSelection);
            Assert.True(tabs[4].Focused);
            AssertSelection(tabs, 4);
        }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SectionBars_ExposeTheirSelection_AndSwitchContentWithHorizontalArrows(bool archiveWindow)
    {
        OnSta(() =>
        {
            void Check(Form form)
            {
                form.Activate();
                Application.DoEvents();
                var bar = Assert.Single(Descendants(form).OfType<FlowLayoutPanel>(),
                    control => control.AccessibilityObject.Role == AccessibleRole.PageTabList);
                var tabs = bar.Controls.OfType<Button>().ToList();
                // Archive window: Ricerca / Stato / Manutenzione; profile: Connessione / Capacità e verifiche / Contesto / Autorizzazioni.
                int count = archiveWindow ? 3 : 4, last = count - 1;
                Assert.Equal(count, tabs.Count);
                Assert.Equal(count, bar.AccessibilityObject.GetChildCount());
                Assert.Same(tabs[1].AccessibilityObject, bar.AccessibilityObject.GetChild(1));
                Assert.Null(bar.AccessibilityObject.GetChild(-1));
                Assert.Null(bar.AccessibilityObject.GetChild(count));
                AssertSelection(tabs, 0);
                Assert.Same(tabs[0].AccessibilityObject, bar.AccessibilityObject.GetSelected());
                Assert.True(tabs[0].Focus(), $"Initial tab focus failed: visible={tabs[0].Visible}, enabled={tabs[0].Enabled}, canSelect={tabs[0].CanSelect}, active={ReferenceEquals(Form.ActiveForm, form)}.");

                PressArrow(tabs[0], Keys.Left);
                Assert.True(tabs[last].Focused);
                AssertSelection(tabs, last);
                Assert.Same(tabs[last].AccessibilityObject, bar.AccessibilityObject.GetSelected());
                Assert.Same(tabs[last].AccessibilityObject, bar.AccessibilityObject.GetFocused());
                if (archiveWindow)
                    Assert.Equal(2, Assert.Single(Descendants(form).OfType<TabControl>()).SelectedIndex);
                else
                    Assert.Contains(Descendants(form).OfType<CheckBox>(), checkbox => checkbox.Visible && checkbox.Text == "Ricerca web attiva");

                PressArrow(tabs[last], Keys.Right);
                Assert.True(tabs[0].Focused);
                AssertSelection(tabs, 0);
                tabs[1].AccessibilityObject.Select(AccessibleSelection.TakeFocus | AccessibleSelection.TakeSelection);
                Assert.True(tabs[1].Focused);
                AssertSelection(tabs, 1);
                Assert.Same(tabs[1].AccessibilityObject, bar.AccessibilityObject.GetSelected());
            }

            if (archiveWindow)
            {
                using var form = new ArchiveSearchForm(refreshOnShow: false);
                form.Show();
                Application.DoEvents();
                Check(form);
            }
            else WithConfigurator(Check);
        });
    }

    private static void AssertSelection(IReadOnlyList<Button> tabs, int selected)
    {
        for (int i = 0; i < tabs.Count; i++)
        {
            var accessible = tabs[i].AccessibilityObject;
            Assert.Equal(AccessibleRole.PageTab, accessible.Role);
            Assert.Equal(tabs[i].Text, accessible.Name);
            Assert.True(accessible.State.HasFlag(AccessibleStates.Selectable));
            Assert.Equal(i == selected, accessible.State.HasFlag(AccessibleStates.Selected));
            // Only the selected tab is a Tab stop: Tab leaves the group instead of walking every tab.
            Assert.Equal(i == selected, tabs[i].TabStop);
        }
    }

    private static void PressArrow(Button tab, Keys key)
    {
        // PreProcessMessage merges the desktop's real modifier state into synthetic key messages.
        var handler = tab.GetType().GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(true, handler.Invoke(tab, new object[] { key }));
    }

    private static void WithConfigurator(Action<MainConfigForm> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "OnlyWorkspace-navigation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new FileConfigurationService(directory, directory);
            settings.SaveConfiguration(new WorkspaceConfig { Profiles = new() { new() { Id = "ollama-lan", DisplayName = "Synthetic" } } });
            settings.SetUserFlag("guidedSetupDone", true);
            using var form = new MainConfigForm(directory, trayContext: null, preferencesDir: directory);
            form.Show();
            Application.DoEvents();
            check(form);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Navigation UI test did not complete.");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
