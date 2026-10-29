using System.Collections.Generic;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Core.Interfaces;

public interface IContextBuilder
{
    /// <summary>Body without HTML and quoted replies; a forward keeps the forwarded message unless <paramref name="senderTextOnly"/>.</summary>
    string CleanEmailBody(string rawBody, string? subject = null, bool senderTextOnly = false);
    /// <summary>True when the whole text fits the budget (tokens, ~3.5 chars each); 0 = no limit.</summary>
    bool Fits(string text, int budgetTokens);
    /// <summary>The text unchanged, or a <see cref="OnlyWorkspace.Core.Context.ContextTooLongException"/>: never a cut.</summary>
    string EnsureFits(string text, int budgetTokens);
    /// <summary>List contexts take whole items while they fit; <paramref name="included"/> says how many.</summary>
    string FormatEmailsContext(IEnumerable<EmailSearchResult> emails, int budgetTokens, out int included);
    string FormatCalendarContext(IEnumerable<AppointmentInfo> appointments, int budgetTokens, out int included);
    string FormatContactsContext(IEnumerable<ContactInfo> contacts, int budgetTokens, out int included);
}
