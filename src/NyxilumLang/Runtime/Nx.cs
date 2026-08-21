using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using NyxilumLang.AST;
using NyxilumLang.Core;
using NyxilumLang.Compiler;
using NyxilumLang.VM;
using NyxilumLang.Tools;
using NyxilumLang.Packages;

namespace NyxilumLang.Runtime;

public class Nx
{
    public static void Main(string[] args)
    {
        // Числа в NyxilumLang (літерали, JSON, toDouble/toInt("...")) завжди
        // використовують "." як десятковий роздільник, незалежно від локалі
        // ОС. Без цього Convert.ToDouble/ToInt32 підхоплюють поточну культуру
        // ОС (напр. uk-UA використовує ","), і toDouble("0.083") падає з
        // "not in a correct format" на машинах з такою локаллю — поведінка
        // програми не має залежати від того, де її запустили.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0)
        {
            RunRepl();
            return;
        }

        string command = args[0];
        if (command == "--version" || command == "-v")
        {
            var v = typeof(Nx).Assembly.GetName().Version;
            Console.WriteLine(v != null ? $"Nx v{v.Major}.{v.Minor}.{v.Build}" : "Nx (версія невідома)");
            return;
        }

        if (command == "format" && args.Length > 1)
        {
            RunFormat(args[1]);
            return;
        }

        if (command == "lint" && args.Length > 1)
        {
            RunLint(args[1]);
            return;
        }

        if (command == "check" && args.Length > 1)
        {
            RunCheck(args[1]);
            return;
        }

        if (command == "ast" && args.Length > 1)
        {
            RunAst(args[1]);
            return;
        }

        if (command == "install")
        {
            RunInstall(args.Length > 1 ? args[1] : null);
            return;
        }

        if (command == "uninstall")
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Використання: nx uninstall <name>");
                Environment.Exit(1);
                return;
            }
            RunUninstall(args[1]);
            return;
        }

        if (command == "update")
        {
            RunUpdate(args.Length > 1 ? args[1] : null);
            return;
        }

        if (File.Exists(command))
        {
            RunFile(command);
        }
        else
        {
            Console.WriteLine($"Error: Cannot find file '{command}'");
            Environment.Exit(1);
        }
    }

    // "nx install"            — ставить усе з nx.json у поточній папці
    // "nx install owner/repo" — тягне конкретний пакет і дописує в nx.json
    private static void RunInstall(string? source)
    {
        try
        {
            var projectDir = Directory.GetCurrentDirectory();
            if (source == null) PackageManager.InstallAll(projectDir);
            else PackageManager.InstallSingle(source, projectDir);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Помилка встановлення: {ex.Message}");
            Environment.Exit(1);
        }
    }

    // "nx uninstall name" — прибирає залежність з nx.json і видаляє nx_modules/<name>/
    private static void RunUninstall(string name)
    {
        try
        {
            PackageManager.Uninstall(name, Directory.GetCurrentDirectory());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Помилка видалення: {ex.Message}");
            Environment.Exit(1);
        }
    }

    // "nx update"      — усі залежності на поточний default branch
    // "nx update name" — лише одну
    private static void RunUpdate(string? name)
    {
        try
        {
            var projectDir = Directory.GetCurrentDirectory();
            if (name == null) PackageManager.UpdateAll(projectDir);
            else PackageManager.UpdateSingle(name, projectDir);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Помилка оновлення: {ex.Message}");
            Environment.Exit(1);
        }
    }

    private static void RunFormat(string path)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"Error: Cannot find file '{path}'");
            Environment.Exit(1);
            return;
        }
        string code = File.ReadAllText(path, Encoding.UTF8);
        var formatter = new Formatter();
        Console.WriteLine(formatter.Format(code));
    }

    private static void RunLint(string path)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"Error: Cannot find file '{path}'");
            Environment.Exit(1);
            return;
        }
        string code = File.ReadAllText(path, Encoding.UTF8);
        var linter = new Linter();
        linter.Lint(code);
    }

    // "nx check файл.nx" — лише Lexer+Parser, БЕЗ Compiler і БЕЗ VM.Run().
    // На відміну від "nx файл.nx" (реально виконує код — небезпечно для
    // перевірки "на льоту" в редакторі, поки текст ще не дописаний: може
    // писати файли, лізти в мережу, зациклитись) і "nx format"/"nx lint"
    // (жоден не будує справжній AST — format лише форматує вже валідний
    // текст, lint працює по токенах поверхнево), check — єдиний спосіб
    // дізнатись "чи взагалі валідний синтаксис" без побічних ефектів.
    // Не резолвить import (ModuleResolver) — це вже семантика, не синтаксис,
    // і для незбереженого/тимчасового буфера шлях однаково не мав би сенсу.
    private static void RunCheck(string path)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"Error: Cannot find file '{path}'");
            Environment.Exit(1);
            return;
        }
        try
        {
            string code = File.ReadAllText(path, Encoding.UTF8);
            var lexer = new Lexer(code);
            var tokens = lexer.Tokenize();
            var parser = new Parser(tokens);
            parser.ParseProgram();
            Console.WriteLine("OK");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Parse Error: {ex.Message}");
            Environment.Exit(1);
        }
    }

    // "nx ast файл.nx" — той самий Lexer+Parser, що й "nx check" (без
    // Compiler/VM), але замість "OK" виводить AST у JSON за канонічною
    // схемою, яку читає anylint (github.com/Faneraiy14/anylint) через свій
    // NyxilumProvider - див. AstJsonDumper.cs, чому саме ця форма.
    private static void RunAst(string path)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"Error: Cannot find file '{path}'");
            Environment.Exit(1);
            return;
        }
        try
        {
            string code = File.ReadAllText(path, Encoding.UTF8);
            var lexer = new Lexer(code);
            var tokens = lexer.Tokenize();
            var parser = new Parser(tokens);
            var program = parser.ParseProgram();
            Console.WriteLine(NyxilumLang.Tools.AstJsonDumper.Dump(program));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Parse Error: {ex.Message}");
            Environment.Exit(1);
        }
    }

    private static void RunFile(string path)
    {
        try
        {
            string code = File.ReadAllText(path, Encoding.UTF8);
            Execute(code, path);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Runtime Error: {ex.Message}");
            // Без цього процес завершувався кодом 0 навіть після
            // необробленої помилки — жоден CI/shell-скрипт, що перевіряє
            // $?/ERRORLEVEL після запуску .nx-файлу, не міг побачити
            // провал: скрипт з throw без catch виглядав як успішний запуск.
            Environment.Exit(1);
        }
    }

    private static void RunRepl()
    {
        Console.WriteLine("Nx REPL v1.0.0");
        Console.WriteLine("Type 'exit()' to quit.");

        // Стан, що зберігається МІЖ рядками REPL:
        //  - globals: значення глобальних var з попередніх рядків — кожен
        //    новий рядок компілюється й виконується окремою VM, тож без
        //    цього "var x = 5", а тоді "print(x)" на наступному рядку
        //    давало б "Змінна 'x' не оголошена".
        //  - priorDecls: сирий текст рядків, що складаються ЛИШЕ з func/
        //    struct-оголошень — щоб функцію з одного рядка можна було
        //    викликати з наступного. Рядки зі звичайними виразами (напр.
        //    print(...)) сюди НЕ потрапляють: інакше побічний ефект
        //    (друк, запис у файл) виконувався б повторно щоразу.
        var globals = new Dictionary<string, object>();
        var priorDecls = new List<string>();

        while (true)
        {
            Console.Write("> ");
            string? line = Console.ReadLine();
            if (line == null || line == "exit()") break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var lineProgram = new Parser(new Lexer(line).Tokenize()).ParseProgram();
                bool isDeclOnly = lineProgram.Statements.Count > 0 &&
                    lineProgram.Statements.All(s => s is FunctionDeclaration || s is StructDeclaration);

                string combinedSource = string.Join("\n", priorDecls) + "\n" + line;
                var program = new Parser(new Lexer(combinedSource).Tokenize()).ParseProgram();

                var compiler = new Compiler.Compiler();
                var bytecode = compiler.Compile(program, globals.Keys);

                var vm = new VirtualMachine(bytecode, globals);
                vm.Run();

                foreach (var (name, value) in vm.Globals)
                    globals[name] = value;

                if (isDeclOnly)
                    priorDecls.Add(line);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }
    }

    private static void Execute(string code, string? sourcePath = null)
    {
        var lexer = new Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new Parser(tokens);
        var program = parser.ParseProgram();

        if (sourcePath != null)
            program = ModuleResolver.ResolveImports(program, sourcePath);

        var compiler = new Compiler.Compiler();
        var bytecode = compiler.Compile(program);

        // Опційний ліміт на кількість NyxilumLang-виділень (масиви/структури/
        // мапи) за весь запуск — захист від некерованого циклу виділень
        // у .nx-скрипті, що інакше поклав би хост-процес.
        var gcLimitEnv = Environment.GetEnvironmentVariable("NX_GC_MAX_OBJECTS");
        if (!string.IsNullOrEmpty(gcLimitEnv) && long.TryParse(gcLimitEnv, out var gcLimit))
            NxGc.Instance.SetLimit(gcLimit);

        var vm = new VirtualMachine(bytecode);
        vm.Run();
    }
}
