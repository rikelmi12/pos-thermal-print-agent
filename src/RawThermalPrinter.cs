using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace CardapioPrintAgent;

internal static class RawThermalPrinter
{
    private const int ThermalDotsPerMillimeter = 8;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class DocInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string DocumentName = string.Empty;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? OutputFile = null;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string DataType = "RAW";
    }

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string printerName, out IntPtr printerHandle, IntPtr defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr printerHandle);

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartDocPrinter(IntPtr printerHandle, int level, [In] DocInfo docInfo);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr printerHandle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr printerHandle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr printerHandle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr printerHandle, byte[] data, int count, out int written);

    public static void PrintText(string text, string printerName, int paperWidthMm, int feedAfterPrintMm)
    {
        var selectedPrinter = (printerName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(selectedPrinter))
        {
            throw new InvalidOperationException("Impressora termica nao selecionada para impressao RAW.");
        }

        var data = BuildEscPosPayload(text, paperWidthMm, feedAfterPrintMm);
        SendBytes(selectedPrinter, data);
    }

    private static byte[] BuildEscPosPayload(string text, int paperWidthMm, int feedAfterPrintMm)
    {
        var printableWidthDots = GetPrintableWidthDots(paperWidthMm);
        var widthLow = (byte)(printableWidthDots % 256);
        var widthHigh = (byte)(printableWidthDots / 256);
        var asciiText = ToPrinterAscii(text)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .TrimEnd();

        var output = new List<byte>(asciiText.Length + 64)
        {
            0x1B, 0x40,             // ESC @: inicializa
            0x1B, 0x61, 0x00,       // ESC a 0: alinhado a esquerda
            0x1B, 0x20, 0x00,       // ESC SP 0: espaco entre caracteres
            0x1D, 0x4C, 0x00, 0x00, // GS L 0: margem esquerda zero
            0x1D, 0x57, widthLow, widthHigh, // GS W: largura imprimivel
        };

        output.AddRange(Encoding.ASCII.GetBytes(asciiText));
        output.Add(0x0A);
        AppendFeedAfterPrint(output, feedAfterPrintMm);
        return output.ToArray();
    }

    private static int GetPrintableWidthDots(int paperWidthMm)
    {
        var safePaperWidthMm = Math.Clamp(paperWidthMm, 40, 120);
        var contentWidthMm = safePaperWidthMm <= 60
            ? Math.Max(35, safePaperWidthMm - 10)
            : Math.Max(35, safePaperWidthMm - 8);
        return Math.Clamp((int)Math.Round((double)(contentWidthMm * ThermalDotsPerMillimeter)), 280, 832);
    }

    private static void AppendFeedAfterPrint(List<byte> output, int feedAfterPrintMm)
    {
        var remainingDots = Math.Clamp(feedAfterPrintMm, 0, 80) * ThermalDotsPerMillimeter;
        while (remainingDots > 0)
        {
            var chunk = Math.Min(remainingDots, 255);
            output.Add(0x1B);
            output.Add(0x4A);
            output.Add((byte)chunk);
            remainingDots -= chunk;
        }
    }

    private static string ToPrinterAscii(string text)
    {
        var normalized = (text ?? string.Empty).Normalize(NormalizationForm.FormD);
        var output = new StringBuilder(normalized.Length);

        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark) continue;

            output.Append(ch switch
            {
                'ç' or 'Ç' => 'c',
                'ñ' or 'Ñ' => 'n',
                '–' or '—' => '-',
                '“' or '”' => '"',
                '‘' or '’' => '\'',
                '\t' => ' ',
                _ when ch >= 32 && ch <= 126 => ch,
                '\n' => '\n',
                _ => ' ',
            });
        }

        return output.ToString().Normalize(NormalizationForm.FormC);
    }

    private static void SendBytes(string printerName, byte[] data)
    {
        if (!OpenPrinter(printerName, out var handle, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Nao foi possivel abrir a impressora: {printerName}");
        }

        try
        {
            var docInfo = new DocInfo
            {
                DocumentName = $"Comanda-{DateTime.Now:yyyyMMdd-HHmmss}",
                DataType = "RAW",
            };

            if (!StartDocPrinter(handle, 1, docInfo))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Nao foi possivel iniciar documento RAW.");
            }

            try
            {
                if (!StartPagePrinter(handle))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Nao foi possivel iniciar pagina RAW.");
                }

                try
                {
                    if (!WritePrinter(handle, data, data.Length, out var written) || written != data.Length)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Nao foi possivel enviar todos os bytes para a impressora.");
                    }
                }
                finally
                {
                    EndPagePrinter(handle);
                }
            }
            finally
            {
                EndDocPrinter(handle);
            }
        }
        finally
        {
            ClosePrinter(handle);
        }
    }
}
