using System.Linq;
using System.Text;
using NyxilumLang.AST;
using NyxilumLang.Core;
using NyxilumLang.Packages;

namespace NyxilumLang.Runtime;

// Розгортає всі "import" СТАТЕМЕНТИ рекурсивно ПЕРЕД компіляцією: читає
// імпортований .nx-файл (шлях відносно файлу, що імпортує), парсить його
// й вливає його функції/структури у спільне дерево.
//
// Два види import:
//   import "lexer.nx"   — шлях до файлу, відносно поточного каталогу
//   import "somepkg"     — ім'я пакета БЕЗ .nx: шукається в nx_modules/,
//                          з підйомом до кореня диска (як node_modules)
// Обидва підтримують вибірковий варіант: import "lexer.nx" { Token, scan }
// — вливає перелічені функції/структури/глобальні змінні РАЗОМ з усім, від
// чого вони залежать (транзитивно: функції, які вони викликають, глобальні
// змінні, структури й батьківські структури, методи структур, а також
// оголошення з модулів, які імпортує сам модуль). Відсутнє ім'я зі списку -
// помилка одразу при резолві imports, ще до компіляції.
//
// Дедуплікація - на рівні ОКРЕМИХ оголошень (за посиланням на вузол AST), а
// не цілих файлів: раніше повторний import вже відвіданого файлу пропускався
// повністю, тож `import "crypto.nx" { sha256 }` + `import "bytes.nx"
// { bytesToHex }` (crypto сам імпортує bytes) губив bytesToHex. Кожен файл
// парситься один раз (кеш), цикли імпортів розриваються.
public static class ModuleResolver
{
    private sealed class Context
    {
        public readonly Dictionary<string, List<StatementNode>> Cache = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> InProgress = new(StringComparer.OrdinalIgnoreCase);
    }

    public static ProgramNode ResolveImports(ProgramNode program, string entryFilePath)
    {
        var ctx = new Context();
        var entry = Path.GetFullPath(entryFilePath);
        ctx.InProgress.Add(entry);
        var baseDir = Path.GetDirectoryName(entry) ?? ".";
        var flat = Expand(program, baseDir, ctx);

        var merged = new ProgramNode();
        var seen = new HashSet<StatementNode>(ReferenceEqualityComparer.Instance);
        foreach (var stmt in flat)
        {
            if (seen.Add(stmt))
                merged.Statements.Add(stmt);
        }
        return merged;
    }

    // Статементи програми з розгорнутими import (вкладені - теж). Одне й те
    // саме оголошення може з'явитись кілька разів (той самий вузол AST з
    // кешу) - дублікати прибирає ResolveImports.
    private static List<StatementNode> Expand(ProgramNode program, string currentDir, Context ctx)
    {
        var result = new List<StatementNode>();
        foreach (var stmt in program.Statements)
        {
            if (stmt is not ImportStatement import)
            {
                result.Add(stmt);
                continue;
            }

            string fullPath = ResolvePath(import.Path, currentDir);
            var module = LoadModule(fullPath, import.Path, ctx);
            if (module == null)
                continue; // цикл імпортів - модуль уже розгортається вище по стеку

            result.AddRange(import.Names != null
                ? FilterByNames(module, import.Names, import.Path)
                : module);
        }
        return result;
    }

    private static List<StatementNode>? LoadModule(string fullPath, string importPath, Context ctx)
    {
        if (ctx.Cache.TryGetValue(fullPath, out var cached))
            return cached;
        if (ctx.InProgress.Contains(fullPath))
            return null;

        if (!File.Exists(fullPath))
        {
            throw new Exception(
                importPath.EndsWith(".nx")
                    ? $"Файл модуля не знайдено: {fullPath}"
                    : $"Пакет '{importPath}' не знайдено в nx_modules/ " +
                      $"(шукано від {Path.GetDirectoryName(fullPath)} до кореня диска). " +
                      $"Встанови його: nx install <owner/repo>");
        }

        ctx.InProgress.Add(fullPath);
        string code = File.ReadAllText(fullPath, Encoding.UTF8);
        var parsed = new Parser(new Lexer(code).Tokenize()).ParseProgram();
        var expanded = Expand(parsed, Path.GetDirectoryName(fullPath) ?? ".", ctx);
        ctx.InProgress.Remove(fullPath);
        ctx.Cache[fullPath] = expanded;
        return expanded;
    }

    private static string? DeclName(StatementNode stmt) => stmt switch
    {
        FunctionDeclaration f => f.Name,
        StructDeclaration s => s.Name,
        VariableDeclaration v => v.Name,
        _ => null,
    };

    // Вибірковий import "file.nx" { a, b }: перелічені імена + транзитивне
    // замикання їхніх залежностей у межах модуля (разом з тим, що модуль сам
    // імпортує). Плюс, як і раніше, будь-яка функція з іменем на "_" -
    // приватний хелпер модуля (lib/discord.nx: _dIdentify/_dHeartbeat).
    // Порядок оголошень зберігається вихідний - глобальні змінні
    // ініціалізуються в тому ж порядку, що й при повному import.
    private static List<StatementNode> FilterByNames(List<StatementNode> statements, List<string> names, string importPath)
    {
        var byName = new Dictionary<string, List<StatementNode>>(StringComparer.Ordinal);
        foreach (var stmt in statements)
        {
            var name = DeclName(stmt);
            if (name == null) continue;
            if (!byName.TryGetValue(name, out var list))
                byName[name] = list = new List<StatementNode>();
            list.Add(stmt);
        }

        var missing = names.Where(n => !byName.ContainsKey(n)).ToList();
        if (missing.Count > 0)
        {
            throw new Exception(
                $"Вибірковий import з \"{importPath}\" не знайшов: {string.Join(", ", missing)}");
        }

        var included = new HashSet<StatementNode>(ReferenceEqualityComparer.Instance);
        var visitedNames = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(names);
        foreach (var name in byName.Keys.Where(n => n.StartsWith('_')))
            queue.Enqueue(name);

        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!visitedNames.Add(name) || !byName.TryGetValue(name, out var decls))
                continue; // вбудована функція, локальна змінна тощо - не з модуля

            foreach (var decl in decls)
            {
                if (!included.Add(decl)) continue;

                var refs = new HashSet<string>(StringComparer.Ordinal);
                CollectReferences(decl, refs, new HashSet<object>(ReferenceEqualityComparer.Instance));
                foreach (var r in refs)
                    queue.Enqueue(r);

                // структура тягне свої методи, оголошені окремо як
                // "func Struct.method" (повне ім'я "Struct.method")
                if (decl is StructDeclaration)
                {
                    foreach (var methodName in byName.Keys.Where(k => k.StartsWith(name + ".", StringComparison.Ordinal)))
                        queue.Enqueue(methodName);
                }
            }
        }

        return statements.Where(included.Contains).ToList();
    }

    // Усі імена, на які посилається вузол AST: виклики функцій, змінні
    // (глобальні, або функції як значення - mapArr(x, w32)), ініціалізація
    // структур, батьківська структура. Обхід рефлексією по вузлах з
    // простору імен AST, щоб не пропустити жоден тип вузла (if/while/try/
    // лямбди/методи...) і не ламатись, коли в AST з'явиться новий.
    private static void CollectReferences(object? node, HashSet<string> refs, HashSet<object> seen)
    {
        if (node == null || node is string || !seen.Add(node))
            return;

        switch (node)
        {
            case CallExpression c: refs.Add(c.FunctionName); break;
            case VariableExpression v: refs.Add(v.Name); break;
            case StructInitExpression s: refs.Add(s.StructName); break;
            case StructDeclaration sd when sd.ParentName != null: refs.Add(sd.ParentName); break;
        }

        if (node is System.Collections.IEnumerable items)
        {
            foreach (var item in items)
                CollectReferences(item, refs, seen);
            return;
        }

        var type = node.GetType();
        if (type.Namespace != typeof(AstNode).Namespace)
            return;

        foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            var pt = prop.PropertyType;
            if (pt.IsPrimitive || pt == typeof(string) || pt.IsEnum) continue;
            CollectReferences(prop.GetValue(node), refs, seen);
        }
    }

    // ".nx" у рядку import — це шлях до конкретного файлу, як і раніше.
    // Без розширення — ім'я пакета: шукаємо через PackageManager, а не як
    // буквальний файл (тому "somepkg" не намагається відкрити файл
    // "somepkg" без розширення).
    private static string ResolvePath(string importPath, string currentDir)
    {
        if (importPath.EndsWith(".nx", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(Path.Combine(currentDir, importPath));

        var found = PackageManager.FindPackageEntry(currentDir, importPath);
        return found != null ? Path.GetFullPath(found) : Path.GetFullPath(Path.Combine(currentDir, importPath));
    }
}
