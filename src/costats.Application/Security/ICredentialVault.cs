namespace costats.Application.Security;

public interface ICredentialVault
{
    Task SaveAsync(string key, string secret, CancellationToken cancellationToken);

    Task<string?> LoadAsync(string key, CancellationToken cancellationToken);
}
