namespace NyxilumLang.Runtime;

// Пісочниця для випадків, коли NyxilumLang-код виконує ненадійне джерело
// (напр. NyxilumMcp — MCP-сервер, що запускає потенційно згенерований ШІ
// код). Вимкнена за замовчуванням — не ламає жоден звичний .nx-скрипт;
// вмикається прапорцем NX_SANDBOX=1 у середовищі процесу, так само, як
// NX_JIT/NX_GC_MAX_OBJECTS уже керують VM ззовні.
public static class Sandbox
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("NX_SANDBOX") == "1";

    // Файловий доступ дозволений лише всередині поточної робочої
    // директорії — блокує вихід за її межі як абсолютним шляхом
    // (/etc/passwd, ~/.ssh/id_rsa), так і відносним ("../../...").
    public static void CheckPath(string path)
    {
        if (!Enabled) return;

        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(Directory.GetCurrentDirectory());
        if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar))
            throw new Exception($"Пісочниця: доступ до файлу поза робочою директорією заборонено ('{path}')");

        // Символічне посилання (сам файл АБО будь-яка проміжна тека на
        // шляху) може вести за межі root, навіть якщо ЛЕКСИЧНИЙ шлях
        // (перевірений вище) лежить усередині — Path.GetFullPath лише
        // нормалізує текст шляху ("."/".."), symlink на диску не резолвить.
        // Живцем перевірено: "escape_link.txt" (symlink на /etc/passwd),
        // покладений усередині sandbox-теки, читав /etc/passwd безперешкодно.
        var resolved = ResolveRealPath(full);
        if (resolved != full && resolved != root && !resolved.StartsWith(root + Path.DirectorySeparatorChar))
            throw new Exception($"Пісочниця: символічне посилання веде поза робочу директорію ('{path}' → '{resolved}')");
    }

    // Той самий алгоритм, що й realpath() у libc: іде по шляху компонент
    // за компонентом від кореня, резолвлячи symlink на КОЖНОМУ рівні (не
    // лише кінцевий файл) — інакше "symlinked_dir/real_file.txt", де сама
    // symlinked_dir веде назовні, а не файл у ній, пройшов би перевірку.
    private static string ResolveRealPath(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? Path.DirectorySeparatorChar.ToString();
        var parts = fullPath.Substring(root.Length).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        var seenLinks = 0;
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            // while, не if - ланцюжок symlink (a -> b -> c) резолвиться
            // до кінця вже на цьому компоненті, а не по одному кроку на
            // кожен наступний part (де ланцюжок міг лишитись недорозв'язаним).
            while (File.Exists(current) || Directory.Exists(current))
            {
                var linkTarget = new FileInfo(current).LinkTarget;
                if (linkTarget == null) break;

                if (++seenLinks > 40) // захист від symlink-циклу (a -> b -> a)
                    throw new Exception("Пісочниця: забагато рівнів символічних посилань у шляху.");

                current = Path.IsPathRooted(linkTarget)
                    ? linkTarget
                    : Path.GetFullPath(linkTarget, Path.GetDirectoryName(current)!);
            }
        }
        return Path.GetFullPath(current);
    }

    public static void CheckNetwork()
    {
        if (Enabled)
            throw new Exception("Пісочниця: мережевий доступ заборонено (NX_SANDBOX=1)");
    }

    // Запуск зовнішніх процесів (ProcessModule.cs) — найширший з усіх
    // доступів: довільний виконуваний файл від імені хоста повністю обходить
    // і файлове, і мережеве обмеження вище. Заборонений у пісочниці завжди,
    // без винятків (на відміну від CheckPath, тут нема "дозволеної теки").
    public static void CheckProcess()
    {
        if (Enabled)
            throw new Exception("Пісочниця: запуск зовнішніх процесів заборонено (NX_SANDBOX=1)");
    }

    // guiWindow(...) (Windows Forms і X11Gui) - відкриває СПРАВЖНЄ вікно, видиме
    // на екрані користувача. Без цієї перевірки NX_SANDBOX=1 (призначений
    // саме для потенційно ШІ-згенерованого коду - напр. NyxilumMcp) не
    // заважав би такому коду малювати вікна на реальному робочому столі.
    public static void CheckGui()
    {
        if (Enabled)
            throw new Exception("Пісочниця: відкриття GUI-вікон заборонено (NX_SANDBOX=1)");
    }
}
