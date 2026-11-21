using System.Diagnostics;
using System.Globalization;

namespace ThixShot.Integration;

internal static class PackageInstaller
{
    private static string WindowsApp => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "ThixShot", "ThixShot.exe");

    private static string? MacBundle
    {
        get
        {
            var executable = Environment.ProcessPath;
            if (executable is null || !executable.EndsWith("/ThixShot.app/Contents/MacOS/ThixShot", StringComparison.Ordinal)) return null;
            return executable[..^"/Contents/MacOS/ThixShot".Length];
        }
    }

    internal static bool IsInstalled => OperatingSystem.IsWindows()
        ? string.Equals(WindowsApp, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)
        : OperatingSystem.IsMacOS() && MacBundle is not null;

    internal static async Task ScheduleAsync(string package, CancellationToken ct)
    {
        if (!IsInstalled) throw new InvalidOperationException("Starte die installierte ThixShot-App, um das Update anzuwenden.");
        ct.ThrowIfCancellationRequested();
        var runId = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        if (OperatingSystem.IsWindows())
        {
            var windows = Environment.SystemDirectory;
            var start = StartInfo(Path.Combine(windows, @"WindowsPowerShell\v1.0\powershell.exe"),
                "-NoProfile", "-NonInteractive", "-Command", WindowsInstall);
            start.Environment["THIXSHOT_WAIT_PID"] = runId;
            start.Environment["THIXSHOT_PACKAGE"] = package;
            start.Environment["THIXSHOT_APP"] = WindowsApp;
            start.Environment["THIXSHOT_MSIEXEC"] = Path.Combine(windows, "msiexec.exe");
            Launch(start).Dispose();
            return;
        }

        var current = MacBundle!;
        // Staging alongside the installed bundle guarantees both renames stay on one volume.
        var staging = Path.Combine(Path.GetDirectoryName(current)!, ".thixshot-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            await RunAsync("/usr/bin/ditto", ct, "-x", "-k", package, staging);
            var bundle = Path.Combine(staging, "ThixShot.app");
            var info = Path.Combine(bundle, "Contents", "Info.plist");
            var bundleId = await RunAsync("/usr/bin/plutil", ct, "-extract", "CFBundleIdentifier", "raw", "-o", "-", info);
            if (bundleId.Trim() != "de.thixapps.thixshot" || !File.Exists(Path.Combine(bundle, "Contents", "MacOS", "ThixShot")))
                throw new InvalidDataException("Das Update enthält keine gültige ThixShot.app.");
            await RunAsync("/usr/bin/codesign", ct, "--verify", "--deep", "--strict", bundle);
            ct.ThrowIfCancellationRequested();
            Launch(StartInfo("/bin/sh", "-c", MacInstall, "ThixShot", runId, current, staging, Path.GetDirectoryName(package)!)).Dispose();
        }
        catch
        {
            Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    private static async Task<string> RunAsync(string tool, CancellationToken ct, params string[] arguments)
    {
        var info = StartInfo(tool, arguments);
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        using var process = Launch(info);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        if (process.ExitCode != 0) throw new IOException($"Update fehlgeschlagen ({Path.GetFileName(tool)}). {(await error).Trim()}");
        return await output;
    }

    private static ProcessStartInfo StartInfo(string path, params string[] args)
    {
        var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in args) info.ArgumentList.Add(argument);
        return info;
    }

    private static Process Launch(ProcessStartInfo start) =>
        Process.Start(start) ?? throw new IOException("Der Installationsprozess konnte nicht gestartet werden.");

    private const string WindowsInstall = """
        $ErrorActionPreference = 'Stop'
        $owner = Get-Process -Id ([int]$env:THIXSHOT_WAIT_PID) -ErrorAction SilentlyContinue
        if ($owner) { $owner.WaitForExit() }
        $package = $env:THIXSHOT_PACKAGE
        $log = Join-Path (Split-Path -Parent $package) 'install.log'
        $arguments = '/i "' + $package + '" /qn /norestart /L*v "' + $log + '"'
        $result = Start-Process -FilePath $env:THIXSHOT_MSIEXEC -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
        if ($result.ExitCode -in 0,3010) {
            Remove-Item -LiteralPath $package -Force
        }
        if (Test-Path -LiteralPath $env:THIXSHOT_APP) { Start-Process -FilePath $env:THIXSHOT_APP }
        """;

    private const string MacInstall = """
        while kill -0 "$1" 2>/dev/null; do /bin/sleep 0.5; done
        current="$2"
        stage="$3"
        download="$4"
        if /bin/mv "$current" "$stage/previous.app"; then
          if /bin/mv "$stage/ThixShot.app" "$current"; then
            /bin/rm -rf "$stage" "$download"
          else
            /bin/mv "$stage/previous.app" "$current"
          fi
        fi
        /usr/bin/open "$current"
        """;
}
