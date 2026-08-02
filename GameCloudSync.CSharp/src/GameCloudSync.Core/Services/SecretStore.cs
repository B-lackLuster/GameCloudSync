using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameCloudSync.Core.Services;

/// <summary>
/// Stores the WebDAV password outside config.json, bound to the current Windows user.
/// New credentials are written with DPAPI; legacy "fernet" credentials from the
/// Python version can still be read and migrated.
/// </summary>
public sealed class SecretStore
{
    private readonly string _appDir;
    private readonly object _lock = new();

    public SecretStore(string appDir)
    {
        _appDir = appDir;
    }

    private string CredPath => Path.Combine(_appDir, Constants.CredFileName);

    public bool HasStoredPassword()
    {
        var cred = LoadCredJson();
        if (cred is null || cred["cipher"] is null)
            return false;
        var scheme = cred["scheme"]?.GetValue<string>() ?? "dpapi";
        return scheme is "dpapi" or "fernet";
    }

    public string? LoadPassword()
    {
        lock (_lock)
        {
            var cred = LoadCredJson();
            if (cred is null || cred["cipher"] is null)
                return null;
            try
            {
                var cipher = Convert.FromBase64String(cred["cipher"]!.GetValue<string>());
                var scheme = cred["scheme"]?.GetValue<string>() ?? "dpapi";
                byte[] plain = scheme switch
                {
                    "dpapi" => ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser),
                    "fernet" => FernetDecrypt(cipher),
                    _ => throw new InvalidOperationException("Unknown credential scheme")
                };
                return Encoding.UTF8.GetString(plain);
            }
            catch
            {
                return null;
            }
        }
    }

    public bool SavePassword(string password)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(password))
                return ClearPassword();
            try
            {
                var plain = Encoding.UTF8.GetBytes(password);
                var cipher = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
                var cred = new JsonObject
                {
                    ["scheme"] = "dpapi",
                    ["cipher"] = Convert.ToBase64String(cipher)
                };
                WriteCredJson(cred);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool ClearPassword()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(CredPath))
                    File.Delete(CredPath);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private JsonObject? LoadCredJson()
    {
        if (!File.Exists(CredPath))
            return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(CredPath)) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private void WriteCredJson(JsonObject cred)
    {
        var tmp = CredPath + ".tmp";
        File.WriteAllText(tmp, cred.ToJsonString(), new UTF8Encoding(false));
        File.Move(tmp, CredPath, overwrite: true);
    }

    // ---- Legacy Fernet compatibility (Python cryptography.Fernet) ----

    private byte[] FernetDecrypt(byte[] token)
    {
        if (token.Length < 57 || token[0] != 0x80)
            throw new InvalidOperationException("Invalid Fernet token");
        var key = GetFernetKey();
        var mac = token[^32..];
        var signed = token[..^32];
        using var hmac = new HMACSHA256(key);
        var expected = hmac.ComputeHash(signed);
        if (!CryptographicOperations.FixedTimeEquals(mac, expected))
            throw new InvalidOperationException("Fernet MAC mismatch");

        var iv = signed[9..25];
        var ciphertext = signed[25..];
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }

    private byte[] GetFernetKey()
    {
        var saltPath = Path.Combine(_appDir, Constants.SaltFileName);
        byte[] salt;
        if (File.Exists(saltPath))
        {
            salt = File.ReadAllBytes(saltPath);
        }
        else
        {
            salt = RandomNumberGenerator.GetBytes(32);
            try
            {
                File.WriteAllBytes(saltPath, salt);
            }
            catch
            {
                // best effort
            }
        }

        var machine = Environment.GetEnvironmentVariable("COMPUTERNAME") ?? "unknown";
        var material = Encoding.UTF8.GetBytes($"{machine}|{_appDir}");
        var derived = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            material,
            outputLength: 32,
            salt: salt,
            info: Encoding.UTF8.GetBytes("GameCloudSync/webdav"));
        return derived;
    }
}
