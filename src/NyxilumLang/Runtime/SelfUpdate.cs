using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace NyxilumLang.Runtime;

// nx update-self [--check] [--force] - оновити сам nx (і Nx IDE, бібліотеку,
// документацію поруч з ним) до останнього релізу NyxilumNode.
//
// Окрема програма Nx IDE (свої релізи в NyxilumIDE, свій вбудований nx)
// оновлюється тим самим механізмом:
//   nx update-self --repo Faneraiy14/NyxilumIDE --prefix NxIDE --current 1.0.0
// --repo - звідки брати релізи, --prefix - назва архіву (<prefix>-<rid>.<ext>),
// --current - встановлена версія програми (замість версії самого nx).
//
// Береться реліз з GitHub API, архів під цю платформу (linux-x64, osx-x64,
// osx-arm64, win-x64), розпаковується в тимчасову теку й копіюється поверх
// теки встановлення (там, де лежить сам nx). Запущений nx.exe Windows не
// дає перезаписати - його перейменовуємо в Nx.exe.old (прибирається при
// наступному запуску); на Linux/Mac файл замінюється перейменуванням.
// Дані користувача (~/.nyx-ide, теки з програмами) не чіпаються.
public static class SelfUpdate
{
    private const string DefaultRepo = "Faneraiy14/NyxilumNode";

    private static string? Opt(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static string? Rid()
    {
        var arch = RuntimeInformation.OSArchitecture;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return arch == Architecture.X64 ? "win-x64" : null;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return arch == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return arch == Architecture.X64 ? "linux-x64" : null;
        return null;
    }

    public static Version Current()
    {
        var v = typeof(SelfUpdate).Assembly.GetName().Version ?? new Version(0, 0, 0);
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    // Залишок попереднього оновлення на Windows.
    public static void CleanupOld()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe != null && File.Exists(exe + ".old")) File.Delete(exe + ".old");
        }
        catch { /* ще зайнятий - приберемо наступного разу */ }
    }

    public static int Run(string[] args)
    {
        bool checkOnly = args.Contains("--check");
        bool force = args.Contains("--force");
        string repo = Opt(args, "--repo") ?? DefaultRepo;
        string prefix = Opt(args, "--prefix") ?? "Nx";
        string? currentOpt = Opt(args, "--current");
        var rid = Rid();
        if (rid == null)
        {
            Console.WriteLine("Для цієї платформи готових збірок нема (є linux-x64, osx-x64, osx-arm64, win-x64).");
            return 1;
        }
        var exe = Environment.ProcessPath ?? throw new Exception("не знайдено шлях до nx");
        var dir = Path.GetDirectoryName(exe)!;

        Console.WriteLine($"Перевіряю останній реліз {repo.Split('/').Last()}...");
        JsonDocument rel;
        using (var http = NewClient())
        {
            try { rel = JsonDocument.Parse(http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest").Result); }
            catch (Exception e)
            {
                var msg = e.InnerException?.Message ?? e.Message;
                if (msg.Contains("404")) { Console.WriteLine($"У {repo} ще нема релізів."); return 0; }
                Console.WriteLine("Не вдалося зв'язатися з GitHub: " + msg);
                return 1;
            }
        }
        string tag = rel.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v'), out var latest)) { Console.WriteLine("Незрозуміла версія релізу: " + tag); return 1; }
        latest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
        var cur = Current();
        if (currentOpt != null && Version.TryParse(currentOpt.TrimStart('v'), out var co))
            cur = new Version(co.Major, co.Minor, Math.Max(0, co.Build));
        Console.WriteLine($"Встановлено: v{cur}   останній: v{latest}");
        if (latest <= cur && !force)
        {
            Console.WriteLine("Уже найновіша версія.");
            return 0;
        }
        if (checkOnly)
        {
            Console.WriteLine("Є оновлення - виконай: nx update-self");
            return 0;
        }

        string ext = rid.StartsWith("win") ? "zip" : "tar.gz";
        string want = $"{prefix}-{rid}.{ext}";
        string? url = null;
        foreach (var a in rel.RootElement.GetProperty("assets").EnumerateArray())
            if (a.GetProperty("name").GetString() == want) url = a.GetProperty("browser_download_url").GetString();
        if (url == null) { Console.WriteLine($"У релізі {tag} нема {want}"); return 1; }

        if (!CanWrite(dir))
        {
            Console.WriteLine($"Нема прав на запис у {dir}.");
            if (dir.StartsWith("/opt") || dir.StartsWith("/usr"))
                Console.WriteLine("Схоже, nx встановлено пакетом (.deb/.pkg) - постав новий пакет зі сторінки релізів або запусти з sudo.");
            return 1;
        }

        var tmp = Path.Combine(Path.GetTempPath(), "nx-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var archive = Path.Combine(tmp, want);
            Console.WriteLine($"Завантажую {want}...");
            using (var http = NewClient())
            using (var src = http.GetStreamAsync(url).Result)
            using (var dst = File.Create(archive))
                src.CopyTo(dst);

            var unpacked = Path.Combine(tmp, "new");
            Directory.CreateDirectory(unpacked);
            if (ext == "zip") ZipFile.ExtractToDirectory(archive, unpacked);
            else
            {
                using var fs = File.OpenRead(archive);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gz, unpacked, overwriteFiles: true);
            }

            string exeName = Path.GetFileName(exe);
            if (!File.Exists(Path.Combine(unpacked, exeName)))
            {
                Console.WriteLine($"В архіві нема {exeName} - оновлення скасовано.");
                return 1;
            }

            // старі ide/src, lib, extensions замінюються цілком (там нема даних користувача)
            foreach (var sub in new[] { Path.Combine("ide", "src"), Path.Combine("ide", "examples"), Path.Combine("ide", "extensions"), "lib" })
            {
                var target = Path.Combine(dir, sub);
                if (Directory.Exists(Path.Combine(unpacked, sub)) && Directory.Exists(target)) Directory.Delete(target, true);
            }
            CopyTree(unpacked, dir, exe);

            // сам nx - останнім
            var newExe = Path.Combine(unpacked, exeName);
            if (OperatingSystem.IsWindows())
            {
                if (File.Exists(exe + ".old")) File.Delete(exe + ".old");
                File.Move(exe, exe + ".old");
                File.Copy(newExe, exe);
            }
            else
            {
                var staged = exe + ".new";
                File.Copy(newExe, staged, true);
                File.SetUnixFileMode(staged, File.GetUnixFileMode(newExe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                File.Move(staged, exe, true);
            }
            Console.WriteLine($"Готово: nx оновлено до v{latest}.");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine("Оновлення не вдалося: " + (e.InnerException?.Message ?? e.Message));
            return 1;
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    private static HttpClient NewClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("nx-update-self");
        return c;
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".nx-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    // Усі файли з src у dst, крім самого nx (його - окремо, останнім).
    private static void CopyTree(string src, string dst, string exe)
    {
        foreach (var d in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, d)));
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, f);
            var target = Path.Combine(dst, rel);
            if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(f, target, true);
        }
    }
}
