using System.Drawing;
using System.Drawing.Printing;
using System.Drawing.Text;
using System.Globalization;
using System.Text;

namespace CardapioPrintAgent;

internal static class NativePrinter
{
    private const float ReceiptFontSizePt = 8.5f;
    private const float LeftMarginMm = 2.0f;
    private const float TopMarginMm = 2.0f;
    private const float RightMarginMm = 2.0f;
    private const float BottomMarginMm = 4.0f;
    private const float ThermalPaperWidthMm = 58.0f;
    private const float Thermal58ContentWidthMm = 50.0f;
    private const float Thermal80ContentWidthMm = 72.0f;
    private const float A4PaperWidthMm = 210.0f;
    private const float A4ContentWidthMm = 185.0f;
    private const float A4PaperHeightMm = 297.0f;
    private const float ThermalPaperHeightMm = 300.0f;

    public static IReadOnlyList<string> ListPrinters()
    {
        var printers = new List<string>();
        foreach (var item in PrinterSettings.InstalledPrinters)
        {
            printers.Add(item?.ToString() ?? string.Empty);
        }
        return printers
            .Where((p) => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy((p) => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static int? TryDetectThermalPaperWidthMm(string printerName)
    {
        try
        {
            using var document = new PrintDocument();
            var selectedPrinter = (printerName ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(selectedPrinter))
            {
                document.PrinterSettings.PrinterName = selectedPrinter;
            }

            if (!document.PrinterSettings.IsValid) return null;

            var paperWidth = document.DefaultPageSettings?.PaperSize?.Width ?? 0;
            if (paperWidth <= 0) return null;

            var widthMm = (int)Math.Round(paperWidth * 25.4 / 100.0);
            return widthMm is >= 40 and <= 120 ? widthMm : null;
        }
        catch
        {
            return null;
        }
    }

    public static async Task PrintTextAsync(
        string text,
        string printerName,
        int timeoutMs,
        string printMode = "thermal",
        int paperWidthMm = 58,
        int feedAfterPrintMm = 15,
        CancellationToken cancellationToken = default)
    {
        var payload = NormalizeText(text);
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new InvalidOperationException("Job sem texto para imprimir.");
        }

        var printTask = Task.Run(
            () => PrintTextInternal(payload, printerName, printMode, paperWidthMm, feedAfterPrintMm),
            cancellationToken);
        var timeoutTask = Task.Delay(timeoutMs, cancellationToken);
        var completed = await Task.WhenAny(printTask, timeoutTask);

        if (completed == timeoutTask)
        {
            throw new TimeoutException($"Tempo excedido ao imprimir ({timeoutMs}ms).");
        }

        await printTask;
    }

    private static void PrintTextInternal(
        string text,
        string printerName,
        string printMode,
        int paperWidthMm,
        int feedAfterPrintMm)
    {
        var payload = NormalizeText(text);
        var sourceLines = payload.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var isA4 = IsA4Mode(printMode, paperWidthMm);

        using var document = new PrintDocument();
        document.DocumentName = $"Comanda-{DateTime.Now:yyyyMMdd-HHmmss}";
        document.PrintController = new StandardPrintController();
        document.OriginAtMargins = false;

        var selectedPrinter = (printerName ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(selectedPrinter))
        {
            document.PrinterSettings.PrinterName = selectedPrinter;
        }

        if (!document.PrinterSettings.IsValid)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(selectedPrinter)
                    ? "Impressora padrao do Windows indisponivel."
                    : $"Impressora nao encontrada: {selectedPrinter}");
        }

        var resolvedPrinterName = document.PrinterSettings.PrinterName;
        if (!isA4)
        {
            RawThermalPrinter.PrintText(payload, resolvedPrinterName, paperWidthMm, feedAfterPrintMm);
            return;
        }

        document.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        TryApplyPaperSize(document, printMode, paperWidthMm);

        List<string>? printableLines = null;
        var currentLine = 0;

        using var font = CreateReceiptFont(isA4);
        using var stringFormat = (StringFormat)StringFormat.GenericTypographic.Clone();
        stringFormat.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;

        document.PrintPage += (_, args) =>
        {
            var graphics = args.Graphics;
            if (graphics is null)
            {
                args.HasMorePages = false;
                return;
            }

            graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;

            var leftMargin = MillimetersToHundredthsInch(LeftMarginMm);
            var rightMargin = MillimetersToHundredthsInch(RightMarginMm);
            var printableLeft = GetPrintableLeft(args, printMode, paperWidthMm);
            var printableTop = GetPrintableTop(args);
            var printableWidth = GetPrintableWidth(args, printMode, paperWidthMm);
            var pageHeight = GetPageHeight(args);
            var usableWidth = Math.Max(MillimetersToHundredthsInch(35), printableWidth - leftMargin - rightMargin);
            var maxWidth = Math.Min(GetTargetContentWidth(printMode, paperWidthMm), usableWidth);
            var maxY = Math.Max(
                MillimetersToHundredthsInch(50),
                pageHeight - printableTop - MillimetersToHundredthsInch(BottomMarginMm));
            var lineHeight = font.GetHeight(graphics) + 1.0f;

            printableLines ??= BuildPrintableLines(graphics, font, stringFormat, sourceLines, maxWidth);
            var contentWidth = MeasureWidestLine(graphics, font, stringFormat, printableLines, maxWidth);
            var x = GetContentLeft(args, printMode, paperWidthMm, printableLeft, leftMargin, usableWidth, contentWidth);
            var y = printableTop + MillimetersToHundredthsInch(TopMarginMm);

            while (currentLine < printableLines.Count)
            {
                var line = printableLines[currentLine];
                if (!string.IsNullOrWhiteSpace(line))
                {
                    graphics.DrawString(line, font, Brushes.Black, x, y, stringFormat);
                }

                currentLine += 1;
                y += lineHeight;

                if (y + lineHeight > maxY)
                {
                    args.HasMorePages = currentLine < printableLines.Count;
                    return;
                }
            }

            args.HasMorePages = false;
        };

        document.Print();
    }

    private static Font CreateReceiptFont(bool isA4)
    {
        var fontSize = isA4 ? 10.0f : ReceiptFontSizePt;
        try
        {
            return new Font("Consolas", fontSize, FontStyle.Regular, GraphicsUnit.Point);
        }
        catch
        {
            return new Font(FontFamily.GenericMonospace, fontSize, FontStyle.Regular, GraphicsUnit.Point);
        }
    }

    private static void TryApplyPaperSize(PrintDocument document, string printMode, int paperWidthMm)
    {
        try
        {
            var current = document.DefaultPageSettings.PaperSize;
            var isA4 = IsA4Mode(printMode, paperWidthMm);
            var safePaperWidthMm = isA4 ? A4PaperWidthMm : paperWidthMm >= 75 ? 80.0f : ThermalPaperWidthMm;
            var safePaperHeightMm = isA4 ? A4PaperHeightMm : ThermalPaperHeightMm;
            var thermalWidth = MillimetersToHundredthsInch(safePaperWidthMm);
            var thermalHeight = MillimetersToHundredthsInch(safePaperHeightMm);
            var minDriverWidth = MillimetersToHundredthsInch(isA4 ? 140.0f : 1.0f);
            var maxDriverWidth = MillimetersToHundredthsInch(isA4 ? 230.0f : 90.0f);
            if (current is null || current.Width < minDriverWidth || current.Width > maxDriverWidth)
            {
                var paperName = isA4 ? "A4" : $"Thermal {(int)safePaperWidthMm}mm";
                document.DefaultPageSettings.PaperSize = new PaperSize(paperName, thermalWidth, thermalHeight);
            }
        }
        catch
        {
            // Alguns drivers ignoram tamanho customizado; margens e quebra de linha ainda mantem o texto alinhado.
        }
    }

    private static int MillimetersToHundredthsInch(float millimeters)
    {
        return (int)MathF.Round(millimeters / 25.4f * 100f);
    }

    private static float GetPageWidth(PrintPageEventArgs args)
    {
        var pageWidth = args.PageBounds.Width > 0
            ? args.PageBounds.Width
            : args.PageSettings.PaperSize.Width;
        var printableWidth = args.PageSettings.PrintableArea.Width > 0
            ? args.PageSettings.PrintableArea.Width
            : pageWidth;
        return Math.Max(1f, Math.Min(pageWidth, printableWidth));
    }

    private static float GetContentLeft(
        PrintPageEventArgs args,
        string printMode,
        int paperWidthMm,
        float printableLeft,
        float leftMargin,
        float usableWidth,
        float contentWidth)
    {
        if (!IsA4Mode(printMode, paperWidthMm))
        {
            return leftMargin;
        }

        return printableLeft + leftMargin + Math.Max(0f, (usableWidth - contentWidth) / 2f);
    }

    private static float GetPrintableLeft(PrintPageEventArgs args, string printMode, int paperWidthMm)
    {
        if (!IsA4Mode(printMode, paperWidthMm)) return 0f;

        var left = args.PageSettings.PrintableArea.Left;
        return left > 0 && left < MillimetersToHundredthsInch(20) ? left : 0f;
    }

    private static float GetPrintableTop(PrintPageEventArgs args)
    {
        var top = args.PageSettings.PrintableArea.Top;
        return top > 0 && top < MillimetersToHundredthsInch(20) ? top : 0f;
    }

    private static float GetPrintableWidth(PrintPageEventArgs args, string printMode, int paperWidthMm)
    {
        var isA4 = IsA4Mode(printMode, paperWidthMm);
        var selectedPaperWidth = MillimetersToHundredthsInch(isA4 ? A4PaperWidthMm : paperWidthMm >= 75 ? 80f : ThermalPaperWidthMm);
        var pageWidth = args.PageBounds.Width > 0
            ? args.PageBounds.Width
            : args.PageSettings.PaperSize.Width;
        var printableWidth = args.PageSettings.PrintableArea.Width > 0
            ? args.PageSettings.PrintableArea.Width
            : pageWidth;

        var maxDriverWidth = MillimetersToHundredthsInch(isA4 ? 260f : 100f);
        if (pageWidth <= 0 || pageWidth > maxDriverWidth)
        {
            return selectedPaperWidth;
        }

        return Math.Max(1f, Math.Min(Math.Min(pageWidth, printableWidth), selectedPaperWidth));
    }

    private static float GetTargetContentWidth(string printMode, int paperWidthMm)
    {
        if (IsA4Mode(printMode, paperWidthMm)) return MillimetersToHundredthsInch(A4ContentWidthMm);
        return MillimetersToHundredthsInch(paperWidthMm >= 75 ? Thermal80ContentWidthMm : Thermal58ContentWidthMm);
    }

    private static bool IsA4Mode(string printMode, int paperWidthMm)
    {
        return string.Equals((printMode ?? string.Empty).Trim(), "a4", StringComparison.OrdinalIgnoreCase)
            || paperWidthMm >= 180;
    }

    private static float GetPageHeight(PrintPageEventArgs args)
    {
        var pageHeight = args.PageBounds.Height > 0
            ? args.PageBounds.Height
            : args.PageSettings.PaperSize.Height;
        var printableHeight = args.PageSettings.PrintableArea.Height > 0
            ? args.PageSettings.PrintableArea.Height
            : pageHeight;
        return Math.Max(1f, Math.Min(pageHeight, printableHeight));
    }

    private static List<string> BuildPrintableLines(
        Graphics graphics,
        Font font,
        StringFormat stringFormat,
        IEnumerable<string> sourceLines,
        float maxWidth)
    {
        var printable = new List<string>();
        foreach (var sourceLine in sourceLines)
        {
            printable.AddRange(WrapLineToWidth(graphics, font, stringFormat, sourceLine, maxWidth));
        }
        return printable;
    }

    private static float MeasureWidestLine(
        Graphics graphics,
        Font font,
        StringFormat stringFormat,
        IReadOnlyCollection<string> lines,
        float fallbackWidth)
    {
        var widest = 0f;
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line)) continue;
            widest = Math.Max(widest, graphics.MeasureString(line, font, int.MaxValue, stringFormat).Width);
        }
        if (widest <= 0) return fallbackWidth;
        return Math.Min(widest, fallbackWidth);
    }

    private static IEnumerable<string> WrapLineToWidth(
        Graphics graphics,
        Font font,
        StringFormat stringFormat,
        string sourceLine,
        float maxWidth)
    {
        var line = sourceLine ?? string.Empty;
        if (line.Length == 0 || FitsLine(graphics, font, stringFormat, line, maxWidth))
        {
            yield return line;
            yield break;
        }

        var indentLength = line.TakeWhile((ch) => ch == ' ').Count();
        var indent = indentLength > 0 ? new string(' ', Math.Min(indentLength, 6)) : string.Empty;
        var words = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = string.Empty;

        foreach (var word in words)
        {
            var candidate = string.IsNullOrEmpty(current) ? word : $"{current} {word}";
            var measuredCandidate = $"{indent}{candidate}";
            if (FitsLine(graphics, font, stringFormat, measuredCandidate, maxWidth))
            {
                current = candidate;
                continue;
            }

            if (!string.IsNullOrEmpty(current))
            {
                yield return $"{indent}{current}";
                current = string.Empty;
            }

            if (FitsLine(graphics, font, stringFormat, $"{indent}{word}", maxWidth))
            {
                current = word;
                continue;
            }

            foreach (var part in SplitLongWord(graphics, font, stringFormat, word, indent, maxWidth))
            {
                yield return part;
            }
        }

        if (!string.IsNullOrEmpty(current))
        {
            yield return $"{indent}{current}";
        }
    }

    private static IEnumerable<string> SplitLongWord(
        Graphics graphics,
        Font font,
        StringFormat stringFormat,
        string word,
        string indent,
        float maxWidth)
    {
        var current = string.Empty;
        foreach (var rune in word.EnumerateRunes())
        {
            var next = current + rune;
            if (FitsLine(graphics, font, stringFormat, $"{indent}{next}", maxWidth) || string.IsNullOrEmpty(current))
            {
                current = next;
                continue;
            }

            yield return $"{indent}{current}";
            current = rune.ToString();
        }

        if (!string.IsNullOrEmpty(current))
        {
            yield return $"{indent}{current}";
        }
    }

    private static bool FitsLine(Graphics graphics, Font font, StringFormat stringFormat, string line, float maxWidth)
    {
        var measured = graphics.MeasureString(line, font, int.MaxValue, stringFormat);
        return measured.Width <= maxWidth;
    }

    private static string NormalizeText(string text)
    {
        var normalized = (text ?? string.Empty)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Trim();

        var output = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (ShouldSkipForThermalPrinter(rune))
            {
                continue;
            }

            output.Append(rune.ToString());
        }

        return output
            .ToString()
            .Replace("*", string.Empty)
            .Replace("`", string.Empty)
            .Replace("~", string.Empty)
            .Trim();
    }

    private static bool ShouldSkipForThermalPrinter(Rune rune)
    {
        if (rune.Value == '\n' || rune.Value == '\t') return false;
        if (rune.Value < 32 || rune.Value == 127) return true;
        if (rune.Value == 0x200D || rune.Value == 0xFE0F) return true;
        if (rune.Value >= 0x1F000 && rune.Value <= 0x1FAFF) return true;
        if (rune.Value >= 0x1F1E6 && rune.Value <= 0x1F1FF) return true;
        if (rune.Value >= 0x1F3FB && rune.Value <= 0x1F3FF) return true;

        var category = Rune.GetUnicodeCategory(rune);
        return category == UnicodeCategory.OtherSymbol && rune.Value >= 0x2600;
    }
}
