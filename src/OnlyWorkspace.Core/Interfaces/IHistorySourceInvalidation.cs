using System.Threading.Tasks;
using OnlyWorkspace.Core.Models;

namespace OnlyWorkspace.Core.Interfaces;

public interface IHistorySourceInvalidation
{
    /// <summary>False once the conversation uses an email deleted from the archive; true also while the history is busy (asked again later).</summary>
    Task<bool> IsValidAsync(ConversationHistory conversation);
}
