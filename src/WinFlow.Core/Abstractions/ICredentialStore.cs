namespace WinFlow.Core.Abstractions;

/// <summary>Secure storage for the OpenAI API key.</summary>
public interface ICredentialStore
{
    string? GetApiKey();

    void SetApiKey(string apiKey);

    void DeleteApiKey();
}
