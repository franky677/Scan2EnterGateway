using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Drawing.Text;

namespace Scan2EnterGateway;

public sealed class WindowsLabelPrinter
{
    public const string PrinterName = "Godex G500";

    private const int PaperWidthHundredthsInch = 157;
    private const int PaperHeightHundredthsInch = 51;

    private readonly SemaphoreSlim _printLock = new(1, 1);

    public async Task PrintAsync(
        byte[] bitmapBytes,
        int quantity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmapBytes);

        if (bitmapBytes.Length == 0)
        {
            throw new InvalidOperationException(
                "La bitmap dell'etichetta è vuota.");
        }

        quantity = Math.Clamp(
            quantity,
            1,
            LabelGenerator.MaxQuantity);

        await _printLock.WaitAsync(cancellationToken);

        try
        {
            await Task.Run(
                () => PrintInternal(
                    bitmapBytes,
                    quantity,
                    cancellationToken),
                cancellationToken);
        }
        finally
        {
            _printLock.Release();
        }
    }

    private static void PrintInternal(
        byte[] bitmapBytes,
        int quantity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = new MemoryStream(bitmapBytes);
        using var sourceImage = Image.FromStream(stream);
        using var image = new Bitmap(sourceImage);

        using var document = new PrintDocument();

        document.DocumentName =
            "Scan2Enter - Etichetta 40x13";

        document.PrintController =
            new StandardPrintController();

        document.PrinterSettings.PrinterName =
            PrinterName;

        if (!document.PrinterSettings.IsValid)
        {
            throw new InvalidOperationException(
                $"La stampante Windows '{PrinterName}' non è disponibile.");
        }

        document.PrinterSettings.Copies =
            checked((short)quantity);

        document.PrinterSettings.Collate = false;

        var paperSize = new PaperSize(
            "Scan2Enter 40x13 mm",
            PaperWidthHundredthsInch,
            PaperHeightHundredthsInch)
        {
            RawKind = 0
        };

        document.DefaultPageSettings.PaperSize =
            paperSize;

        document.DefaultPageSettings.Landscape =
            false;

        document.DefaultPageSettings.Margins =
            new Margins(0, 0, 0, 0);

        document.OriginAtMargins = false;

        document.PrintPage += (_, args) =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            args.Graphics.PageUnit =
                GraphicsUnit.Display;

            args.Graphics.CompositingQuality =
                CompositingQuality.HighQuality;

            args.Graphics.InterpolationMode =
                InterpolationMode.HighQualityBicubic;

            args.Graphics.PixelOffsetMode =
                PixelOffsetMode.HighQuality;

            args.Graphics.SmoothingMode =
                SmoothingMode.HighQuality;

            args.Graphics.TextRenderingHint =
                TextRenderingHint.ClearTypeGridFit;

            /*
             * Compensa l'area non stampabile dichiarata dal driver,
             * così il punto 0,0 corrisponde realmente all'angolo
             * superiore sinistro dell'etichetta.
             */
            args.Graphics.TranslateTransform(
                -args.PageSettings.HardMarginX,
                -args.PageSettings.HardMarginY);

            args.Graphics.DrawImage(
                image,
                new RectangleF(
                    0f,
                    0f,
                    PaperWidthHundredthsInch,
                    PaperHeightHundredthsInch));

            args.HasMorePages = false;
        };

        document.Print();
    }
}
