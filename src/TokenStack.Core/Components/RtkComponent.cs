using System.IO.Compression;
using TokenStack.Core.Config;
using TokenStack.Core.Windows;

namespace TokenStack.Core.Components;

public sealed class RtkComponent(IProcessRunner runner, IEnvStore env)
{
    public static string ReleaseUrl(RtkConfig cfg)
    {
        const string prefix = "github:";
        if (!cfg.Source.StartsWith(prefix, StringComparison.Ordinal))
            throw new ArgumentException(
                $"rtk.source must be 'github:<owner>/<repo>' (got '{cfg.Source}')");
        var repo = cfg.Source[prefix.Length..];
        return $"https://github.com/{repo}/releases/download/v{cfg.Version}/rtk-x86_64-pc-windows-msvc.zip";
    }

    public static void ExtractRtkExe(string zipPath, string destDir)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.FirstOrDefault(e =>
                e.Name.Equals("rtk.exe", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"rtk.exe not found inside {zipPath}");
        Directory.CreateDirectory(destDir);
        entry.ExtractToFile(Path.Combine(destDir, "rtk.exe"), overwrite: true);
    }

    public string RtkDir(StackConfig cfg) => Path.Combine(cfg.InstallRoot, "rtk");
    public string RtkExe(StackConfig cfg) => Path.Combine(RtkDir(cfg), "rtk.exe");

    public void Install(StackConfig cfg, InstallSource src)
    {
        Directory.CreateDirectory(RtkDir(cfg));
        if (src.IsOffline)
        {
            if (!File.Exists(src.RtkExe))
                throw new FileNotFoundException($"offline rtk.exe missing in bundle: {src.RtkExe}");
            File.Copy(src.RtkExe, RtkExe(cfg), overwrite: true);
        }
        else
        {
            var url = ReleaseUrl(cfg.Rtk);
            var tmpZip = Path.Combine(cfg.InstallRoot, "tmp", "rtk.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(tmpZip)!);
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            using (var stream = http.GetStreamAsync(url).GetAwaiter().GetResult())
            using (var file = File.Create(tmpZip))
                stream.CopyTo(file);
            ExtractRtkExe(tmpZip, RtkDir(cfg));
            File.Delete(tmpZip);
        }

        UserPath.EnsureContains(env, RtkDir(cfg));
        var verify = runner.Run(RtkExe(cfg), "--version", 15000);
        if (!verify.Ok)
            throw new InvalidOperationException($"rtk.exe placed but failed to run: {verify.StdErr}");
    }

    public bool IsOnPath() => runner.Run("rtk", "--version", 10000).Ok;

    /// <summary>Which rtk a bare `rtk` would run, judged against the PATH a *newly launched*
    /// Claude will see: the process PATH (which carries the whole MACHINE PATH plus the USER
    /// entries that existed at startup) joined with the persisted USER PATH we just wrote. Ours
    /// is appended last, so anything able to shadow it also appears earlier in this join —
    /// which is exactly the ordering we need to detect shadowing.</summary>
    public static string? ResolveOnPath(IEnvStore env) => PathResolver.Resolve(
        string.Join(';', new[] { env.GetProcess("Path"), env.GetUser("Path") }
            .Where(s => !string.IsNullOrWhiteSpace(s))),
        env.GetProcess("PATHEXT"),
        "rtk");

    /// <summary>True only when a bare `rtk` provably resolves to the exe WE installed. Gates
    /// writing the bare hook spelling: `rtk --version` succeeding proves nothing, because a
    /// planted binary answers it too, and that binary would control every Bash rewrite.</summary>
    public static bool BareHookIsSafe(IEnvStore env, StackConfig cfg) =>
        PathResolver.SamePath(ResolveOnPath(env), Path.Combine(cfg.InstallRoot, "rtk", "rtk.exe"));

    public void Unwire(StackConfig cfg) => UserPath.Remove(env, RtkDir(cfg));
}
