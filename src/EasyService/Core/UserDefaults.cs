using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace EasyService.Core;

/// <summary>
/// Remembers what the administrator picked last time, so the second service is faster to
/// set up than the first. Lives in HKCU, not HKLM: these are personal conveniences, and the
/// stored credential must not become readable for every user of the machine.
/// </summary>
public static class UserDefaults
{
    private const string KeyPath = @"Software\EasyService";

    /// <summary>Kept in memory for the lifetime of the process even when nothing is persisted.</summary>
    private static string? _sessionPassword;

    private static RegistryKey Open() => Registry.CurrentUser.CreateSubKey(KeyPath, true)!;

    private static string GetString(string name, string fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, false);
            return key?.GetValue(name) as string ?? fallback;
        }
        catch { return fallback; }
    }

    private static int GetInt(string name, int fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, false);
            return key?.GetValue(name) is int i ? i : fallback;
        }
        catch { return fallback; }
    }

    private static void Set(string name, object value, RegistryValueKind kind)
    {
        try
        {
            using var key = Open();
            key.SetValue(name, value, kind);
        }
        catch { /* defaults are a convenience, never a hard failure */ }
    }

    private static void Remove(string name)
    {
        try
        {
            using var key = Open();
            key.DeleteValue(name, throwOnMissingValue: false);
        }
        catch { }
    }

    // ------------------------------------------------------------- defaults ---

    public static string LogDirectory
    {
        get
        {
            var stored = GetString("LogDirectory", "");
            return string.IsNullOrWhiteSpace(stored) ? ServiceConfig.DefaultLogDirectory : stored;
        }
        set => Set("LogDirectory", value ?? "", RegistryValueKind.String);
    }

    public static LogonType LastLogon
    {
        get => (LogonType)GetInt("LastLogon", (int)LogonType.LocalSystem);
        set => Set("LastLogon", (int)value, RegistryValueKind.DWord);
    }

    public static string LastAccountName
    {
        get => GetString("LastAccountName", "");
        set => Set("LastAccountName", value ?? "", RegistryValueKind.String);
    }

    public static StartupType LastStartup
    {
        get => (StartupType)GetInt("LastStartup", (int)StartupType.Automatic);
        set => Set("LastStartup", (int)value, RegistryValueKind.DWord);
    }

    public static bool RememberPassword
    {
        get => GetInt("RememberPassword", 0) != 0;
        set => Set("RememberPassword", value ? 1 : 0, RegistryValueKind.DWord);
    }

    // ------------------------------------------------------------- password ---

    /// <summary>
    /// Returns the remembered service account password: from this session if one was typed,
    /// otherwise from the DPAPI blob - which only this Windows user on this machine can decrypt.
    /// </summary>
    public static string? GetPassword()
    {
        if (_sessionPassword is not null) return _sessionPassword;
        if (!RememberPassword) return null;

        var encoded = GetString("Credential", "");
        if (encoded.Length == 0) return null;
        try
        {
            var protectedBytes = Convert.FromBase64String(encoded);
            var plain = Dpapi.Unprotect(protectedBytes);
            return plain is null ? null : Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    public static void SetPassword(string? password, bool persist)
    {
        _sessionPassword = string.IsNullOrEmpty(password) ? null : password;

        if (!persist || string.IsNullOrEmpty(password))
        {
            RememberPassword = false;
            Remove("Credential");
            return;
        }

        try
        {
            var protectedBytes = Dpapi.Protect(Encoding.UTF8.GetBytes(password));
            if (protectedBytes is null) return;
            Set("Credential", Convert.ToBase64String(protectedBytes), RegistryValueKind.String);
            RememberPassword = true;
        }
        catch
        {
            RememberPassword = false;
        }
    }

    public static void ForgetPassword()
    {
        _sessionPassword = null;
        RememberPassword = false;
        Remove("Credential");
    }

    /// <summary>Called after a service was created, so the next one starts from these values.</summary>
    public static void RememberFrom(ServiceConfig config, bool persistPassword)
    {
        LastLogon = config.Logon;
        LastStartup = config.Startup;
        if (config.Logon == LogonType.Account)
        {
            LastAccountName = config.AccountName;
            SetPassword(config.Password, persistPassword);
        }

        var directory = Path.GetDirectoryName(config.StdoutPath);
        if (!string.IsNullOrWhiteSpace(directory)) LogDirectory = directory;
    }
}

/// <summary>
/// Windows data protection, user scope. P/Invoked rather than pulled in as a NuGet package,
/// in keeping with the rest of the project.
/// </summary>
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DATA_BLOB input, string? description, ref DATA_BLOB entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB output);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB input, IntPtr description, ref DATA_BLOB entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    // Ties the blob to this application; a copy pasted elsewhere is useless without it.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EasyService/service-account/v1");

    public static byte[]? Protect(byte[] plain) => Transform(plain, protect: true);

    public static byte[]? Unprotect(byte[] encrypted) => Transform(encrypted, protect: false);

    private static byte[]? Transform(byte[] data, bool protect)
    {
        var inputHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var output = default(DATA_BLOB);
        try
        {
            var input = new DATA_BLOB { cbData = data.Length, pbData = inputHandle.AddrOfPinnedObject() };
            var entropy = new DATA_BLOB { cbData = Entropy.Length, pbData = entropyHandle.AddrOfPinnedObject() };

            var ok = protect
                ? CryptProtectData(ref input, "EasyService", ref entropy, IntPtr.Zero, IntPtr.Zero, 0, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 0, out output);

            if (!ok || output.pbData == IntPtr.Zero) return null;

            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
            entropyHandle.Free();
            inputHandle.Free();
        }
    }
}
