using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

const string PipeName = "Scan2EnterFrontHelper";

if (args.Length == 1)
{
    var result = DueFront.SendCollo(args[0]);

    if (result.Success)
    {
        Console.WriteLine($"Collo inviato al FRONT: {args[0]}");
        return 0;
    }

    Console.Error.WriteLine(result.Message);
    return result.ErrorCode;
}

if (args.Length != 0)
{
    Console.Error.WriteLine("Uso:");
    Console.Error.WriteLine("  Scan2EnterFrontHelper.exe <barcode-collo>");
    Console.Error.WriteLine("  Scan2EnterFrontHelper.exe");
    return 2;
}

Console.WriteLine("Scan2EnterFrontHelper avviato.");
Console.WriteLine($"Named Pipe: {PipeName}");
Console.WriteLine("In attesa di comandi dal Gateway...");

while (true)
{
    try
    {
        await using var pipe = new NamedPipeServerStream(
            PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        await pipe.WaitForConnectionAsync();

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

        var command = await reader.ReadLineAsync();

        if (string.IsNullOrWhiteSpace(command))
        {
            await writer.WriteLineAsync("ERR|2|Comando vuoto.");
            continue;
        }

        command = command.Trim();

        Console.WriteLine(
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Richiesta: {command}");

        var result = DueFront.SendCollo(command);

        if (result.Success)
        {
            await writer.WriteLineAsync($"OK|{command}");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] OK {command}");
        }
        else
        {
            await writer.WriteLineAsync(
                $"ERR|{result.ErrorCode}|{result.Message}");

            Console.WriteLine(
                $"[{DateTime.Now:HH:mm:ss}] ERRORE {result.Message}");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Errore helper: {ex.Message}");

        await Task.Delay(500);
    }
}


internal readonly record struct SendResult(
    bool Success,
    int ErrorCode,
    string Message);


internal static class DueFront
{
    public static SendResult SendCollo(string rawBarcode)
    {
        var barcode = rawBarcode.Trim();

        if (barcode.Length != 13 || !barcode.All(char.IsDigit))
        {
            return new SendResult(
                false,
                3,
                "Barcode collo non valido.");
        }

        var dueProcesses = Process.GetProcessesByName("2bit.main");

        if (dueProcesses.Length == 0)
        {
            return new SendResult(
                false,
                10,
                "DUE non risulta avviato.");
        }

        var dueProcess = dueProcesses[0];

        var dueForm =
            NativeMethods.FindDueOperationalForm((uint)dueProcess.Id);

        if (dueForm == IntPtr.Zero)
        {
            return new SendResult(
                false,
                11,
                "Finestra operativa DUE non trovata.");
        }

        NativeMethods.ShowWindow(
            dueForm,
            NativeMethods.SW_RESTORE);

        NativeMethods.SetForegroundWindow(dueForm);

        Thread.Sleep(700);

        if (!NativeMethods.GetWindowRect(
                dueForm,
                out var rect))
        {
            return new SendResult(
                false,
                12,
                "Impossibile leggere la finestra DUE.");
        }

        /*
         * Pulsante NUOVO SCONTRINO.
         *
         * Coordinate ricavate e verificate sulla form DUE:
         * X circa 1190
         * Y circa 1279
         *
         * Form di riferimento:
         * 2563 x 1443
         *
         * Usiamo proporzioni rispetto alla form operativa,
         * non coordinate assolute dello schermo.
         */
        const double newReceiptXRatio =
            966.0 / 2050.0;

        const double newReceiptYRatio =
            1020.0 / 1154.0;

        var width =
            rect.Right - rect.Left;

        var height =
            rect.Bottom - rect.Top;

        var clickX =
            rect.Left +
            (int)Math.Round(width * newReceiptXRatio);

        var clickY =
            rect.Top +
            (int)Math.Round(height * newReceiptYRatio);

        dynamic shell = Activator.CreateInstance(
            Type.GetTypeFromProgID("WScript.Shell")!
        )!;

        shell.AppActivate(dueProcess.Id);

        Thread.Sleep(500);

        NativeMethods.SetCursorPos(
            clickX,
            clickY);

        Thread.Sleep(300);

        NativeMethods.MouseClick();

        Thread.Sleep(1200);

        NativeMethods.SetForegroundWindow(dueForm);
        shell.AppActivate(dueProcess.Id);

        Thread.Sleep(500);

        shell.SendKeys(barcode);
        shell.SendKeys("{ENTER}");

        return new SendResult(
            true,
            0,
            "Collo inviato al FRONT.");
    }
}


internal static class NativeMethods
{
    public const int SW_RESTORE = 9;

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    public delegate bool EnumWindowsProc(
        IntPtr hWnd,
        IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(
        EnumWindowsProc lpEnumFunc,
        IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(
        IntPtr hWnd,
        out uint processId);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(
        IntPtr hWnd);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Auto)]
    public static extern int GetClassName(
        IntPtr hWnd,
        StringBuilder lpClassName,
        int nMaxCount);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(
        IntPtr hWnd,
        out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(
        IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(
        IntPtr hWnd,
        int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(
        int X,
        int Y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(
        uint dwFlags,
        uint dx,
        uint dy,
        uint dwData,
        UIntPtr dwExtraInfo);

    public static IntPtr FindDueOperationalForm(
        uint processId)
    {
        IntPtr result = IntPtr.Zero;

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(
                hWnd,
                out var windowProcessId);

            if (windowProcessId != processId ||
                !IsWindowVisible(hWnd))
            {
                return true;
            }

            var className =
                new StringBuilder(256);

            GetClassName(
                hWnd,
                className,
                className.Capacity);

            if (!string.Equals(
                    className.ToString(),
                    "ThunderRT6FormDC",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!GetWindowRect(
                    hWnd,
                    out var rect))
            {
                return true;
            }

            var width =
                rect.Right - rect.Left;

            var height =
                rect.Bottom - rect.Top;

            if (width > 1000 &&
                height > 700)
            {
                result = hWnd;
                return false;
            }

            return true;

        }, IntPtr.Zero);

        return result;
    }

    public static void MouseClick()
    {
        mouse_event(
            MOUSEEVENTF_LEFTDOWN,
            0, 0, 0,
            UIntPtr.Zero);

        mouse_event(
            MOUSEEVENTF_LEFTUP,
            0, 0, 0,
            UIntPtr.Zero);
    }
}


