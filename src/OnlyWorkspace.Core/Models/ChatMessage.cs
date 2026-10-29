namespace OnlyWorkspace.Core.Models;

public sealed class ChatMessage
{
    public string Role { get; }
    public string Content { get; }

    public ChatMessage(string role, string content)
    {
        Role = role ?? throw new System.ArgumentNullException(nameof(role));
        Content = content ?? string.Empty;
    }

    public static ChatMessage System(string content) => new("system", content);
    public static ChatMessage User(string content) => new("user", content);
    public static ChatMessage Assistant(string content) => new("assistant", content);
}
