using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace SourceGit.Utils
{
    public static class CredentialStore
    {
        private class Entry
        {
            public string U { get; set; } = string.Empty; // encrypted username (base64)
            public string P { get; set; } = string.Empty; // encrypted password/token (base64)
        }

        private static readonly object _lock = new();
        private static Dictionary<string, Entry> _cache;
        private static bool _loaded = false;

        private static string GetStorePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appData, "SourceGit");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "buildserver_creds.json");
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                string path = GetStorePath();
                if (File.Exists(path))
                {
                    try
                    {
                        string json = File.ReadAllText(path, Encoding.UTF8);
                        _cache = JsonSerializer.Deserialize<Dictionary<string, Entry>>(json) ?? new();
                    }
                    catch
                    {
                        _cache = new Dictionary<string, Entry>();
                    }
                }
                else
                {
                    _cache = new Dictionary<string, Entry>();
                }
                _loaded = true;
            }
        }

        private static string MakeKey(string repo, string type, string url)
        {
            return $"{repo}|{type}|{url}";
        }

        public static bool TryGet(string repo, string type, string url, out string username, out string secret)
        {
            EnsureLoaded();
            username = string.Empty;
            secret = string.Empty;

            string key = MakeKey(repo, type, url);
            if (!_cache.TryGetValue(key, out var entry))
                return false;

            try
            {
                byte[] uBytes = Convert.FromBase64String(entry.U);
                byte[] pBytes = Convert.FromBase64String(entry.P);

#if WINDOWS
                username = Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(uBytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
                secret = Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(pBytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
                return true;
#else
                // Non-Windows: do not support DPAPI; skip
                return false;
#endif
            }
            catch
            {
                return false;
            }
        }

        public static void Set(string repo, string type, string url, string username, string secret)
        {
            EnsureLoaded();

#if !WINDOWS
            return;
#else
            byte[] uEnc = System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes(username ?? string.Empty), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            byte[] pEnc = System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes(secret ?? string.Empty), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);

            var entry = new Entry { U = Convert.ToBase64String(uEnc), P = Convert.ToBase64String(pEnc) };

            string key = MakeKey(repo, type, url);
            lock (_lock)
            {
                _cache[key] = entry;
                try
                {
                    string json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = false });
                    File.WriteAllText(GetStorePath(), json, Encoding.UTF8);
                }
                catch
                {
                    // ignore persistence errors
                }
            }
#endif
        }

        public static void Delete(string repo, string type, string url)
        {
            EnsureLoaded();
            string key = MakeKey(repo, type, url);
            lock (_lock)
            {
                if (_cache.Remove(key))
                {
                    try
                    {
                        string json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = false });
                        File.WriteAllText(GetStorePath(), json, Encoding.UTF8);
                    }
                    catch { }
                }
            }
        }
    }
}
