using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace WorkTiles.Services;

/// <summary>
/// 用当前 Windows 用户的 DPAPI 保护本机凭据。换用户或换机器后无法解密，调用方按“需要重新配对”处理；
/// 已经是 dpapi: 前缀的值原样返回，重复保存不会二次加密。
/// </summary>
internal static class SecretProtector
{
    private const string Prefix = "dpapi:";
    private const int UiForbidden = 1;

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value) || value.StartsWith(Prefix, StringComparison.Ordinal)) return value;
        // 应用只发布 Windows 版；其他平台运行测试时不加密，也不改变数据。
        if (!OperatingSystem.IsWindows()) return value;
        byte[] clear = Encoding.UTF8.GetBytes(value);
        IntPtr input = Marshal.AllocHGlobal(clear.Length);
        try
        {
            Marshal.Copy(clear, 0, input, clear.Length);
            DataBlob inBlob = new() { cbData = clear.Length, pbData = input };
            if (!CryptProtectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out DataBlob outBlob))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                byte[] cipher = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, cipher, 0, cipher.Length);
                return Prefix + Convert.ToBase64String(cipher);
            }
            finally { LocalFree(outBlob.pbData); }
        }
        finally { Marshal.FreeHGlobal(input); }
    }

    public static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal)) return value;
        if (!OperatingSystem.IsWindows()) return "";
        try
        {
            byte[] cipher = Convert.FromBase64String(value[Prefix.Length..]);
            IntPtr input = Marshal.AllocHGlobal(cipher.Length);
            try
            {
                Marshal.Copy(cipher, 0, input, cipher.Length);
                DataBlob inBlob = new() { cbData = cipher.Length, pbData = input };
                if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out DataBlob outBlob))
                    return "";
                try
                {
                    byte[] clear = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, clear, 0, clear.Length);
                    return Encoding.UTF8.GetString(clear);
                }
                finally { LocalFree(outBlob.pbData); }
            }
            finally { Marshal.FreeHGlobal(input); }
        }
        catch (FormatException) { return ""; }
    }

    public static void ProtectSettings(BoardSettingsState settings)
    {
        settings.CloudSync.DeviceToken = Protect(settings.CloudSync.DeviceToken);
        settings.Lock.TotpSecret = Protect(settings.Lock.TotpSecret);
    }

    public static void UnprotectSettings(BoardSettingsState settings)
    {
        settings.CloudSync.DeviceToken = Unprotect(settings.CloudSync.DeviceToken);
        settings.Lock.TotpSecret = Unprotect(settings.Lock.TotpSecret);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
