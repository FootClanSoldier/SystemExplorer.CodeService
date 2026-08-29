using System.Security.Cryptography;

namespace SystemExplorer.CodeService;

internal sealed class SessionCredentials : IDisposable
{
    internal const int AuthenticationTokenByteCount = 32;
    internal const int AuthenticationTokenBase64Length =
        ((AuthenticationTokenByteCount + 2) / 3) * 4;

    private byte[]? _authenticationToken;

    private SessionCredentials(byte[] authenticationToken)
    {
        _authenticationToken = authenticationToken;
    }

    internal ReadOnlySpan<byte> AuthenticationToken
    {
        get
        {
            byte[] token = Volatile.Read(ref _authenticationToken)
                ?? throw new ObjectDisposedException(nameof(SessionCredentials));
            return token;
        }
    }

    public static SessionCredentials Create()
    {
        byte[] authenticationToken = new byte[AuthenticationTokenByteCount];

        try
        {
            RandomNumberGenerator.Fill(authenticationToken);
            return new SessionCredentials(authenticationToken);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(authenticationToken);
            throw;
        }
    }

    internal bool Matches(ReadOnlySpan<byte> candidate)
    {
        byte[]? authenticationToken = Volatile.Read(ref _authenticationToken);
        return authenticationToken is not null
            && candidate.Length == authenticationToken.Length
            && CryptographicOperations.FixedTimeEquals(authenticationToken, candidate);
    }

    public void Dispose()
    {
        byte[]? authenticationToken = Interlocked.Exchange(ref _authenticationToken, null);
        if (authenticationToken is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(authenticationToken);
    }
}
