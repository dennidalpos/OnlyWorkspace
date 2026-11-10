namespace OnlyWorkspace.Core.Interfaces;

public interface ISecretVault
{
    void SetSecret(string key, string secret);
    string? GetSecret(string key);
    bool HasSecret(string key);
    void RemoveSecret(string key);
    void Save(string filePath);
    void Load(string filePath);
}
