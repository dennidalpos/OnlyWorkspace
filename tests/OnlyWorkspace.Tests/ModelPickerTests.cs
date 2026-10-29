using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using OnlyWorkspace.ConfigTool.Forms;
using OnlyWorkspace.ConfigTool.Services;
using OnlyWorkspace.Core.Interfaces;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Tests;

/// <summary>«Scegli modello…» shared by the profile page and the setup wizard, where «Avanti» without a model opens the list.</summary>
[Collection(WinFormsCollection.Name)]
public sealed class ModelPickerTests
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly LlmProfileConfig Local = new() { Id = "a", Type = "ollama", BaseUrl = "http://127.0.0.1:11434", Model = "" };

    [Fact]
    public async Task List_IsSortedWithoutBlanksOrRepeats_AndFreesTheClient()
    {
        var client = new FakeClient(_ => Task.FromResult<IReadOnlyList<string>>(new[] { "qwen3.5:9b", "", "Gemma3", "gemma3", "llama3.2" }));
        string? key = null;
        var result = await ModelPicker.ListAsync(Local, "tok", (_, s) => { key = s; return client; });
        Assert.Null(result.Error);
        Assert.Equal(new[] { "Gemma3", "llama3.2", "qwen3.5:9b" }, result.Models);
        Assert.Equal("tok", key);
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task List_EmptyIsNoError_FailureAndSilenceAreReported()
    {
        var empty = await ModelPicker.ListAsync(Local, null, (_, _) => new FakeClient(_ => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>())));
        Assert.Empty(empty.Models);
        Assert.Null(empty.Error);

        var failing = new FakeClient(_ => throw new HttpRequestException("401 Unauthorized"));
        var failed = await ModelPicker.ListAsync(Local, null, (_, _) => failing);
        Assert.Contains("401 Unauthorized", failed.Error);
        Assert.Contains(Local.BaseUrl, failed.Error);
        Assert.True(failing.Disposed);

        var silent = await ModelPicker.ListAsync(Local, null, (_, _) => new FakeClient(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Array.Empty<string>();
        }), TimeSpan.FromMilliseconds(50));
        Assert.StartsWith("Nessuna risposta", silent.Error);
    }

    [Fact]
    public async Task List_WithoutEndpoint_AsksForItWithoutCallingTheService()
    {
        bool called = false;
        var result = await ModelPicker.ListAsync(new LlmProfileConfig { Type = "ollama", BaseUrl = " " }, null, (_, _) => { called = true; return new FakeClient(_ => throw new InvalidOperationException()); });
        Assert.False(called);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Menu_ListsTheModelsAndHandsBackTheChosenOne()
    {
        OnSta(() =>
        {
            string? chosen = null;
            using var menu = ModelPicker.Menu(new[] { "a", "b" }, m => chosen = m);
            Assert.Equal(new[] { "a", "b" }, menu.Items.Cast<ToolStripItem>().Select(i => i.Text));
            menu.Items[1].PerformClick();
            Assert.Equal("b", chosen);
        });
    }

    /// <summary>Order Servizio → Indirizzo → Chiave → Modello: a cloud service lists its models only once the key is in.</summary>
    [Fact]
    public void Wizard_AsksTheKeyBeforeTheModel()
    {
        OnSta(() =>
        {
            using var wizard = new SetupWizardForm(Local, null, archiveEnabled: true, indexAttachments: true);
            var page = ((List<Control>)typeof(SetupWizardForm).GetField("_steps", Flags)!.GetValue(wizard)!)[1];
            var rows = page.Controls.Cast<Control>().OfType<TableLayoutPanel>().ToList();
            var key = (Control)typeof(SetupWizardForm).GetField("_txtKey", Flags)!.GetValue(wizard)!;
            var model = (Control)typeof(SetupWizardForm).GetField("_txtModel", Flags)!.GetValue(wizard)!;
            var detect = (Control)typeof(SetupWizardForm).GetField("_btnDetect", Flags)!.GetValue(wizard)!;
            int Row(Control c) => rows.FindIndex(r => Contains(r, c));
            Assert.Equal(2, Row(key));
            Assert.Equal(3, Row(model));
            Assert.Equal(3, Row(detect));
        });
    }

    /// <summary>«Avanti» with an empty model opens the service's list (no «Indica il modello» message) and a click fills the field.</summary>
    [Fact]
    public void Wizard_NextWithoutModel_OpensTheListAndPicks()
    {
        OnSta(() =>
        {
            var client = new FakeClient(_ => Task.FromResult<IReadOnlyList<string>>(new[] { "qwen3.5:9b", "gemma3" }));
            LlmProfileConfig? asked = null;
            string? key = null;
            using var wizard = new SetupWizardForm(Local, "tok", archiveEnabled: true, indexAttachments: true);
            wizard.ClientFactory = (p, s) => { asked = p; key = s; return client; };
            wizard.Show();
            var next = typeof(SetupWizardForm).GetMethod("Next", Flags)!;
            next.Invoke(wizard, null);
            next.Invoke(wizard, null);

            var menu = WaitForMenu();
            Assert.Equal(1, (int)typeof(SetupWizardForm).GetField("_step", Flags)!.GetValue(wizard)!);
            Assert.Equal(Local.BaseUrl, asked!.BaseUrl);
            Assert.Equal("tok", key);
            Assert.Equal(new[] { "gemma3", "qwen3.5:9b" }, menu.Items.Cast<ToolStripItem>().Select(i => i.Text));
            menu.Items[0].PerformClick();
            menu.Close();
            Assert.Equal("gemma3", ((Control)typeof(SetupWizardForm).GetField("_txtModel", Flags)!.GetValue(wizard)!).Text);
            Assert.True(client.Disposed);

            next.Invoke(wizard, null);
            Assert.Equal(2, (int)typeof(SetupWizardForm).GetField("_step", Flags)!.GetValue(wizard)!);
            wizard.Close();
        });
    }

    /// <summary>«Verifica connessione» without a model lists the models instead of failing on an empty name.</summary>
    [Fact]
    public void Wizard_TestWithoutModel_ReportsTheListAndOpensIt()
    {
        OnSta(() =>
        {
            using var wizard = new SetupWizardForm(Local, null, archiveEnabled: true, indexAttachments: true);
            wizard.ClientFactory = (_, _) => new FakeClient(_ => Task.FromResult<IReadOnlyList<string>>(new[] { "m1", "m2", "m3" }));
            wizard.Show();
            var button = new Button();
            var test = (Task)typeof(SetupWizardForm).GetMethod("TestAsync", Flags)!.Invoke(wizard, new object[] { button })!;
            Pump(() => test.IsCompleted);
            test.GetAwaiter().GetResult();
            Assert.Contains("3 modelli disponibili", ((Label)typeof(SetupWizardForm).GetField("_lblTest", Flags)!.GetValue(wizard)!).Text);
            var menu = WaitForMenu();
            Assert.Equal(3, menu.Items.Count);
            menu.Close();
            wizard.Close();
        });
    }

    private static bool Contains(Control parent, Control target) =>
        parent.Controls.Cast<Control>().Any(c => c == target || Contains(c, target));

    private static void Pump(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < until)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        Assert.True(done(), "Timed out.");
    }

    /// <summary>The menu ModelPicker opened on this thread (a top-level window of its own).</summary>
    private static ContextMenuStrip WaitForMenu()
    {
        ContextMenuStrip? found = null;
        Pump(() =>
        {
            EnumThreadWindows(GetCurrentThreadId(), (hwnd, _) =>
            {
                if (Control.FromHandle(hwnd) is ContextMenuStrip { Visible: true } menu) found = menu;
                return found == null;
            }, IntPtr.Zero);
            return found != null;
        });
        return found!;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "UI test did not complete.");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private sealed class FakeClient : ILlmClient, IDisposable
    {
        private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _models;
        public bool Disposed { get; private set; }
        public FakeClient(Func<CancellationToken, Task<IReadOnlyList<string>>> models) => _models = models;

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => _models(cancellationToken);
        public void Dispose() => Disposed = true;
        public LlmTokenUsage? LastTokenUsage => null;
        public LlmCompletion? LastCompletion { get; init; }
        public Task<LlmHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ModelInfo?> GetModelInfoAsync(string model, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> CompleteStructuredAsync(IEnumerable<ChatMessage> messages, string jsonSchema, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<string> StreamChatAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
