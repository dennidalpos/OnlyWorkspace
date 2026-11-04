using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using OnlyWorkspace.ConfigTool.Forms;

namespace OnlyWorkspace.Tests;

[Collection(WinFormsCollection.Name)]
public sealed class ArchiveSearchFormTests
{
    [Theory]
    [InlineData(1.0f)]
    [InlineData(1.25f)]
    [InlineData(1.5f)]
    public void ArchiveWindow_SeparatesSearchStatusAndMaintenance_AtScaledFonts(float scale)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new ArchiveSearchForm(false, IsolatedArchive.NewPath()) { Size = new Size(1000, 650), Font = new Font("Segoe UI", 9 * scale) };
                form.Show();
                Application.DoEvents();
                var tabs = Assert.Single(Descendants(form).OfType<TabControl>());
                Assert.Equal(new[] { "Ricerca", "Stato e sincronizzazione", "Manutenzione" },
                    tabs.TabPages.Cast<TabPage>().Select(page => page.Text));
                // The pages are switched from a bar of focusable section buttons.
                var sections = Descendants(form).OfType<Button>().Where(b => !Descendants(tabs).Contains(b) && b.Text is "Ricerca" or "Stato e sincronizzazione" or "Manutenzione").ToList();
                Assert.Equal(3, sections.Count);
                // One Tab stop, on the selected section; arrows move between sections.
                Assert.Equal(new[] { true, false, false }, sections.Select(b => b.TabStop));
                sections[2].PerformClick();
                Assert.Equal(2, tabs.SelectedIndex);
                Assert.Equal(new[] { false, false, true }, sections.Select(b => b.TabStop));
                Assert.Contains(Descendants(tabs.TabPages[0]).OfType<SplitContainer>(), split =>
                    split.Orientation == Orientation.Horizontal && split.Panel1.Controls.Count > 0 && split.Panel2.Controls.Count > 0);
                Assert.Contains(Descendants(tabs.TabPages[0]).OfType<Button>(), button => button.Text == "Cerca");
                Assert.DoesNotContain(Descendants(tabs.TabPages[0]).OfType<Button>(), button => button.Text == "Mostra scheda");
                Assert.DoesNotContain(Descendants(tabs.TabPages[0]).OfType<Button>(), button => button.Text == "Rigenera scheda");
                Assert.Contains(Descendants(tabs.TabPages[1]).OfType<Button>(), button => button.Text == "Aggiorna da Outlook");
                Assert.Contains(Descendants(tabs.TabPages[1]).OfType<Button>(), button => button.Text == "Pausa");
                Assert.Contains(Descendants(tabs.TabPages[2]).OfType<Button>(), button => button.Text == "Compatta database");
                foreach (TabPage page in tabs.TabPages) { tabs.SelectTab(page); Assert.Same(page, tabs.SelectedTab); }
                tabs.SelectTab(0);
                Application.DoEvents();
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, form.ClientRectangle);
                var split = Assert.Single(Descendants(tabs.TabPages[0]).OfType<SplitContainer>());
                Assert.True(split.Height > 180, $"Search results and preview need room below the commands (height {split.Height}).");
                Assert.True(split.Panel1.Height > 70 && split.Panel2.Height > 70, "Both search panes must remain visible.");
                form.Size = new Size(680, 480);
                Application.DoEvents();
                Assert.True(split.Panel1.Height > 45 && split.Panel2.Height > 45, "Both panes must remain visible at the minimum window size.");
                var fields = Descendants(form).Where(c => c.Name.StartsWith("Fact", StringComparison.Ordinal)).ToDictionary(c => c.Name);
                fields["FactAmountMin"].Text = "1.000,00"; fields["FactAmountMax"].Text = "1000.01"; fields["FactCurrency"].Text = "eur";
                var from = (DateTimePicker)fields["FactDueFrom"]; from.Value = new DateTime(2026, 11, 1); from.Checked = true;
                var to = (DateTimePicker)fields["FactDueTo"]; to.Value = new DateTime(2026, 11, 30); to.Checked = true;
                var query = form.CreateQuery();
                Assert.Equal(2, query.FactFilters.Count);
                Assert.Equal(1000m, query.FactFilters[0].AmountMin); Assert.Equal(1000.01m, query.FactFilters[0].AmountMax); Assert.Equal("EUR", query.FactFilters[0].Currency);
                Assert.Equal(OnlyWorkspace.Core.Archive.ArchiveFactKind.Deadline, query.FactFilters[1].Kind);
                Assert.Equal("2026-11-01", query.FactFilters[1].DateFrom); Assert.Equal("2026-11-30", query.FactFilters[1].DateTo);
                var text = (Control)typeof(ArchiveSearchForm).GetField("_query", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(form)!;
                text.Text = "ordine ORD-2026/118";
                Assert.Equal(3, form.CreateQuery().FactFilters.Count);
                Assert.Equal("ORD-2026/118", form.CreateQuery().FactFilters[2].Value);
                text.Text = "2026/118"; Assert.Equal("2026/118", form.CreateQuery().Text);
                fields["FactAmountMin"].Text = "1.000"; Assert.Throws<ArgumentException>(() => form.CreateQuery());
                fields["FactAmountMin"].Text = "1000"; from.Value = to.Value.AddDays(1); Assert.Throws<ArgumentException>(() => form.CreateQuery());
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>INT01: invoice filters are folded away, a hint tells when one is set, results get the height, compacting is not red.</summary>
    [Fact]
    public void ArchiveWindow_FoldsInvoiceFilters_GivesResultsTheHeight_AndKeepsCompactNeutral()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new ArchiveSearchForm(false, IsolatedArchive.NewPath()) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000) };
                form.Show();
                Application.DoEvents();
                var factBar = Descendants(form).Single(c => c.Name == "FactFilters");
                var toggle = Descendants(form).OfType<Button>().Single(b => b.Name == "FactFiltersToggle");
                Assert.False(factBar.Visible, "Filters on amounts and due dates start folded.");
                Assert.EndsWith("▸", toggle.Text);
                toggle.PerformClick();
                Assert.True(factBar.Visible);
                Assert.EndsWith("▾", toggle.Text);
                Assert.Equal("Espanso", toggle.AccessibleDescription);
                toggle.PerformClick();
                Descendants(form).Single(c => c.Name == "FactCurrency").Text = "EUR";
                Assert.Contains("(attivi)", toggle.Text);

                var split = Assert.Single(Descendants(form).OfType<SplitContainer>());
                Assert.True(split.Panel1.Height > split.Panel2.Height, $"Results {split.Panel1.Height} px vs preview {split.Panel2.Height} px.");
                var compact = Descendants(form).OfType<FlatButton>().Single(b => b.Text == "Compatta database");
                Assert.NotEqual(ButtonKind.Danger, compact.Kind);
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }
}
