using System.IO.Pipes;
using System.Text;

public readonly record struct FrontHelperResult(
    bool Success,
    string Message,
    string? Response = null);

public static class FrontHelperClient
{
    private const string PipeName = "Scan2EnterFrontHelper";

    public static async Task<FrontHelperResult> SendColloAsync(
        string barcode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(barcode))
        {
            return new FrontHelperResult(
                false,
                "Barcode collo mancante.");
        }

        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await pipe.ConnectAsync(
                3000,
                cancellationToken);

            using var reader = new StreamReader(
                pipe,
                Encoding.UTF8,
                false,
                1024,
                leaveOpen: true);

            using var writer = new StreamWriter(
                pipe,
                new UTF8Encoding(false),
                1024,
                leaveOpen: true)
            {
                AutoFlush = true
            };

            await writer.WriteLineAsync(barcode);

            var response = await reader.ReadLineAsync(
                cancellationToken);

            if (string.IsNullOrWhiteSpace(response))
            {
                return new FrontHelperResult(
                    false,
                    "L'helper FRONT non ha restituito una risposta.");
            }

            if (response.StartsWith(
                    "OK|",
                    StringComparison.OrdinalIgnoreCase))
            {
                return new FrontHelperResult(
                    true,
                    "Collo inviato al FRONT.",
                    response);
            }

            if (response.StartsWith(
                    "ERR|",
                    StringComparison.OrdinalIgnoreCase))
            {
                var parts = response.Split(
                    '|',
                    3,
                    StringSplitOptions.None);

                var message =
                    parts.Length >= 3
                        ? parts[2]
                        : response;

                return new FrontHelperResult(
                    false,
                    message,
                    response);
            }

            return new FrontHelperResult(
                false,
                $"Risposta helper non riconosciuta: {response}",
                response);
        }
        catch (TimeoutException)
        {
            return new FrontHelperResult(
                false,
                "Scan2EnterFrontHelper non è raggiungibile.");
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return new FrontHelperResult(
                false,
                "Timeout durante la comunicazione con il FRONT.");
        }
        catch (Exception ex)
        {
            return new FrontHelperResult(
                false,
                $"Errore comunicazione con FRONT: {ex.Message}");
        }
    }
}
