using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Scan2EnterGateway;

public sealed class LabelBitmapRenderer
{
    private const int Width = LabelGenerator.LabelWidthDots;
    private const int Height = LabelGenerator.LabelHeightDots;

    public RenderedLabel Render(LabelRenderRequest request)
    {
        var requestedTemplate = request.Template;
        var actualTemplate = requestedTemplate;
        var imageUsed = false;

        using var canvas = new Bitmap(
            Width,
            Height,
            PixelFormat.Format24bppRgb);

        canvas.SetResolution(203, 203);

        using (var graphics = Graphics.FromImage(canvas))
        {
            ConfigureGraphics(graphics);
            graphics.Clear(Color.White);

            using var productImage = TryLoadProductImage(request.ImagePath);

            if (
                LabelGenerator.RequiresImage(requestedTemplate) &&
                productImage is null
            )
            {
                actualTemplate = requestedTemplate == LabelTemplate.ImagePrice
                    ? LabelTemplate.Price
                    : LabelTemplate.Standard;
            }

            switch (actualTemplate)
            {
                case LabelTemplate.Image:
                    imageUsed = DrawImageLayout(
                        graphics,
                        productImage!,
                        request,
                        showPrice: false);
                    break;

                case LabelTemplate.ImagePrice:
                    imageUsed = DrawImageLayout(
                        graphics,
                        productImage!,
                        request,
                        showPrice: true);
                    break;

                case LabelTemplate.Price:
                    DrawPriceLayout(
                        graphics,
                        request);
                    break;

                case LabelTemplate.Note:
                    DrawNoteLayout(
                        graphics,
                        request.Note);
                    break;

                default:
                    DrawStandardLayout(
                        graphics,
                        request,
                        showPrice: false);
                    break;
            }
        }

        using var grayscale = ConvertToGrayscale8Bit(canvas);
        using var output = new MemoryStream();

        grayscale.Save(output, ImageFormat.Bmp);

        return new RenderedLabel(
            BitmapBytes: output.ToArray(),
            RequestedTemplate: requestedTemplate,
            ActualTemplate: actualTemplate,
            ImageUsed: imageUsed);
    }

    private static void DrawStandardLayout(
        Graphics graphics,
        LabelRenderRequest request,
        bool showPrice)
    {
        var code = LabelGenerator.NormalizeArticleCode(
            request.ArticleCode);
        var description = LabelGenerator.NormalizeDescription(
            request.Description);
        var barcode = LabelGenerator.NormalizeBarcode(
            request.Barcode);

        var isColloLabel = IsColloDateTime(description);

        if (isColloLabel)
        {
            /*
             * Etichetta collo 40x13:
             * - cliente a sinistra, nella posizione già usata
             * - data/ora sulla stessa riga, sempre allineata a destra
             * - data/ora +1 punto rispetto al vecchio testo descrizione (13 -> 14)
             */
            DrawFittedText(
                graphics,
                code,
                new RectangleF(7, 0, 185, 28),
                maxSize: 24.0f,
                minSize: 18.0f,
                bold: true);

       DrawFittedText(
    graphics,
    description,
    new RectangleF(148, 0, 165, 28),
    maxSize: 18.0f,
    minSize: 15.0f,
    bold: false,
    alignRight: true,
    centeredVertically: true
);
        }
        else
        {
            DrawFittedText(
                graphics,
                code,
                new RectangleF(7, 0, 306, 28),
                maxSize: 24.0f,
                minSize: 18.0f,
                bold: true);

            DrawFittedText(
                graphics,
                description,
                new RectangleF(7, 16, 306, 24),
                maxSize: 13.0f,
                minSize: 10.0f,
                bold: false,
                maxLines: 2);
        }

        var barcodeTop = showPrice ? 31 : 32;
        var barcodeHeight = showPrice ? 37 : 42;
        var numberTop = showPrice ? 67 : 71;

        DrawEan13(
            graphics,
            barcode,
            new Rectangle(1, barcodeTop, 286, barcodeHeight),
            drawNumber: false);

        DrawFittedText(
            graphics,
            FormatEanForDisplay(barcode),
            new RectangleF(13, numberTop, 262, 32),
            maxSize: 44.0f,
            minSize: 24.0f,
            bold: true,
            centered: true);

        if (showPrice)
        {
            graphics.DrawLine(
                Pens.Black,
                8,
                85,
                312,
                85);

            DrawFittedText(
                graphics,
                LabelGenerator.FormatPrice(request.PublicPrice),
                new RectangleF(171, 84, 141, 20),
                maxSize: 12.5f,
                minSize: 9.0f,
                bold: true,
                centered: false,
                alignRight: true);
        }
    }

    private static void DrawPriceLayout(
        Graphics graphics,
        LabelRenderRequest request)
    {
        var code = LabelGenerator.NormalizeArticleCode(request.ArticleCode);
        var description = LabelGenerator.NormalizeDescription(request.Description);
        var barcode = LabelGenerator.NormalizeBarcode(request.Barcode);

        DrawFittedText(
            graphics,
            code,
            new RectangleF(7, 0, 306, 28),
            maxSize: 24.0f,
            minSize: 18.0f,
            bold: true);

        DrawFittedText(
            graphics,
            description,
            new RectangleF(7, 16, 306, 24),
            maxSize: 13.0f,
            minSize: 10.0f,
            bold: false,
            maxLines: 2);

        DrawEan13(
            graphics,
            barcode,
            new Rectangle(8, 34, 296, 32),
            false);

        DrawFittedText(
            graphics,
            FormatEanForDisplay(barcode),
            new RectangleF(8, 65, 202, 37),
            maxSize: 36.0f,
            minSize: 22.0f,
            bold: true,
            centered: true,
            centeredVertically: true);

        DrawFittedText(
            graphics,
            LabelGenerator.FormatPrice(request.PublicPrice),
            new RectangleF(205, 65, 108, 37),
            maxSize: 34.0f,
            minSize: 22.0f,
            bold: true,
            alignRight: true,
            centeredVertically: true);
    }

    private static void DrawNoteLayout(
        Graphics graphics,
        string note)
    {
        var text = (note ?? string.Empty)
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim()
            .ToUpperInvariant();

        if (text.Length == 0)
        {
            throw new InvalidOperationException(
                "Impossibile stampare: scrivere un testo.");
        }

        if (text.Length > 80)
        {
            text = text[..80];
        }

        /*
         * Dimensioni volutamente molto differenziate:
         * una nota corta deve riempire quasi tutta l'etichetta,
         * mentre una nota lunga viene distribuita fino a tre righe.
         */
        var maxSize = text.Length switch
        {
            <= 8 => 78.0f,
            <= 15 => 62.0f,
            <= 25 => 46.0f,
            <= 38 => 36.0f,
            <= 52 => 29.0f,
            <= 65 => 24.0f,
            _ => 20.0f
        };

        var minSize = text.Length switch
        {
            <= 8 => 44.0f,
            <= 15 => 36.0f,
            <= 25 => 28.0f,
            <= 38 => 22.0f,
            <= 52 => 18.0f,
            <= 65 => 15.0f,
            _ => 12.0f
        };

        var maxLines = text.Length switch
        {
            <= 15 => 1,
            <= 38 => 2,
            _ => 3
        };

        DrawFittedText(
            graphics,
            text,
            new RectangleF(6, 3, 308, 98),
            maxSize,
            minSize,
            true,
            centered: true,
            maxLines: maxLines,
            centeredVertically: true);
    }

    private static bool DrawImageLayout(
        Graphics graphics,
        Image productImage,
        LabelRenderRequest request,
        bool showPrice)
    {
        var code = LabelGenerator.NormalizeArticleCode(
            request.ArticleCode);
        var description = LabelGenerator.NormalizeDescription(
            request.Description);
        var barcode = LabelGenerator.NormalizeBarcode(
            request.Barcode);

        var imageArea = showPrice
            ? new Rectangle(-8, -11, 44, 52)
            : new Rectangle(-8, -11, 48, 58);

        DrawContainedImage(
            graphics,
            productImage,
            imageArea);

        DrawFittedText(
            graphics,
            code,
            new RectangleF(84, 0, 229, 28),
            maxSize: 24.0f,
            minSize: 18.0f,
            bold: true);

        DrawFittedText(
            graphics,
            description,
            new RectangleF(84, 16, 229, 24),
            maxSize: 13.0f,
            minSize: 10.0f,
            bold: false,
            maxLines: 2);

        var barcodeTop = showPrice ? 36 : 37;
        var barcodeHeight = showPrice ? 31 : 35;
        var numberTop = showPrice ? 65 : 71;

        DrawEan13(
            graphics,
            barcode,
            new Rectangle(67, barcodeTop, 239, barcodeHeight),
            drawNumber: false);

        DrawFittedText(
            graphics,
            FormatEanForDisplay(barcode),
            new RectangleF(72, numberTop, 228, 32),
            maxSize: 44.0f,
            minSize: 24.0f,
            bold: true,
            centered: true);

        if (showPrice)
        {
            graphics.DrawLine(
                Pens.Black,
                6,
                87,
                313,
                87);

            DrawFittedText(
                graphics,
                LabelGenerator.FormatPrice(request.PublicPrice),
                new RectangleF(178, 85, 134, 19),
                maxSize: 11.5f,
                minSize: 8.5f,
                bold: true,
                alignRight: true);
        }

        return true;
    }

    private static bool IsColloDateTime(string value)
    {
        return Regex.IsMatch(
            value ?? string.Empty,
            @"^\d{2}/\d{2}/\d{2}\s+\d{2}:\d{2}$");
    }

    private static void DrawEan13(
        Graphics graphics,
        string barcode,
        Rectangle bounds,
        bool drawNumber)
    {
        if (!IsValidEan13(barcode))
        {
            DrawFittedText(
                graphics,
                barcode,
                bounds,
                maxSize: 10f,
                minSize: 6f,
                bold: true,
                centered: true);
            return;
        }

        var modules = BuildEan13Modules(barcode);
        const int quietModules = 9;

        var moduleWidth = Math.Max(
            1,
            bounds.Width / (modules.Length + quietModules * 2));

        var renderedWidth =
            (modules.Length + quietModules * 2) * moduleWidth;

        var startX =
            bounds.X + (bounds.Width - renderedWidth) / 2 +
            quietModules * moduleWidth;

        using var brush = new SolidBrush(Color.Black);

        for (var index = 0; index < modules.Length; index++)
        {
            if (modules[index] != '1')
            {
                continue;
            }

            var isGuard =
                index < 3 ||
                index is >= 45 and < 50 ||
                index >= 92;

            var barHeight = isGuard
                ? bounds.Height
                : Math.Max(1, bounds.Height - 4);

            graphics.FillRectangle(
                brush,
                startX + index * moduleWidth,
                bounds.Y,
                moduleWidth,
                barHeight);
        }

        if (drawNumber)
        {
            DrawFittedText(
                graphics,
                FormatEanForDisplay(barcode),
                new RectangleF(
                    bounds.X,
                    bounds.Bottom - 14,
                    bounds.Width,
                    14),
                maxSize: 8f,
                minSize: 6f,
                bold: true,
                centered: true);
        }
    }

    private static string BuildEan13Modules(string ean)
    {
        var leftPatterns = new[,]
        {
            { "0001101", "0100111" },
            { "0011001", "0110011" },
            { "0010011", "0011011" },
            { "0111101", "0100001" },
            { "0100011", "0011101" },
            { "0110001", "0111001" },
            { "0101111", "0000101" },
            { "0111011", "0010001" },
            { "0110111", "0001001" },
            { "0001011", "0010111" }
        };

        var parityPatterns = new[]
        {
            "LLLLLL", "LLGLGG", "LLGGLG", "LLGGGL", "LGLLGG",
            "LGGLLG", "LGGGLL", "LGLGLG", "LGLGGL", "LGGLGL"
        };

        var rightPatterns = new[]
        {
            "1110010", "1100110", "1101100", "1000010", "1011100",
            "1001110", "1010000", "1000100", "1001000", "1110100"
        };

        var result = new System.Text.StringBuilder(95);
        result.Append("101");

        var parity = parityPatterns[ean[0] - '0'];

        for (var index = 1; index <= 6; index++)
        {
            var digit = ean[index] - '0';
            var patternIndex = parity[index - 1] == 'L' ? 0 : 1;
            result.Append(leftPatterns[digit, patternIndex]);
        }

        result.Append("01010");

        for (var index = 7; index <= 12; index++)
        {
            result.Append(rightPatterns[ean[index] - '0']);
        }

        result.Append("101");

        return result.ToString();
    }

    private static bool IsValidEan13(string value)
    {
        if (value.Length != 13 || value.Any(c => !char.IsDigit(c)))
        {
            return false;
        }

        var sum = 0;

        for (var index = 0; index < 12; index++)
        {
            var digit = value[index] - '0';
            sum += index % 2 == 0 ? digit : digit * 3;
        }

        var expected = (10 - sum % 10) % 10;

        return value[12] - '0' == expected;
    }

    private static string FormatEanForDisplay(string barcode)
    {
        if (barcode.Length != 13)
        {
            return barcode;
        }

        return $"{barcode[0]} {barcode.Substring(1, 6)} {barcode.Substring(7, 6)}";
    }

    private static void DrawContainedImage(
        Graphics graphics,
        Image image,
        Rectangle destination)
    {
        var scale = Math.Min(
            destination.Width / (double)image.Width,
            destination.Height / (double)image.Height);

        var width = Math.Max(
            1,
            (int)Math.Round(image.Width * scale));

        var height = Math.Max(
            1,
            (int)Math.Round(image.Height * scale));

        var x =
            destination.X +
            (destination.Width - width) / 2;

        var y =
            destination.Y +
            (destination.Height - height) / 2;

        /*
         * La GoDEX è una stampante monocromatica.
         * Per simulare le sfumature di Crystal Reports,
         * l'immagine viene prima ridimensionata e poi convertita
         * con dithering Floyd-Steinberg.
         *
         * Testo e barcode non vengono ditherizzati:
         * restano neri e nitidi.
         */
        using var resized = new Bitmap(
            width,
            height,
            PixelFormat.Format24bppRgb);

        using (var resizeGraphics = Graphics.FromImage(resized))
        {
            resizeGraphics.Clear(Color.White);
            resizeGraphics.InterpolationMode =
                InterpolationMode.HighQualityBicubic;
            resizeGraphics.SmoothingMode =
                SmoothingMode.HighQuality;
            resizeGraphics.PixelOffsetMode =
                PixelOffsetMode.HighQuality;

            resizeGraphics.DrawImage(
                image,
                new Rectangle(0, 0, width, height));
        }

        using var grayscale =
            ConvertToGrayscale24Bit(resized);

        graphics.DrawImageUnscaled(
            grayscale,
            x,
            y);
    }

    private static Bitmap ConvertToGrayscale24Bit(
        Bitmap source)
    {
        var result = new Bitmap(
            source.Width,
            source.Height,
            PixelFormat.Format24bppRgb);

        const double contrast = 1.05;

        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var color = source.GetPixel(x, y);

                var gray =
                    color.R * 0.299 +
                    color.G * 0.587 +
                    color.B * 0.114;

                gray =
                    (gray - 128.0) * contrast +
                    128.0;

                var value = (int)Math.Clamp(
                    Math.Round(gray),
                    0.0,
                    255.0);

                result.SetPixel(
                    x,
                    y,
                    Color.FromArgb(
                        value,
                        value,
                        value));
            }
        }

        return result;
    }

    private static void DrawFittedText(
        Graphics graphics,
        string text,
        RectangleF bounds,
        float maxSize,
        float minSize,
        bool bold,
        bool centered = false,
        bool alignRight = false,
        int maxLines = 1,
        bool centeredVertically = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var fontStyle = bold
            ? FontStyle.Bold
            : FontStyle.Regular;

        using var format = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoClip,
            Alignment = alignRight
                ? StringAlignment.Far
                : centered
                    ? StringAlignment.Center
                    : StringAlignment.Near,
            LineAlignment = centeredVertically
                ? StringAlignment.Center
                : StringAlignment.Near
        };

        if (maxLines == 1)
        {
            format.FormatFlags |= StringFormatFlags.NoWrap;
        }

        Font? selected = null;

        for (var size = maxSize; size >= minSize; size -= 0.4f)
        {
            var candidate = new Font(
                "Arial Narrow",
                size,
                fontStyle,
                GraphicsUnit.Pixel);

            var measured = graphics.MeasureString(
                text,
                candidate,
                new SizeF(bounds.Width, bounds.Height),
                format);

            var lineHeight = candidate.GetHeight(graphics);
            var lineCount = Math.Max(
                1,
                (int)Math.Ceiling(measured.Height / lineHeight));

            if (
                measured.Width <= bounds.Width + 1 &&
                measured.Height <= bounds.Height + 1 &&
                lineCount <= maxLines
            )
            {
                selected = candidate;
                break;
            }

            candidate.Dispose();
        }

        selected ??= new Font(
            "Arial Narrow",
            minSize,
            fontStyle,
            GraphicsUnit.Pixel);

        using (selected)
        using (var brush = new SolidBrush(Color.Black))
        {
            graphics.DrawString(
                text,
                selected,
                brush,
                bounds,
                format);
        }
    }

    private static Image? TryLoadProductImage(string? path)
    {
        if (
            string.IsNullOrWhiteSpace(path) ||
            !File.Exists(path)
        )
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);

            using var source = Image.FromStream(stream);

            return new Bitmap(source);
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap ConvertToGrayscale8Bit(
        Bitmap source)
    {
        var result = new Bitmap(
            source.Width,
            source.Height,
            PixelFormat.Format8bppIndexed);

        var palette = result.Palette;

        for (var index = 0; index < 256; index++)
        {
            palette.Entries[index] =
                Color.FromArgb(
                    index,
                    index,
                    index);
        }

        result.Palette = palette;

        var rectangle = new Rectangle(
            0,
            0,
            result.Width,
            result.Height);

        var data = result.LockBits(
            rectangle,
            ImageLockMode.WriteOnly,
            PixelFormat.Format8bppIndexed);

        try
        {
            var stride = Math.Abs(data.Stride);
            var bytes = new byte[stride * result.Height];

            for (var y = 0; y < result.Height; y++)
            {
                var row = y * stride;

                for (var x = 0; x < result.Width; x++)
                {
                    var color = source.GetPixel(x, y);

                    var gray =
                        color.R * 0.299 +
                        color.G * 0.587 +
                        color.B * 0.114;

                    bytes[row + x] =
                        (byte)Math.Clamp(
                            (int)Math.Round(gray),
                            0,
                            255);
                }
            }

            Marshal.Copy(
                bytes,
                0,
                data.Scan0,
                bytes.Length);
        }
        finally
        {
            result.UnlockBits(data);
        }

        return result;
    }

    private static void ConfigureGraphics(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.InterpolationMode =
            InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.TextRenderingHint =
            TextRenderingHint.SingleBitPerPixelGridFit;
    }
}