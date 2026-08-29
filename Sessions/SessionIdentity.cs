using System.Security.Cryptography;

namespace SystemExplorer.CodeService;

internal readonly record struct SessionIdentity(string SessionId)
{
    private const int SessionIdByteCount = 16;

    public static SessionIdentity Create()
    {
        Span<byte> sessionIdBytes = stackalloc byte[SessionIdByteCount];
        RandomNumberGenerator.Fill(sessionIdBytes);
        return new SessionIdentity(Convert.ToHexString(sessionIdBytes).ToLowerInvariant());
    }
}
