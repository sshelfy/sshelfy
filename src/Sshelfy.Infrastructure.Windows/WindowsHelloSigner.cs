using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Sshelfy.Core.Security;
using Windows.Security.Credentials;

namespace Sshelfy.Infrastructure.Windows;

/// <summary>
/// Windows Hello through <see cref="KeyCredentialManager"/>: an RSA key created in the TPM, bound to
/// the user and usable only after a fingerprint, face or PIN gesture. Windows shows its own prompt.
/// </summary>
public sealed class WindowsHelloSigner : IHelloSigner
{
    // Frozen: "Shellfish" is the app's former name. Renaming the Hello key would orphan existing Hello unlocks.
    private const string KeyName = "Shellfish vault";

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            return await KeyCredentialManager.IsSupportedAsync();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task<HelloStatus> CreateKeyAsync()
    {
        try
        {
            var result = await KeyCredentialManager.RequestCreateAsync(KeyName, KeyCredentialCreationOption.ReplaceExisting);
            return Map(result.Status);
        }
        catch (COMException)
        {
            return HelloStatus.Failed;
        }
    }

    public async Task<HelloSignature> SignAsync(byte[] data)
    {
        try
        {
            var opened = await KeyCredentialManager.OpenAsync(KeyName);
            if (opened.Status != KeyCredentialStatus.Success)
                return new HelloSignature(Map(opened.Status));

            var signed = await opened.Credential.RequestSignAsync(data.AsBuffer());
            return signed.Status == KeyCredentialStatus.Success
                ? new HelloSignature(HelloStatus.Success, signed.Result.ToArray())
                : new HelloSignature(Map(signed.Status));
        }
        catch (COMException)
        {
            return new HelloSignature(HelloStatus.Failed);
        }
    }

    public async Task DeleteKeyAsync()
    {
        try
        {
            await KeyCredentialManager.DeleteAsync(KeyName);
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException)
        {
            // Already gone.
        }
    }

    private static HelloStatus Map(KeyCredentialStatus status) => status switch
    {
        KeyCredentialStatus.Success => HelloStatus.Success,
        KeyCredentialStatus.UserCanceled or KeyCredentialStatus.UserPrefersPassword => HelloStatus.Cancelled,
        KeyCredentialStatus.NotFound => HelloStatus.KeyMissing,
        KeyCredentialStatus.SecurityDeviceLocked => HelloStatus.Unavailable,
        _ => HelloStatus.Failed,
    };
}