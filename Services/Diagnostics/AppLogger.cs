using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LiveCaptioner.Services.Diagnostics;

public static class AppLogger
{
    private static readonly object SyncRoot = new();
    private static string? _logFilePath;
    private static bool _initialized;

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleTitle(string lpConsoleTitle);

    public static void Initialize(string projectRoot)
    {
        lock (SyncRoot)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            Directory.CreateDirectory(Path.Combine(projectRoot, "Logs"));
            _logFilePath = Path.Combine(projectRoot, "Logs", $"livecaptioner-{DateTime.Now:yyyyMMdd-HHmmss}.log");

            try
            {
                AllocConsole();
                SetConsoleTitle("LiveCaptioner logs");
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch
            {
                // Logging to file/debug output still works if the console cannot be created.
            }

            Info("Logger initialized.");
            Info($"Log file: {_logFilePath}");
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? exception = null)
        => Write("ERROR", exception == null ? message : $"{message}{Environment.NewLine}{exception}");

    public static void Memory(string label)
    {
        using var process = Process.GetCurrentProcess();
        var managedMb = GC.GetTotalMemory(false) / 1024d / 1024d;
        var workingSetMb = process.WorkingSet64 / 1024d / 1024d;
        var privateMb = process.PrivateMemorySize64 / 1024d / 1024d;
        Write("MEM", $"{label}: managed={managedMb:0.0} MB, workingSet={workingSetMb:0.0} MB, private={privateMb:0.0} MB");
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";

        lock (SyncRoot)
        {
            try
            {
                Console.WriteLine(line);
            }
            catch
            {
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(_logFilePath))
                {
                    File.AppendAllText(_logFilePath, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
            }

            Debug.WriteLine(line);
        }
    }
}
