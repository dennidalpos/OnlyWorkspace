using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using OnlyWorkspace.Core.Privacy;
using OnlyWorkspace.Core.Validation;

namespace OnlyWorkspace.Core.Models;

public sealed class LlmProfileConfig
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>Connector id, see ProviderCatalog.All.</summary>
    public string Type { get; set; } = ProviderCatalog.Ollama;
    public string BaseUrl { get; set; } = ProviderCatalog.Get(ProviderCatalog.Ollama).DefaultBaseUrl;
    public string AuthType { get; set; } = "none"; // see ProviderCapabilities.AuthTypes
    public string SecretRef { get; set; } = string.Empty;
    /// <summary>Recomputed from the vault and verified consent on load; never accepted from config.json.</summary>
    [JsonIgnore]
    public string EgressAuthorizationKey { get; set; } = string.Empty;
    public string Model { get; set; } = ProviderCatalog.Get(ProviderCatalog.Ollama).DefaultModel;
    // Ollama only (fixed num_ctx): cloud providers decide context and output, see ProviderCatalog.PromptBudget.
    public int ContextBudgetTokens { get; set; } = 12698; // ProviderCatalog.ComputeBudget(16384, 2048)
    public int MaxOutputTokens { get; set; } = 2048; // options.num_predict
    public double Temperature { get; set; } = 0.3;
    public int NumCtx { get; set; } = 16384;
    public string KeepAlive { get; set; } = "30m";
    /// <summary>Ollama: send think:false (thinking tokens would eat num_predict and are not shown).</summary>
    public bool DisableThinking { get; set; } = true;
    /// <summary>Request start → first streamed line.</summary>
    public int FirstTokenTimeoutSeconds { get; set; } = 120;
    /// <summary>Max gap between streamed lines; 0 disables.</summary>
    public int IdleTimeoutSeconds { get; set; } = 60;
    public bool UseSystemProxy { get; set; } = false;
    public bool External { get; set; } = false;
    /// <summary>Effective destination policy; the editable flag can make it stricter, never bypass a remote endpoint.</summary>
    [JsonIgnore]
    public bool IsExternal => External || ProviderCatalog.Get(Type).IsExternal ||
        (Uri.TryCreate(BaseUrl, UriKind.Absolute, out var endpoint) && !ConfigurationValidator.IsLoopback(endpoint));
    /// <summary>External profiles only: how much Outlook data the profile may send (ExposurePolicy).</summary>
    public ExposureLevel Exposure { get; set; } = ExposureLevel.Minimal;
    /// <summary>Optional reversible substitutions on external requests; both default to false.</summary>
    public bool PseudonymizeAmounts { get; set; }
    public bool PseudonymizeDates { get; set; }
    /// <summary>External profiles only: attachment text (extracted locally) may be sent to the provider.</summary>
    public bool AllowAttachmentUpload { get; set; } = false;
    /// <summary>Attachment text may reach the model: always for a local profile, else the flag within the exposure level.</summary>
    [JsonIgnore]
    public bool SendsAttachmentText => !IsExternal || (AllowAttachmentUpload && ExposurePolicy.For(Exposure).Attachments);
    /// <summary>Admin permission for web search, verified for the model; it runs on WebSearchActions without asking.</summary>
    public bool WebSearchEnabled { get; set; } = false;
    /// <summary>Pane actions that search the web when WebSearchEnabled (WebSearchAction ids).</summary>
    public List<string> WebSearchActions { get; set; } = WebSearchAction.Defaults();
    /// <summary>Ollama web search key in the encrypted vault, separate from the model endpoint token.</summary>
    public string WebSearchSecretRef { get; set; } = string.Empty;
    /// <summary>Capabilities last read from the provider for Model at BaseUrl; null until read.</summary>
    public ModelCapabilitiesCache? ModelCapabilities { get; set; }
}
