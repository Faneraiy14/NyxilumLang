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
