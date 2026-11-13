using System.Runtime.InteropServices;
using System.Text;

namespace StreamLightControl2.Core.Services;

/// <summary>
/// Versleutelt de BetterTok API-key met Windows DPAPI (CurrentUser). Het versleutelde resultaat
/// staat in settings.json en is alleen te ontsleutelen door jouw Windows-account op deze pc.
/// Geen extra NuGet-pakket nodig.
/// </summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("StreamLightControl.BetterTok.v3");

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        if (!OperatingSystem.IsWindows()) return "plain:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plain)); // alleen voor tests
        var data = Encoding.UTF8.GetBytes(plain);
        return "dpapi:" + Convert.ToBase64String(Crypt(data, protect: true));
    }

    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try
        {
            if (stored.StartsWith("plain:")) return Encoding.UTF8.GetString(Convert.FromBase64String(stored[6..]));
            if (stored.StartsWith("dpapi:") && OperatingSystem.IsWindows())
                return Encoding.UTF8.GetString(Crypt(Convert.FromBase64String(stored[6..]), protect: false));
        }
        catch { }
        return "";
    }

    public static string Mask(string key) =>
        string.IsNullOrEmpty(key) ? "" : key.Length <= 14 ? new string('•', key.Length) : key[..9] + new string('•', 8) + key[^4..];

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DataBlob { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, string? szDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr hMem);

    private static byte[] Crypt(byte[] input, bool protect)
    {
        var inBlob = ToBlob(input);
        var entropy = ToBlob(Entropy);
        var outBlob = new DataBlob();
        try
        {
            const int UiForbidden = 0x1;
            var ok = protect
                ? CryptProtectData(ref inBlob, "StreamLightControl", ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref outBlob);
            if (!ok) throw new InvalidOperationException("DPAPI-fout " + Marshal.GetLastWin32Error());
            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.pbData);
            Marshal.FreeHGlobal(entropy.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    private static DataBlob ToBlob(byte[] data)
    {
        var ptr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, ptr, data.Length);
        return new DataBlob { cbData = data.Length, pbData = ptr };
    }
}
