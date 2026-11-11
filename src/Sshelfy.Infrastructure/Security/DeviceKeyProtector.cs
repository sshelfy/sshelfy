using System.Security.Cryptography;

namespace Sshelfy.Infrastructure.Security;

/// <summary>Protects the vault key with credentials of the current OS user.</summary>
internal interface IDeviceKeyProtector
{
    bool IsSupported { get; }

    byte[] Protect(byte[] data, byte[] entropy);

    byte[] Unprotect(byte[] data, byte[] entropy);
}

/// <summary>Windows DPAPI (CurrentUser scope). Other platforms report <see cref="IsSupported"/> = false.</summary>
internal sealed class DeviceKeyProtector : IDeviceKeyProtector
{
    public bool IsSupported => OperatingSystem.IsWindows();

    public byte[] Protect(byte[] data, byte[] entropy) =>
        OperatingSystem.IsWindows()
            ? ProtectedData.Protect(data, entropy, DataProtectionScope.CurrentUser)
            : throw new PlatformNotSupportedException();

    public byte[] Unprotect(byte[] data, byte[] entropy) =>
        OperatingSystem.IsWindows()
            ? ProtectedData.Unprotect(data, entropy, DataProtectionScope.CurrentUser)
            : throw new PlatformNotSupportedException();
}
