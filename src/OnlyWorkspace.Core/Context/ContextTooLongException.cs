using System;

namespace OnlyWorkspace.Core.Context;

/// <summary>
/// The whole text does not fit the fixed context of the profile. Nothing is sent: an answer built on a part
/// of an email or of a document is not reliable. The message is shown to the user as it is.
/// </summary>
public sealed class ContextTooLongException : Exception
{
    public int EstimatedTokens { get; }
    public int BudgetTokens { get; }

    public ContextTooLongException(int estimatedTokens, int budgetTokens)
        : base(string.Format(System.Globalization.CultureInfo.GetCultureInfo("it-IT"),
               "Il testo è troppo lungo per il profilo in uso: circa {0:N0} token, il suo contesto ne accetta {1:N0}. ", estimatedTokens, budgetTokens) +
               "Non è stato inviato nulla, per non rispondere su una parte sola: scegli un profilo con un contesto più ampio.")
    {
        EstimatedTokens = estimatedTokens;
        BudgetTokens = budgetTokens;
    }
}
