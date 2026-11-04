using Lunar.Conversion.Providers;

namespace Lunar.Conversion.Tests.Support;

/// <summary>
/// A stand-in for LibreOffice: a small batch file that records the arguments it was started with (one per line), then behaves as the test
/// says through environment variables it inherits: <c>ok</c> (copies a prepared PDF to the output folder under the input's name),
/// <c>fail</c> (exit code 3), <c>garbage</c> (writes something that is not a PDF), <c>nooutput</c> (exits cleanly, writes nothing) or
/// <c>hang</c> (starts a child process that sleeps for two minutes). This proves how Lunar starts, waits for, times out and stops an
/// engine and its children; it proves nothing about what the real LibreOffice does with a document (that check is NOT_RUN).
/// Tests that use it must not run in parallel with each other (the variables are process wide): keep them in one class.
/// </summary>
internal sealed class FakeOffice : ILibreOfficeLocator, IDisposable
{
    private const string Script = """
        @echo off
        setlocal enabledelayedexpansion
        set OUT=
        set LAST=
        set PREV=
        :loop
        if "%~1"=="" goto done
        if "!PREV!"=="--outdir" set OUT=%~1
        >>"%LUNAR_FAKE_OFFICE_DIR%\args.txt" echo %~1
        set PREV=%~1
        set LAST=%~1
        shift
        goto loop
        :done
        >>"%LUNAR_FAKE_OFFICE_DIR%\cmdline.txt" echo %*
        >"%LUNAR_FAKE_OFFICE_DIR%\started.txt" echo started
        if "%LUNAR_FAKE_OFFICE_MODE%"=="hang" ( ping -n 120 127.0.0.1 >nul & exit /b 0 )
        if "%LUNAR_FAKE_OFFICE_MODE%"=="fail" exit /b 3
        if "%LUNAR_FAKE_OFFICE_MODE%"=="nooutput" exit /b 0
        for %%F in ("%LAST%") do set BASE=%%~nF
        if "%LUNAR_FAKE_OFFICE_MODE%"=="garbage" ( >"%OUT%\%BASE%.pdf" echo this is not a pdf & exit /b 0 )
        copy /y "%LUNAR_FAKE_OFFICE_PDF%" "%OUT%\%BASE%.pdf" >nul
        exit /b 0
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lunar-fakeoffice-" + Guid.NewGuid().ToString("N"));

    public FakeOffice(byte[] pdfToProduce)
    {
        Directory.CreateDirectory(_folder);
        ExecutablePath = Path.Combine(_folder, "soffice.cmd");
        File.WriteAllText(ExecutablePath, Script.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        File.WriteAllBytes(Path.Combine(_folder, "produced.pdf"), pdfToProduce);
        Environment.SetEnvironmentVariable("LUNAR_FAKE_OFFICE_DIR", _folder);
        Environment.SetEnvironmentVariable("LUNAR_FAKE_OFFICE_PDF", Path.Combine(_folder, "produced.pdf"));
        Mode = "ok";
    }

    public string ExecutablePath { get; }

    /// <summary>When false the stand-in is "not installed".</summary>
    public bool Installed { get; set; } = true;

    public string Mode
    {
        set => Environment.SetEnvironmentVariable("LUNAR_FAKE_OFFICE_MODE", value);
    }

    public bool Started => File.Exists(Path.Combine(_folder, "started.txt"));

    /// <summary>The arguments of the last start, one per element.</summary>
    public string[] Arguments => File.Exists(Path.Combine(_folder, "args.txt")) ? File.ReadAllLines(Path.Combine(_folder, "args.txt")) : [];

    /// <summary>The whole command line of the last start, exactly as it reached the program (a batch file splits arguments at "=", so use this for those).</summary>
    public string CommandLine => File.Exists(Path.Combine(_folder, "cmdline.txt")) ? File.ReadAllText(Path.Combine(_folder, "cmdline.txt")).Trim() : string.Empty;

    /// <summary>Forgets what was recorded so far (between two runs in one test).</summary>
    public void Reset()
    {
        foreach (var name in new[] { "args.txt", "cmdline.txt", "started.txt" })
        {
            var path = Path.Combine(_folder, name);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    public LibreOfficeInfo? Locate() => Installed ? new LibreOfficeInfo(ExecutablePath, new Version(24, 2)) : null;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("LUNAR_FAKE_OFFICE_MODE", null);
        Environment.SetEnvironmentVariable("LUNAR_FAKE_OFFICE_DIR", null);
        Environment.SetEnvironmentVariable("LUNAR_FAKE_OFFICE_PDF", null);
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
