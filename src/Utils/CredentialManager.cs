using System;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace SourceGit.Utils
{
    internal static class CredentialManager
    {
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWriteW(ref CREDENTIAL credential, uint flags);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredReadW(string targetName, CRED_TYPE type, int flags, out IntPtr credential);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDeleteW(string targetName, CRED_TYPE type, int flags);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern void CredFree(IntPtr credential);

        private enum CRED_TYPE : uint
        {
            GENERIC = 1,
            DOMAIN_PASSWORD = 2,
            DOMAIN_CERTIFICATE = 3,
            DOMAIN_VISIBLE_PASSWORD = 4
        }

        private enum CRED_PERSIST : uint
        {
            SESSION = 1,
            LOCAL_MACHINE = 2,
            ENTERPRISE = 3
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public uint Flags;
            public CRED_TYPE Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public CRED_PERSIST Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        public static void WriteCredential(string targetName, NetworkCredential credential)
        {
            string password = credential.Password ?? string.Empty;
            byte[] passwordBytes = Encoding.Unicode.GetBytes(password);
            
            IntPtr passwordPtr = IntPtr.Zero;
            try
            {
                passwordPtr = Marshal.AllocCoTaskMem(passwordBytes.Length);
                Marshal.Copy(passwordBytes, 0, passwordPtr, passwordBytes.Length);

                var cred = new CREDENTIAL
                {
                    Type = CRED_TYPE.GENERIC,
                    TargetName = targetName,
                    UserName = credential.UserName ?? string.Empty,
                    CredentialBlobSize = (uint)passwordBytes.Length,
                    CredentialBlob = passwordPtr,
                    Persist = CRED_PERSIST.LOCAL_MACHINE,
                    AttributeCount = 0,
                    Attributes = IntPtr.Zero,
                    Comment = "SourceGit Build Server Credential",
                    TargetAlias = null
                };

                if (!CredWriteW(ref cred, 0))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new Exception($"CredWriteW failed with error code {error}");
                }
            }
            finally
            {
                if (passwordPtr != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(passwordPtr);
                }
            }
        }

        public static NetworkCredential ReadCredential(string targetName)
        {
            IntPtr credPtr = IntPtr.Zero;
            try
            {
                if (!CredReadW(targetName, CRED_TYPE.GENERIC, 0, out credPtr))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 1168) // ERROR_NOT_FOUND
                    {
                        return null;
                    }
                    throw new Exception($"CredReadW failed with error code {error}");
                }

                var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
                
                string password = string.Empty;
                if (cred.CredentialBlobSize > 0 && cred.CredentialBlob != IntPtr.Zero)
                {
                    byte[] passwordBytes = new byte[cred.CredentialBlobSize];
                    Marshal.Copy(cred.CredentialBlob, passwordBytes, 0, (int)cred.CredentialBlobSize);
                    password = Encoding.Unicode.GetString(passwordBytes);
                }

                return new NetworkCredential(cred.UserName, password);
            }
            finally
            {
                if (credPtr != IntPtr.Zero)
                {
                    CredFree(credPtr);
                }
            }
        }

        public static void DeleteCredential(string targetName)
        {
            if (!CredDeleteW(targetName, CRED_TYPE.GENERIC, 0))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 1168) // Ignore ERROR_NOT_FOUND
                {
                    throw new Exception($"CredDeleteW failed with error code {error}");
                }
            }
        }
    }
}
