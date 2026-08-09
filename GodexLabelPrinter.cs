namespace Scan2EnterGateway;

public sealed class GodexLabelPrinter
{
    private readonly LabelBitmapRenderer _renderer;
    private readonly WindowsLabelPrinter _windowsPrinter;

    public GodexLabelPrinter(
        LabelBitmapRenderer renderer,
        WindowsLabelPrinter windowsPrinter)
    {
        _renderer = renderer;
        _windowsPrinter = windowsPrinter;
    }

    public async Task<RenderedLabel> PrintAsync(
        LabelRenderRequest request,
        int quantity,
        CancellationToken cancellationToken = default)
    {
        quantity = Math.Clamp(
            quantity,
            1,
            LabelGenerator.MaxQuantity);

        /*
         * Tutti i modelli, compresa la STANDARD, passano ora
         * attraverso il driver Windows "Godex G500".
         *
         * In questo modo dimensioni, font e posizioni vengono
         * controllati tutti dal LabelBitmapRenderer.
         */
        var rendered = _renderer.Render(request);

        await _windowsPrinter.PrintAsync(
            rendered.BitmapBytes,
            quantity,
            cancellationToken);

        return rendered;
    }
}
