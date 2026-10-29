using System;

namespace OnlyWorkspace.Outlook.Services;

/// <summary>
/// Whether a store can read the extended archive columns (Message-ID, In-Reply-To, References, ConversationId...).
/// Only a failure the basic columns do not share is the columns' fault: a store closing or a folder gone says
/// nothing about them, and remembering it would drop the chain metadata of a PST reopened in the same session.
/// </summary>
internal static class ArchiveColumnProbe
{
    /// <returns>The verdict to remember for the store; null when the basic columns fail too (<paramref name="failure"/> is then the extended one).</returns>
    public static bool? Probe(Action extended, Action basic, out Exception? failure)
    {
        failure = null;
        try
        {
            extended();
            return true;
        }
        catch (Exception ex) { failure = ex; }
        try
        {
            basic();
            return false;
        }
        catch { return null; }
    }
}

/// <summary>The store of a folder is no longer available: a mailbox closed, not a failure of the folder.</summary>
internal sealed class ArchiveStoreClosedException : InvalidOperationException
{
    public ArchiveStoreClosedException() : base("The mailbox of the folder is no longer open.") { }
}
