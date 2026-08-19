using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace NyxilumLang.Packages;

// ============================================================
// PackageManager — "nx install", аналог npm install, але без реєстру:
// пакет — це будь-який публічний GitHub-репозиторій з main.nx у корені.
//
// Маніфест проєкту — nx.json поруч із головним файлом:
//   { "dependencies": { "somepkg": "owner/repo" } }
//
// Пакети лягають у nx_modules/<name>/ (той самий вигляд файлів, що в
// репозиторії пакета). import "somepkg" (без .nx) резолвиться в
// nx_modules/somepkg/main.nx — див. ModuleResolver.
// ============================================================
public static class PackageManager
{
    private const string ManifestName = "nx.json";
    private const string ModulesDir = "nx_modules";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient();
        // GitHub API вимагає User-Agent для будь-яких запитів, інакше 403.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("NyxilumLang-PackageManager");
        return c;
    }

    public sealed class Manifest
    {
        public string Name { get; set; } = "";
        public Dictionary<string, string> Dependencies { get; set; } = new();
    }

    private static string ManifestPath(string projectDir) => Path.Combine(projectDir, ManifestName);

    private static Manifest LoadManifest(string projectDir)
    {
        var path = ManifestPath(projectDir);
        if (!File.Exists(path)) return new Manifest();

        var json = File.ReadAllText(path);
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<Manifest>(json, opts) ?? new Manifest();
    }

    private static void SaveManifest(string projectDir, Manifest manifest)
    {
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(ManifestPath(projectDir), JsonSerializer.Serialize(manifest, opts));
    }

    // "nx install" без аргументів: ставить усе з nx.json.
    public static void InstallAll(string projectDir)
    {
        var manifest = LoadManifest(projectDir);
        if (manifest.Dependencies.Count == 0)
        {
            Console.WriteLine($"{ManifestName} не знайдено або в ньому немає залежностей — нічого встановлювати.");
            return;
        }

        foreach (var (name, source) in manifest.Dependencies)
            InstallOne(name, source, projectDir).GetAwaiter().GetResult();

        Console.WriteLine($"Готово: {manifest.Dependencies.Count} пакет(ів).");
    }

    // "nx install owner/repo" — тягне конкретний пакет і дописує його в
    // nx.json, щоб наступний "nx install" без аргументів підхопив його теж.
    //
    // У nx.json завжди записується конкретний SHA коміта (owner/repo@<sha>),
    // навіть якщо користувач вказав гілку чи тег — інакше "nx install" без
    // аргументів щоразу тягнув би те, що ЗАРАЗ лежить на гілці, а не те, що
    // реально стояло на момент встановлення. Це захищає і від "тихої" підміни
    // коду в чужому репозиторії заднім числом (force-push/переписана гілка).
    public static void InstallSingle(string source, string projectDir)
    {
        var name = PackageNameFrom(source);
        var (ownerRepo, _) = SplitRef(source);
        var sha = InstallOne(name, source, projectDir).GetAwaiter().GetResult();

        var manifest = LoadManifest(projectDir);
        manifest.Dependencies[name] = $"{ownerRepo}@{sha}";
        SaveManifest(projectDir, manifest);

        Console.WriteLine($"Встановлено '{name}' ({ownerRepo}@{sha[..7]}), додано в {ManifestName}.");
    }

    // "nx uninstall name" — прибирає запис з nx.json і видаляє
    // nx_modules/<name>/ з диска. Не чіпає інші залежності навіть якщо
    // видалюваний пакет був єдиним, хто фактично його потребував —
    // менеджер тут навмисно найпростіший (owner/repo, без графа
    // залежностей між самими пакетами), тож "осиротілих" транзитивних
    // залежностей просто не існує як поняття.
    public static void Uninstall(string name, string projectDir)
    {
        var manifest = LoadManifest(projectDir);
        if (!manifest.Dependencies.ContainsKey(name))
        {
            Console.WriteLine($"'{name}' немає серед залежностей у {ManifestName} — нічого видаляти.");
            return;
        }

        manifest.Dependencies.Remove(name);
        SaveManifest(projectDir, manifest);

        var targetDir = Path.Combine(projectDir, ModulesDir, name);
        if (Directory.Exists(targetDir))
            Directory.Delete(targetDir, true);

        Console.WriteLine($"Видалено '{name}' з {ManifestName} і {ModulesDir}/.");
    }

    // "nx update"      — усі залежності на поточний default branch репозиторію
    // "nx update name" — лише одну
    //
    // SHA-пінінг (InstallOne) робить nx.json відтворюваним, але як наслідок
    // "nx install" без аргументів НІКОЛИ сам не підхопить новий коміт —
    // update явно перерезолвлює owner/repo (відкидаючи вже запінений SHA)
    // проти його ПОТОЧНОГО default branch і перезаписує пін на свіжий SHA.
    // Якщо колись було встановлено з конкретної гілки/тегу (owner/repo@dev),
    // update однаково піде на default branch — щоб оновитись саме в межах
    // тієї самої гілки, простіше й чесніше повторити "nx install owner/repo@dev".
    public static void UpdateAll(string projectDir)
    {
        var manifest = LoadManifest(projectDir);
        if (manifest.Dependencies.Count == 0)
        {
            Console.WriteLine($"{ManifestName} не знайдено або в ньому немає залежностей — нічого оновлювати.");
            return;
        }

        foreach (var name in manifest.Dependencies.Keys.ToList())
            UpdateOne(name, manifest, projectDir);

        SaveManifest(projectDir, manifest);
    }

    public static void UpdateSingle(string name, string projectDir)
    {
        var manifest = LoadManifest(projectDir);
        if (!manifest.Dependencies.ContainsKey(name))
        {
            Console.WriteLine($"'{name}' немає серед залежностей у {ManifestName}. Спочатку встанови: nx install owner/repo");
            return;
        }

        UpdateOne(name, manifest, projectDir);
        SaveManifest(projectDir, manifest);
    }

    private static void UpdateOne(string name, Manifest manifest, string projectDir)
    {
        var (ownerRepo, oldRef) = SplitRef(manifest.Dependencies[name]);
        var oldSha = oldRef ?? "";

        var newSha = InstallOne(name, ownerRepo, projectDir).GetAwaiter().GetResult();
        manifest.Dependencies[name] = $"{ownerRepo}@{newSha}";

        if (oldSha.Length >= 7 && oldSha[..7] == newSha[..7])
            Console.WriteLine($"  {name}: вже найновіше ({newSha[..7]})");
        else
            Console.WriteLine($"  {name}: {(oldSha.Length >= 7 ? oldSha[..7] : "?")} -> {newSha[..7]}");
    }

    private static string PackageNameFrom(string source)
    {
        // "owner/repo" -> "repo"; "owner/repo@branch" -> "repo"
        var withoutRef = source.Split('@')[0];
        var slash = withoutRef.LastIndexOf('/');
        return slash >= 0 ? withoutRef[(slash + 1)..] : withoutRef;
    }

    // Повертає SHA коміта, з якого реально встановлено пакет — викликачі
    // (InstallSingle) записують саме його в nx.json, а не вихідний ref.
    private static async Task<string> InstallOne(string name, string source, string projectDir)
    {
        var (ownerRepo, explicitRef) = SplitRef(source);
        var parts = ownerRepo.Split('/');
        if (parts.Length != 2)
            throw new Exception($"Неправильний формат залежності '{source}' — очікується owner/repo або owner/repo@ref");

        var owner = parts[0];
        var repo = parts[1];

        Console.WriteLine($"Завантаження {owner}/{repo}...");
        var ghRef = explicitRef ?? await GetDefaultBranch(owner, repo);
        var sha = await ResolveSha(owner, repo, ghRef);

        // Качаємо архів саме по SHA коміта (не по гілці) — це і є фіксація:
        // навіть якщо гілка зміниться між резолвом і завантаженням, або
        // хтось перепише історію гілки пізніше, повторний "nx install" з
        // nx.json завжди дістане той самий байт-в-байт вміст.
        var zipBytes = await Http.GetByteArrayAsync(
            $"https://github.com/{owner}/{repo}/archive/{sha}.zip");

        var targetDir = Path.Combine(projectDir, ModulesDir, name);
        if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
        Directory.CreateDirectory(targetDir);

        using var zipStream = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        // Архів GitHub завжди має один кореневий каталог виду "repo-branch/" —
        // прибираємо його, щоб файли пакета лягли прямо в nx_modules/<name>/.
        string? rootPrefix = null;
        foreach (var entry in archive.Entries)
        {
            if (rootPrefix == null)
            {
                var firstSlash = entry.FullName.IndexOf('/');
                if (firstSlash > 0) rootPrefix = entry.FullName[..(firstSlash + 1)];
            }

            if (string.IsNullOrEmpty(entry.Name)) continue; // це запис-папка, не файл

            var relative = rootPrefix != null && entry.FullName.StartsWith(rootPrefix)
                ? entry.FullName[rootPrefix.Length..]
                : entry.FullName;

            // "nx install" тягне АРХІВ ДОВІЛЬНОГО стороннього репозиторію
            // (owner/repo від будь-кого) - без цієї перевірки шкідливий
            // "пакет" міг би записом типу "../../../.bashrc" в імені файлу
            // архіву вийти за межі targetDir і переписати щось поза
            // nx_modules/. Той самий захист, що й у zipExtract() (ArchiveModule).
            var destPath = Path.GetFullPath(Path.Combine(targetDir, relative));
            if (!destPath.StartsWith(Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar))
                throw new Exception($"Підозрілий шлях у архіві пакета '{name}' ('{entry.FullName}' веде за межі {ModulesDir}/{name}) - можлива Zip Slip атака, встановлення скасовано");

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);
        }

        var entryFile = Path.Combine(targetDir, "main.nx");
        if (!File.Exists(entryFile))
            Console.WriteLine($"  Увага: у '{name}' немає main.nx у корені — import \"{name}\" не спрацює, доки він не з'явиться.");

        Console.WriteLine($"  {name} -> {targetDir} (коміт {sha[..7]})");
        return sha;
    }

    private static (string OwnerRepo, string? Ref) SplitRef(string source)
    {
        var at = source.IndexOf('@');
        return at < 0 ? (source, null) : (source[..at], source[(at + 1)..]);
    }

    private static async Task<string> GetDefaultBranch(string owner, string repo)
    {
        var json = await Http.GetStringAsync($"https://api.github.com/repos/{owner}/{repo}");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("default_branch").GetString() ?? "main";
    }

    // Приймає гілку, тег або вже готовий SHA — GitHub API в усіх випадках
    // повертає повний SHA коміта, на який це вказує саме зараз.
    private static async Task<string> ResolveSha(string owner, string repo, string gitRef)
    {
        var json = await Http.GetStringAsync(
            $"https://api.github.com/repos/{owner}/{repo}/commits/{Uri.EscapeDataString(gitRef)}");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("sha").GetString()
            ?? throw new Exception($"Не вдалося визначити SHA коміта для {owner}/{repo}@{gitRef}");
    }

    // Використовується ModuleResolver'ом для пошуку nx_modules/<name>/main.nx,
    // піднімаючись від файлу, що імпортує, до кореня диска — так само, як
    // Node.js шукає node_modules.
    public static string? FindPackageEntry(string startDir, string packageName)
    {
        var dir = startDir;
        while (true)
        {
            var candidate = Path.Combine(dir, ModulesDir, packageName, "main.nx");
            if (File.Exists(candidate)) return candidate;

            var parent = Directory.GetParent(dir);
            if (parent == null) return null;
            dir = parent.FullName;
        }
    }
}
