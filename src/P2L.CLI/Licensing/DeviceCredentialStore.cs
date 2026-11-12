using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace P2L.CLI.Licensing;

/// <summary>Shared native credential vault for CLI and UI enrollment under the same OS account.</summary>
internal static class DeviceCredentialStore
{
    private const string Target = "P2L.License/device";

    public static RSA Load()
    {
        string encoded;
        if (OperatingSystem.IsWindows()) encoded = ReadWindows();
        else if (OperatingSystem.IsMacOS()) encoded = ReadMac();
        else encoded = ReadLinux();
        var bytes = Convert.FromBase64String(encoded);
        try
        {
            var key = RSA.Create();
            try
            {
                key.ImportPkcs8PrivateKey(bytes, out var consumed);
                if (consumed != bytes.Length || key.KeySize != 2048) throw new CryptographicException("Invalid device key");
                return key;
            }
            catch { key.Dispose(); throw; }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static RSA LoadOrCreate()
    {
        try { return Load(); }
        catch (DeviceNotEnrolledException)
        {
            var key = RSA.Create(2048);
            try { Save(key); return key; }
            catch { key.Dispose(); throw; }
        }
    }

    private static void Save(RSA key)
    {
        var der = key.ExportPkcs8PrivateKey();
        byte[] bytes;
        try { bytes = Encoding.ASCII.GetBytes(Convert.ToBase64String(der)); }
        finally { CryptographicOperations.ZeroMemory(der); }
        try
        {
            if (OperatingSystem.IsWindows()) WriteWindows(bytes);
            else if (OperatingSystem.IsMacOS()) WriteMac(bytes);
            else WriteLinux(bytes);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void WriteWindows(byte[] bytes)
    {
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        var target = Marshal.StringToCoTaskMemUni(Target);
        var user = Marshal.StringToCoTaskMemUni("device");
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential {Type = 1, TargetName = target, UserName = user,
                Size = (uint)bytes.Length, Blob = blob, Persist = 2};
            if (!CredWrite(ref credential, 0)) throw new InvalidOperationException("Cannot save device key in Windows Credential Manager.");
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeCoTaskMem(blob); Marshal.FreeCoTaskMem(target); Marshal.FreeCoTaskMem(user);
        }
    }

    private static void WriteMac(byte[] bytes)
    {
        var service = Encoding.UTF8.GetBytes("P2L.License");
        var account = Encoding.UTF8.GetBytes("device");
        // Only create: never silently overwrite a key that another process enrolled.
        var status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)service.Length, service,
            (uint)account.Length, account, (uint)bytes.Length, bytes, IntPtr.Zero);
        if (status != 0) throw new InvalidOperationException("Cannot save device key in macOS Keychain. Check Keychain access and retry.");
    }

    private static void WriteLinux(byte[] bytes)
    {
        var info = new ProcessStartInfo("secret-tool") {RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false};
        foreach (var argument in new[] {"store", "--label=Pick2Light device", "application", "P2L.License", "account", "device"}) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Secret Service unavailable");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.StandardInput.BaseStream.Write(bytes);
        process.StandardInput.Close();
        if (!process.WaitForExit(15000)) { process.Kill(); throw new InvalidOperationException("Secret Service timeout"); }
        if (process.ExitCode != 0) throw new InvalidOperationException("Cannot save device key in Secret Service.");
    }

    private static string ReadWindows()
    {
        if (!CredRead(Target, 1, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == 1168) throw new DeviceNotEnrolledException();
            throw new InvalidOperationException("Cannot read Windows Credential Manager under this OS account.");
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            var bytes = new byte[credential.Size];
            Marshal.Copy(credential.Blob, bytes, 0, bytes.Length);
            try { return Encoding.ASCII.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(pointer); }
    }

    private static string ReadMac()
    {
        var service = Encoding.UTF8.GetBytes("P2L.License");
        var account = Encoding.UTF8.GetBytes("device");
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service,
            (uint)account.Length, account, out var length, out var data, IntPtr.Zero);
        if (status == -25300) throw new DeviceNotEnrolledException();
        if (status != 0) throw new InvalidOperationException("Cannot read macOS Keychain under this OS account.");
        try
        {
            var bytes = new byte[length]; Marshal.Copy(data, bytes, 0, bytes.Length);
            try { return Encoding.ASCII.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { SecKeychainItemFreeContent(IntPtr.Zero, data); }
    }

    private static string ReadLinux()
    {
        var info = new ProcessStartInfo("secret-tool") {RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false};
        foreach (var argument in new[] {"lookup", "application", "P2L.License", "account", "device"}) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Secret Service unavailable");
        var read = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000)) { process.Kill(); throw new InvalidOperationException("Secret Service timeout"); }
        if (process.ExitCode == 1 && string.IsNullOrWhiteSpace(error.GetAwaiter().GetResult())) throw new DeviceNotEnrolledException();
        if (process.ExitCode != 0) throw new InvalidOperationException("Cannot read Secret Service under this OS account.");
        return read.GetAwaiter().GetResult().Trim();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public IntPtr TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint Size;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes, TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr pointer);
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    [DllImport(Security)] private static extern int SecKeychainFindGenericPassword(IntPtr keychain,
        uint serviceLength, byte[] service, uint accountLength, byte[] account, out uint length, out IntPtr data, IntPtr item);
    [DllImport(Security)] private static extern int SecKeychainAddGenericPassword(IntPtr keychain,
        uint serviceLength, byte[] service, uint accountLength, byte[] account, uint length, byte[] data, IntPtr item);
    [DllImport(Security)] private static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);
}

internal sealed class DeviceNotEnrolledException : InvalidOperationException
{
    public DeviceNotEnrolledException() : base("Device not activated. Run p2l license activate under the same OS account.") { }
}
