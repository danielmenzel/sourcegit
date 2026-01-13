using System;
using System.Net;

namespace SourceGit.Utils
{
    public static class CredentialStore
    {
        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            // Normalize to forward slashes and remove trailing slashes for consistent key generation
            return path.Replace('\\', '/').TrimEnd('/');
        }

        private static string MakeTargetName(string repo, string type, string url)
        {
            return $"SourceGit:{NormalizePath(repo)}:{type}:{url}";
        }

        public static void ForceReload()
        {
            // No-op: Windows Credential Manager handles caching automatically
        }

        public static bool TryGet(string repo, string type, string url, out string username, out string secret)
        {
            username = string.Empty;
            secret = string.Empty;

            string targetName = MakeTargetName(repo, type, url);
            
            System.Diagnostics.Debug.WriteLine($"[CredentialStore] TryGet - Looking for credential:");
            System.Diagnostics.Debug.WriteLine($"  Original repo path: '{repo}'");
            System.Diagnostics.Debug.WriteLine($"  Normalized repo path: '{NormalizePath(repo)}'");
            System.Diagnostics.Debug.WriteLine($"  Target name: '{targetName}'");
            
            try
            {
                NetworkCredential cred = CredentialManager.ReadCredential(targetName);
                if (cred != null)
                {
                    username = cred.UserName;
                    secret = cred.Password;
                    System.Diagnostics.Debug.WriteLine($"[CredentialStore] Credential found for user: '{username}'");
                    return true;
                }

                System.Diagnostics.Debug.WriteLine($"[CredentialStore] Credential not found");
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CredentialStore] Error reading credential: {ex.Message}");
                return false;
            }
        }

        public static void Set(string repo, string type, string url, string username, string secret)
        {
            string targetName = MakeTargetName(repo, type, url);
            System.Diagnostics.Debug.WriteLine($"[CredentialStore] Set - Storing credential:");
            System.Diagnostics.Debug.WriteLine($"  Original repo path: '{repo}'");
            System.Diagnostics.Debug.WriteLine($"  Normalized repo path: '{NormalizePath(repo)}'");
            System.Diagnostics.Debug.WriteLine($"  Type: '{type}'");
            System.Diagnostics.Debug.WriteLine($"  URL: '{url}'");
            System.Diagnostics.Debug.WriteLine($"  Target name: '{targetName}'");
            System.Diagnostics.Debug.WriteLine($"  Username: '{username}'");
            
            try
            {
                var cred = new NetworkCredential(username ?? string.Empty, secret ?? string.Empty);
                CredentialManager.WriteCredential(targetName, cred);
                System.Diagnostics.Debug.WriteLine($"[CredentialStore] Credential saved successfully");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CredentialStore] Error saving credential: {ex.Message}");
            }
        }

        public static void Delete(string repo, string type, string url)
        {
            string targetName = MakeTargetName(repo, type, url);
            try
            {
                CredentialManager.DeleteCredential(targetName);
                System.Diagnostics.Debug.WriteLine($"[CredentialStore] Credential deleted: '{targetName}'");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CredentialStore] Error deleting credential: {ex.Message}");
            }
        }
    }
}
