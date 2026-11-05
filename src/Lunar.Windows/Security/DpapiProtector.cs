using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Lunar.Windows.Security;

/// <summary>
/// Windows DPAPI for the current user (<c>CryptProtectData</c>): the result can only be decrypted by the same Windows user on the same PC. A fixed
/// application-specific entropy is mixed in, so another program of the same user does not decrypt it by accident, and the call is told never to show
/// a prompt. No package is needed: it is the operating system's own function.
/// </summary>
internal sealed class DpapiProtector : ISecretProtector
{
    private const int UiForbidden = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Lunar.UsageToken.v1");

    public byte[] Protect(byte[] plain)
    {
        ArgumentNullException.ThrowIfNull(plain);
        return Run(plain, protect: true);
    }

    public byte[] Unprotect(byte[] protectedBytes)
    {
        ArgumentNullException.ThrowIfNull(protectedBytes);
        return Run(protectedBytes, protect: false);
    }

    private static byte[] Run(byte[] input, bool protect)
    {
        var inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var output = new DataBlob();
        try
        {
            var inBlob = new DataBlob { Size = input.Length, Data = inHandle.AddrOfPinnedObject() };
            var entropyBlob = new DataBlob { Size = Entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output)
                : CryptUnprotectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output);
            if (!ok)
                throw new CryptographicException(new Win32Exception(Marshal.GetLastWin32Error()).Message);

            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                // The operating system allocated this buffer; wipe it before giving it back.
                var zero = new byte[output.Size];
                Marshal.Copy(zero, 0, output.Data, output.Size);
                LocalFree(output.Data);
            }

            inHandle.Free();
            entropyHandle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
