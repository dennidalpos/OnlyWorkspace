using OnlyWorkspace.ConfigTool.Forms;
using OnlyWorkspace.ConfigTool.Services;

namespace OnlyWorkspace.Tests;

/// <summary>#177: the administrator-password flow outside MainConfigForm, with scripted answers and no dialogs.</summary>
public sealed class AdminPasswordFlowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OnlyWorkspace_Test_AdminPw_" + Guid.NewGuid().ToString("N"));

    public AdminPasswordFlowTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) TestArtifacts.DeleteDirectory(_dir, true);
    }

    private sealed class Host : IAdminPasswordHost
    {
        public string ConfigDirectory { get; set; } = "";
        public bool IsLocked { get; set; }
        public Queue<string?> Answers = new();
        public List<string> Prompts = new(), Warnings = new(), Errors = new(), Statuses = new(), Log = new();
        public List<string> ElevatedCalls = new();
        public ElevatedConfigWriter.ElevationResult Elevated = new() { Success = true };
        public List<bool> LockChanges = new();
        public string? AskPassword(string title, string label) { Prompts.Add(title); return Answers.Count > 0 ? Answers.Dequeue() : null; }
        public void Warn(string message, string title) => Warnings.Add(message);
        public void ShowError(string message, string title) => Errors.Add(message);
        public Task<ElevatedConfigWriter.ElevationResult> RunElevatedAsync(string arguments, string? resultDir) { ElevatedCalls.Add(arguments); return Task.FromResult(Elevated); }
        public void SetLocked(bool locked) => LockChanges.Add(locked);
        public void SetStatus(string message, Tone tone) => Statuses.Add(tone + ":" + message);
        public void LogActivity(string message) => Log.Add(message);
    }

    private (AdminPasswordService Service, Host Host, AdminPasswordFlow Flow) Setup(string? existing = null)
    {
        var service = new AdminPasswordService(_dir);
        if (existing != null) service.ApplyRecord(AdminPasswordService.CreateRecord(existing));
        var host = new Host { ConfigDirectory = _dir };
        return (service, host, new AdminPasswordFlow(service, host));
    }

    [Fact]
    public void Unlock_WithAWrongPassword_WarnsAndStaysLocked()
    {
        var (_, host, flow) = Setup("correct-horse-1");
        host.Answers.Enqueue("wrong");
        flow.Unlock();
        Assert.Equal(new[] { "Password non corretta." }, host.Warnings);
        Assert.Empty(host.LockChanges);
    }

    [Fact]
    public void Unlock_WithTheRightPassword_UnlocksTheWindow()
    {
        var (_, host, flow) = Setup("correct-horse-1");
        host.Answers.Enqueue("correct-horse-1");
        flow.Unlock();
        Assert.Equal(new[] { false }, host.LockChanges);
        Assert.Single(host.Statuses);
        Assert.Empty(host.Warnings);
    }

    [Fact]
    public void Unlock_Cancelled_DoesNothing()
    {
        var (_, host, flow) = Setup("correct-horse-1");
        flow.Unlock();
        Assert.Empty(host.Warnings);
        Assert.Empty(host.LockChanges);
    }

    [Fact]
    public async Task Manage_WhileLocked_AsksNothing()
    {
        var (_, host, flow) = Setup("correct-horse-1");
        host.IsLocked = true;
        await flow.ManageAsync();
        Assert.Empty(host.Prompts);
    }

    [Fact]
    public async Task Manage_WithAWrongCurrentPassword_StopsBeforeAskingTheNewOne()
    {
        var (_, host, flow) = Setup("correct-horse-1");
        host.Answers.Enqueue("wrong");
        await flow.ManageAsync();
        Assert.Single(host.Prompts);
        Assert.Equal(new[] { "Password non corretta." }, host.Warnings);
    }

    [Fact]
    public async Task Manage_RefusesAShortNewPassword_AndOneThatIsNotConfirmed()
    {
        var (service, host, flow) = Setup("correct-horse-1");
        host.Answers.Enqueue("correct-horse-1");
        host.Answers.Enqueue("short");
        await flow.ManageAsync();
        Assert.Contains("almeno " + AdminPasswordService.MinLength, host.Warnings[0]);

        host.Answers.Enqueue("correct-horse-1");
        host.Answers.Enqueue("new-password-9");
        host.Answers.Enqueue("new-password-8");
        await flow.ManageAsync();
        Assert.Equal("Le password non coincidono.", host.Warnings[1]);
        Assert.True(service.Verify("correct-horse-1"));
    }

    [Fact]
    public async Task Manage_ChangesThePasswordDirectly_WhenTheFolderIsWritable()
    {
        var (service, host, flow) = Setup("correct-horse-1");
        host.Answers.Enqueue("correct-horse-1");
        host.Answers.Enqueue("new-password-9");
        host.Answers.Enqueue("new-password-9");
        await flow.ManageAsync();

        Assert.True(service.Verify("new-password-9"));
        Assert.False(service.Verify("correct-horse-1"));
        Assert.Empty(host.ElevatedCalls);
        Assert.Equal(new[] { false }, host.LockChanges);
        Assert.Equal("Success:Password amministrativa aggiornata.", host.Statuses.Last());
    }

    [Fact]
    public async Task Manage_WithAnEmptyNewPassword_SwitchesTheProtectionOff()
    {
        var (service, host, flow) = Setup("correct-horse-1");
        host.Answers.Enqueue("correct-horse-1");
        host.Answers.Enqueue("");
        host.Answers.Enqueue("");
        await flow.ManageAsync();

        Assert.False(service.Enabled);
        Assert.Equal("Success:Password amministrativa disattivata.", host.Statuses.Last());
    }

    [Fact]
    public async Task Reset_AlwaysGoesThroughTheElevatedInstance()
    {
        var (service, host, flow) = Setup("correct-horse-1");
        host.Answers.Enqueue("new-password-9");
        host.Answers.Enqueue("new-password-9");
        await flow.ResetAsync();

        Assert.Single(host.ElevatedCalls);
        Assert.StartsWith("admin-password-apply ", host.ElevatedCalls[0]);
        Assert.Empty(host.Errors);
        Assert.True(service.Verify("correct-horse-1")); // the fake elevated instance wrote nothing
    }

    [Fact]
    public async Task Reset_CancelledAtTheUacPrompt_KeepsThePassword_AndSaysSo()
    {
        var (service, host, flow) = Setup("correct-horse-1");
        host.Elevated = new ElevatedConfigWriter.ElevationResult { Cancelled = true };
        host.Answers.Enqueue("new-password-9");
        host.Answers.Enqueue("new-password-9");
        await flow.ResetAsync();

        Assert.Equal("Warning:Password non modificata: autorizzazione amministratore non concessa.", host.Statuses.Last());
        Assert.Empty(host.LockChanges);
        Assert.True(service.Verify("correct-horse-1"));
    }

    [Fact]
    public async Task Reset_ThatFails_ShowsTheErrorAndLogsIt()
    {
        var (_, host, flow) = Setup("correct-horse-1");
        host.Elevated = new ElevatedConfigWriter.ElevationResult { Message = "boom" };
        host.Answers.Enqueue("new-password-9");
        host.Answers.Enqueue("new-password-9");
        await flow.ResetAsync();

        Assert.Single(host.Errors);
        Assert.Contains("boom", host.Log[0]);
        Assert.Empty(host.LockChanges);
    }
}
