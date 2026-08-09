using System.Globalization;
using System.Text;

namespace Scan2EnterGateway;

public enum LabelTemplate
{
    Standard,
    Image,
    Price,
    ImagePrice,
    Note
}

public sealed record LabelRenderRequest(
    string ArticleCode,
    string Description,
    string Barcode,
    string PublicPrice,
    string? ImagePath,
    LabelTemplate Template,
    string Note = "");

public sealed record RenderedLabel(
    byte[] BitmapBytes,
    LabelTemplate RequestedTemplate,
    LabelTemplate ActualTemplate,
    bool ImageUsed);

public static class LabelGenerator
{
    public const int LabelWidthDots = 320;
    public const int LabelHeightDots = 104;
    public const int MaxQuantity = 100;

    public static LabelTemplate ParseTemplate(string? value)
    {
        return (value ?? string.Empty)
            .Trim()
            .ToUpperInvariant() switch
        {
            "IMAGE" => LabelTemplate.Image,
            "PRICE" => LabelTemplate.Price,
            "IMAGE_PRICE" => LabelTemplate.ImagePrice,
            "NOTE" => LabelTemplate.Note,
            _ => LabelTemplate.Standard
        };
    }

    public static string ToApiValue(LabelTemplate template)
    {
        return template switch
        {
            LabelTemplate.Image => "IMAGE",
            LabelTemplate.Price => "PRICE",
            LabelTemplate.ImagePrice => "IMAGE_PRICE",
            LabelTemplate.Note => "NOTE",
            _ => "STANDARD"
        };
    }

    public static bool RequiresImage(LabelTemplate template)
    {
        return template is LabelTemplate.Image or LabelTemplate.ImagePrice;
    }

    public static string NormalizeArticleCode(string? value)
    {
        var result = Sanitize(value).ToUpperInvariant();

        return result.Length <= 20
            ? result
            : result[..20];
    }

    public static string NormalizeDescription(string? value)
    {
        var result = Sanitize(value);

        return result.Length <= 52
            ? result
            : result[..49] + "...";
    }

    public static string NormalizeBarcode(string? value)
    {
        return new string(
            (value ?? string.Empty)
                .Where(char.IsDigit)
                .ToArray());
    }

    public static string FormatPrice(string? value)
    {
        var raw = (value ?? string.Empty)
            .Trim()
            .Replace("€", string.Empty)
            .Replace(" ", string.Empty);

        if (raw.Contains(',') && raw.Contains('.'))
        {
            raw = raw.Replace(".", string.Empty).Replace(',', '.');
        }
        else
        {
            raw = raw.Replace(',', '.');
        }

        if (!decimal.TryParse(
                raw,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var price))
        {
            return "€ --";
        }

        return $"€ {price.ToString("0.00", CultureInfo.GetCultureInfo("it-IT"))}";
    }

    public static string GraphicName(LabelTemplate template)
    {
        return template switch
        {
            LabelTemplate.Image => "S2E_IMG",
            LabelTemplate.Price => "S2E_PRICE",
            LabelTemplate.ImagePrice => "S2E_IMGP",
            _ => "S2E_STD"
        };
    }

    public static string CreateDeleteGraphicCommand(string graphicName)
    {
        return $"~MDELG,{graphicName}\r\n";
    }

    public static string CreateGraphicDownloadHeader(
        string graphicName,
        int byteCount)
    {
        return $"~EB,{graphicName},{byteCount}\r\n";
    }

    public static string CreatePrintCommand(
        string graphicName,
        int quantity)
    {
        quantity = Math.Clamp(quantity, 1, MaxQuantity);

        var command = new StringBuilder();

        command.Append("\r\n");
        command.Append("^Q13,3\r\n");
        command.Append("^W40\r\n");
        command.Append("^H10\r\n");
        command.Append("^S2\r\n");
        command.Append("^AD\r\n");
        command.Append("^C1\r\n");
        command.Append($"^P{quantity}\r\n");
        command.Append("^R0\r\n");
        command.Append("^D0\r\n");
        command.Append("^L\r\n");
        command.Append($"Y0,0,{graphicName}\r\n");
        command.Append("E\r\n");

        return command.ToString();
    }

    private static string Sanitize(string? value)
    {
        return (value ?? string.Empty)
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace(",", " ")
            .Trim();
    }
}
