﻿using log4net;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Toastify
{
    public static class Security
    {
        private static readonly ILog logger = LogManager.GetLogger(typeof(Security));

        public static bool ProtectedDataExists(string fileName)
        {
            string secFilePath = Path.Combine(App.LocalApplicationData, fileName);
            return File.Exists(secFilePath);
        }

        #region Proxy Password

        internal static string GetProxyPassword()
        {
            byte[] data = GetProtectedData("proxy.sec");
            return data != null ? Encoding.UTF8.GetString(data) : null;
        }

        internal static SecureString GetSecureProxyPassword()
        {
            SecureString secureString = GetProtectedSecureString("proxy.sec");
            return secureString != null && secureString.Length > 0 ? secureString : null;
        }

        internal static void SaveProxyPassword(byte[] plaintext)
        {
            SaveProtectedData(plaintext, "proxy.sec");
        }

        internal static void SaveProxyPassword(SecureString secureString)
        {
            SaveProtectedData(secureString, "proxy.sec");
        }

        #endregion Proxy Password

        internal static byte[] GetProtectedData(string fileName)
        {
            byte[] encryptedData = GetProtectedDataInternal(fileName, out byte[] entropy);
            return ProtectedData.Unprotect(encryptedData, entropy, DataProtectionScope.CurrentUser);
        }

        internal static SecureString GetProtectedSecureString(string fileName)
        {
            byte[] encryptedData = GetProtectedDataInternal(fileName, out byte[] entropy);

            IntPtr entropyPtr = Marshal.AllocHGlobal(entropy.Length);
            Marshal.Copy(entropy, 0, entropyPtr, entropy.Length);

            IntPtr dataPtr = Marshal.AllocHGlobal(encryptedData.Length);
            Marshal.Copy(encryptedData, 0, dataPtr, encryptedData.Length);

            try
            {
                Crypt32.DataBlob dataBlob = new Crypt32.DataBlob
                {
                    cbData = encryptedData.Length,
                    pbData = dataPtr
                };
                Crypt32.DataBlob entropyBlob = new Crypt32.DataBlob
                {
                    cbData = entropy.Length,
                    pbData = entropyPtr
                };
                Crypt32.DataBlob outBlob = new Crypt32.DataBlob();

                // Crypt
                bool success = Crypt32.CryptUnprotectData(ref dataBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, Crypt32.CryptProtectFlags.CRYPTPROTECT_LOCAL_MACHINE, ref outBlob);
                if (!success)
                {
                    int error = Marshal.GetLastWin32Error();
                    logger.Error($"CryptUnprotectData failed with error code {error}!");
                }

                // Save to file
                byte[] bytes = null;
                char[] chars = null;
                try
                {
                    SecureString secureString = new SecureString();

                    bytes = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, bytes, 0, bytes.Length);

                    chars = Encoding.UTF8.GetChars(bytes, 0, bytes.Length);
                    foreach (char c in chars)
                        secureString.AppendChar(c);

                    return secureString;
                }
                finally
                {
                    Marshal.FreeHGlobal(outBlob.pbData);

                    unsafe
                    {
                        // Zero out the byte array
                        if (bytes != null)
                        {
                            byte[] zeroB = new byte[bytes.Length];
                            fixed (byte* pb = &bytes[0])
                            {
                                Marshal.Copy(zeroB, 0, new IntPtr(pb), zeroB.Length);
                            }
                        }

                        // Zero out the char array
                        if (chars != null)
                        {
                            char[] zeroC = new char[chars.Length];
                            fixed (char* pc = &chars[0])
                            {
                                Marshal.Copy(zeroC, 0, new IntPtr(pc), zeroC.Length);
                            }
                        }
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(entropyPtr);
                Marshal.FreeHGlobal(dataPtr);
            }
        }

        internal static void SaveProtectedData(byte[] plaintext, string fileName)
        {
            // Generate entropy
            byte[] entropy = new byte[20];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(entropy);
            }
            byte[] ciphertext = ProtectedData.Protect(plaintext, entropy, DataProtectionScope.CurrentUser);

            // Write to file
            SaveProtectedDataInternal(ciphertext, entropy, fileName);
        }

        internal static void SaveProtectedData(SecureString secureString, string fileName)
        {
            // Generate entropy
            byte[] entropy = new byte[20];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(entropy);
            }
            IntPtr entropyPtr = Marshal.AllocHGlobal(entropy.Length);
            Marshal.Copy(entropy, 0, entropyPtr, entropy.Length);

            // Get a BSTR from the SecureString
            IntPtr unmanagedString = Marshal.SecureStringToBSTR(secureString);

            try
            {
                Crypt32.DataBlob dataBlob = new Crypt32.DataBlob
                {
                    cbData = secureString.Length,
                    pbData = unmanagedString
                };
                Crypt32.DataBlob entropyBlob = new Crypt32.DataBlob
                {
                    cbData = entropy.Length,
                    pbData = entropyPtr
                };
                Crypt32.DataBlob outBlob = new Crypt32.DataBlob();

                // Crypt
                bool success = Crypt32.CryptProtectData(ref dataBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, Crypt32.CryptProtectFlags.CRYPTPROTECT_LOCAL_MACHINE, ref outBlob);
                if (!success)
                {
                    int error = Marshal.GetLastWin32Error();
                    logger.Error($"CryptProtectData failed with error code {error}!");
                }

                // Save to file
                try
                {
                    byte[] cryptedData = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, cryptedData, 0, cryptedData.Length);

                    SaveProtectedDataInternal(cryptedData, entropy, fileName);
                }
                finally
                {
                    Marshal.FreeHGlobal(outBlob.pbData);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(entropyPtr);
                Marshal.ZeroFreeBSTR(unmanagedString);
            }
        }

        #region Internal

        private static byte[] GetProtectedDataInternal(string fileName, out byte[] entropy)
        {
            string secFilePath = Path.Combine(App.LocalApplicationData, fileName);
            if (!File.Exists(secFilePath))
            {
                entropy = null;
                return null;
            }

            ProtectedText pt;
            using (FileStream fs = new FileStream(secFilePath, FileMode.Open, FileAccess.Read))
            {
                BinaryFormatter binaryFormatter = new BinaryFormatter();
                pt = (ProtectedText)binaryFormatter.Deserialize(fs);
            }

            entropy = pt.entropy;
            return pt.ciphertext;
        }

        private static void SaveProtectedDataInternal(byte[] cryptedData, byte[] entropy, string fileName)
        {
            string secFilePath = Path.Combine(App.LocalApplicationData, fileName);

            // Write to file
            using (FileStream fs = new FileStream(secFilePath, FileMode.Create, FileAccess.Write))
            {
                BinaryFormatter binaryFormatter = new BinaryFormatter();
                binaryFormatter.Serialize(fs, new ProtectedText { entropy = entropy, ciphertext = cryptedData });
            }

            // Only allow access to the file to the current user
            var acl = File.GetAccessControl(secFilePath);
            acl.AddAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().Name,
                FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Delete,
                InheritanceFlags.None,
                PropagationFlags.NoPropagateInherit,
                AccessControlType.Allow));
            acl.SetAccessRuleProtection(true, false);
            File.SetAccessControl(secFilePath, acl);
        }

        #endregion Internal

        [Serializable]
        private class ProtectedText
        {
            public byte[] entropy;
            public byte[] ciphertext;
        }
    }
}