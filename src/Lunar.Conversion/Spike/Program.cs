using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Lunar.Conversion.Spike;

internal static class Program
{
    internal static uint SystemDpi { get; private set; }
    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Record(string experiment, string status, object details) => Console.WriteLine(
        JsonSerializer.Serialize(new { Timestamp = DateTimeOffset.UtcNow, Experiment = experiment, Status = status, Details = details }));

    internal static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static async Task<int> Main(string[] args)
    {
        var elapsed = Stopwatch.StartNew();
        try
        {
            var dpiAware = SetProcessDpiAwarenessContext(new IntPtr(-4));
            SystemDpi = GetDpiForSystem();
            Check(SystemDpi > 0, "System DPI unavailable");
            Record("ProcessDpiContext", "INFO", new { PerMonitorV2Requested = dpiAware, SystemDpi });
            Check(args.Length == 2 && args[0] is "generate" or "render-reference", "Expected mode and workspace");
            var allowedRoot = Path.GetFullPath("artifacts/sprint-0-converter") + Path.DirectorySeparatorChar;
            var workspace = Path.GetFullPath(args[1]);
            Check(workspace.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase), "Workspace must be inside experiment artifacts");
            if (args[0] == "generate")
            {
                Check(!Directory.Exists(workspace), "Use a new workspace; experiments never overwrite previous runs");
                Directory.CreateDirectory(workspace);
                ImageExperiments.Run(workspace);
                DocxExperiment.Create(workspace);
                await PdfExperiments.Run(workspace);
            }
            else
            {
                var reference = Path.Combine(workspace, "word-reference.pdf");
                Check(File.Exists(reference), "Reference export missing");
                await PdfExperiments.Render(reference, workspace, "word-reference", 150, 2);
                Record("DOCX-WordReferenceRaster", "PASS", new { ExpectedPages = 2, Dpi = 150,
                    Boundary = "Installed Word reference only; not LibreOffice fidelity proof" });
            }
            var files = Directory.GetFiles(workspace).Where(p => !p.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .Select(p => new { Fixture = Path.GetFileName(p), Bytes = new FileInfo(p).Length, SHA256 = Hash(p) }).ToArray();
            File.WriteAllText(Path.Combine(workspace, "manifest.json"), JsonSerializer.Serialize(files, new JsonSerializerOptions { WriteIndented = true }));
            Record("ExperimentProcess", "PASS", new { elapsed.ElapsedMilliseconds,
                PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64, Files = files.Length,
                Boundary = "Synthetic bounded CLI experiment; not production queue/cancellation/security acceptance" });
            return 0;
        }
        catch (Exception ex)
        {
            Record("ExperimentProcess", "FAIL", new { ErrorType = ex.GetType().Name,
                HResult = $"0x{ex.HResult:X8}", Assertion = ex is InvalidOperationException ? ex.Message : null,
                elapsed.ElapsedMilliseconds });
            return 1;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
