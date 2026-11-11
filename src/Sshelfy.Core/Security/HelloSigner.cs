namespace Sshelfy.Core.Security;

public enum HelloStatus
{
    Success,

    /// <summary>The user dismissed the Windows Hello prompt.</summary>
    Cancelled,

    /// <summary>The Hello key is gone (Hello reset, new PIN, other device).</summary>
    KeyMissing,

    /// <summary>Hello is not set up or not supported on this system.</summary>
    Unavailable,

    Failed,
}

public sealed record HelloSignature(HelloStatus Status, byte[]? Signature = null);

/// <summary>
/// A key held by the platform's biometric / PIN store (Windows Hello: an RSA key in the TPM bound
/// to the user). Signing requires the user's gesture; the private key never leaves the device.
/// </summary>
public interface IHelloSigner
{
    Task<bool> IsAvailableAsync();

    /// <summary>Creates (or replaces) the key; the user confirms with Hello.</summary>
    Task<HelloStatus> CreateKeyAsync();

    /// <summary>Signs <paramref name="data"/> after a Hello gesture. RSA PKCS#1 v1.5, so the same data gives the same signature.</summary>
    Task<HelloSignature> SignAsync(byte[] data);

    Task DeleteKeyAsync();
}

/// <summary>Platforms without a Hello-style key store.</summary>
public sealed class NoHelloSigner : IHelloSigner
{
    public Task<bool> IsAvailableAsync() => Task.FromResult(false);

    public Task<HelloStatus> CreateKeyAsync() => Task.FromResult(HelloStatus.Unavailable);

    public Task<HelloSignature> SignAsync(byte[] data) => Task.FromResult(new HelloSignature(HelloStatus.Unavailable));

    public Task DeleteKeyAsync() => Task.CompletedTask;
}
