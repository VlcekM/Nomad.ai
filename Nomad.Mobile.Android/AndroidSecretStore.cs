using System.Text;
using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using Nomad.Mobile.Hub;

namespace Nomad.Mobile.Android;

/// <summary>
/// Keeps the enrollment (hub address, device id and secret) in the app's private preferences, each value encrypted with AES-GCM under
/// a key that lives in the Android Keystore and never leaves it. A value that no longer decrypts (the key was lost, say after a
/// restore to another phone) reads as missing, so the app asks to connect again.
/// </summary>
public sealed class AndroidSecretStore(Context context) : ISecretStore
{
    private const string KeyAlias = "nomad-mobile-secrets";
    private const string Provider = "AndroidKeyStore";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int TagBits = 128;

    private readonly ISharedPreferences _preferences = context.GetSharedPreferences("nomad-secrets", FileCreationMode.Private)!;

    public string? Get(string key)
    {
        if (_preferences.GetString(key, null) is not { Length: > 0 } stored)
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(stored);
            var ivLength = bytes[0];
            var cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.DecryptMode, Key(), new GCMParameterSpec(TagBits, bytes, 1, ivLength));
            return Encoding.UTF8.GetString(cipher.DoFinal(bytes, 1 + ivLength, bytes.Length - 1 - ivLength)!);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Set(string key, string value)
    {
        var cipher = Cipher.GetInstance(Transformation)!;
        cipher.Init(CipherMode.EncryptMode, Key());
        var iv = cipher.GetIV()!;
        var sealedBytes = cipher.DoFinal(Encoding.UTF8.GetBytes(value))!;
        var stored = new byte[1 + iv.Length + sealedBytes.Length];
        stored[0] = (byte)iv.Length;
        iv.CopyTo(stored, 1);
        sealedBytes.CopyTo(stored, 1 + iv.Length);
        _preferences.Edit()!.PutString(key, Convert.ToBase64String(stored))!.Commit();
    }

    public void Remove(string key) => _preferences.Edit()!.Remove(key)!.Commit();

    private static IKey Key()
    {
        var store = KeyStore.GetInstance(Provider)!;
        store.Load(null);
        if (store.GetKey(KeyAlias, null) is { } existing)
        {
            return existing;
        }

        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, Provider)!;
        generator.Init(new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)!
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
            .SetKeySize(256)!
            .Build());
        return generator.GenerateKey()!;
    }
}
