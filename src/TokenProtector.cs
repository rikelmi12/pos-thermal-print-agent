using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace CardapioPrintAgent;

internal static class TokenProtector
{
    private const string Prefix = "dpapi:v1:";
    private const uint CryptProtectUiForbidden = 0x1;

    public static string Protect(string? value)
    {
        var plainText = (value ?? string.Empty).Trim();
        if (plainText.Length == 0) return string.Empty;

        var input = Encoding.UTF8.GetBytes(plainText);
        var inputBlob = CreateBlob(input);
        try
        {
            if (!CryptProtectData(
                    ref inputBlob,
                    "Cardapio Print Agent credential",
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out var outputBlob))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Nao foi possivel proteger a credencial no Windows.");
            }

            try
            {
                var encrypted = CopyBlob(outputBlob);
                return Prefix + Convert.ToBase64String(encrypted);
            }
            finally
            {
                if (outputBlob.Data != IntPtr.Zero)
                {
                    Marshal.Copy(new byte[outputBlob.Size], 0, outputBlob.Data, outputBlob.Size);
                    LocalFree(outputBlob.Data);
                }
            }
        }
        finally
        {
            if (inputBlob.Data != IntPtr.Zero)
            {
                Marshal.Copy(new byte[input.Length], 0, inputBlob.Data, input.Length);
                Marshal.FreeHGlobal(inputBlob.Data);
            }
            Array.Clear(input);
        }
    }

    public static string Unprotect(string? protectedValue)
    {
        var encoded = (protectedValue ?? string.Empty).Trim();
        if (encoded.Length == 0) return string.Empty;
        if (!encoded.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Formato da credencial protegida desconhecido.");
        }

        byte[] encrypted;
        try
        {
            encrypted = Convert.FromBase64String(encoded[Prefix.Length..]);
        }
        catch (FormatException error)
        {
            throw new InvalidOperationException("Credencial protegida corrompida.", error);
        }

        var inputBlob = CreateBlob(encrypted);
        try
        {
            if (!CryptUnprotectData(
                    ref inputBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out var outputBlob))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "A credencial pertence a outro usuario do Windows ou esta corrompida.");
            }

            try
            {
                var plainBytes = CopyBlob(outputBlob);
                try
                {
                    return Encoding.UTF8.GetString(plainBytes).Trim();
                }
                finally
                {
                    Array.Clear(plainBytes);
                }
            }
            finally
            {
                if (outputBlob.Data != IntPtr.Zero)
                {
                    Marshal.Copy(new byte[outputBlob.Size], 0, outputBlob.Data, outputBlob.Size);
                    LocalFree(outputBlob.Data);
                }
            }
        }
        finally
        {
            if (inputBlob.Data != IntPtr.Zero) Marshal.FreeHGlobal(inputBlob.Data);
            Array.Clear(encrypted);
        }
    }

    private static DataBlob CreateBlob(byte[] data)
    {
        if (data.Length == 0) return new DataBlob();
        var pointer = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, pointer, data.Length);
        return new DataBlob { Size = data.Length, Data = pointer };
    }

    private static byte[] CopyBlob(DataBlob blob)
    {
        if (blob.Size <= 0 || blob.Data == IntPtr.Zero) return [];
        var output = new byte[blob.Size];
        Marshal.Copy(blob.Data, output, 0, blob.Size);
        return output;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
