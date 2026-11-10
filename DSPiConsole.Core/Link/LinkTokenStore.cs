using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DSPiConsole.Core.Link;

/// <summary>
/// The pairing token for each hub (spec 6.2), keyed by the hub's id rather
/// than its address, so a hub that moves to a new address is still known.
/// Kept in one file encrypted with DPAPI for the current Windows user, so
/// tokens can't be read by another account or copied to another machine.
/// Port of the macOS Console's LinkTokenStore (which uses the keychain).
/// </summary>
public sealed class LinkTokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DSPiConsole.LinkTokens.v1");
    private readonly string _path;
    private readonly object _lock = new();

    /// <param name="path">The token file; tests pass their own. Defaults to
    /// %LOCALAPPDATA%\DSPiConsole\link-tokens.dat.</param>
    public LinkTokenStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSPiConsole", "link-tokens.dat");
    }

    public string? Token(string hubId)
    {
        lock (_lock) return Load().GetValueOrDefault(Key(hubId));
    }

    public void SetToken(string hubId, string token)
    {
        lock (_lock)
        {
            var all = Load();
            all[Key(hubId)] = token;
            Save(all);
        }
    }

    public void RemoveToken(string hubId)
    {
        lock (_lock)
        {
            var all = Load();
            if (all.Remove(Key(hubId))) Save(all);
        }
    }

    private static string Key(string hubId) => hubId.Trim().ToLowerInvariant();

    /// <summary>The stored tokens; empty when there are none or the file can't
    /// be read (another user's, damaged): pairing again is the remedy.</summary>
    private Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? new();
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    private void Save(Dictionary<string, string> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var cipher = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(all), Entropy, DataProtectionScope.CurrentUser);
        // Written aside and moved over, so a crash mid-write can't lose every token.
        var temp = _path + ".tmp";
        File.WriteAllBytes(temp, cipher);
        File.Move(temp, _path, overwrite: true);
    }
}
