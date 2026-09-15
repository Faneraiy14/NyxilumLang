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

        if (command == "compile-native" && args.Length > 1)
        {
            string? outPath = null;
            var target = NyxilumLang.Native.NativeTarget.Linux;
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "-o" && i + 1 < args.Length) outPath = args[++i];
                else if (args[i] == "--target" && i + 1 < args.Length)
                {
                    target = args[++i] switch
                    {
                        "nyxos" => NyxilumLang.Native.NativeTarget.NyxOS,
                        "nyxos-kernel" => NyxilumLang.Native.NativeTarget.NyxOSKernel,
                        _ => NyxilumLang.Native.NativeTarget.Linux,
                    };
                }
            }
            RunCompileNative(args[1], outPath, target);
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

    // "nx compile-native <file.nx> [-o output] [--target linux|nyxos|nyxos-kernel]" —
    // Фаза N1-N7 (NATIVE_ROADMAP.md): компілює підмножину мови у СПРАВЖНІЙ
    // x86 (32-біт) машинний код через NativeCodegen.cs + `as`/`ld` (той
    // самий інструментарій, що збирає NyxOS) - НЕ через VirtualMachine.cs.
    //
    // --target linux (типово) - звичайний ELF, запускається напряму з
    // Linux-термінала (`./output`), сирі Linux-syscall'и (int 0x80), БЕЗ
    // libc.
    // --target nyxos - ПЛАСКИЙ бінарник (objcopy -O binary, БЕЗ ELF-
    // заголовків узагалі) за адресою 0xC00000 (PROC_CODE_BASE в
    // src/process.c репозиторію NyxOS) - готовий для NyxOS-команди
    // "install"/"run" (той самий формат, що programs/hello.c там),
    // NyxOS-syscall'и замість Linux - user-процес (Ring3).
    // --target nyxos-kernel (Фаза N7) - ЗОВСІМ ІНША модель: НЕ виконуваний
    // файл узагалі, а звичайний РЕЛОКОВАНИЙ .o з C-ABI-сумісними
    // символами (немає main/_start, немає syscall'ів) - призначений
    // влитись у РЕАЛЬНУ збірку ядра NyxOS (Ring0) поряд з рештою .o
    // файлів (build.sh у репозиторії NyxOS), НЕ лінкується тут узагалі.
    private static void RunCompileNative(string path, string? outPath, NyxilumLang.Native.NativeTarget target)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"Error: Cannot find file '{path}'");
            Environment.Exit(1);
            return;
        }
        outPath ??= Path.GetFileNameWithoutExtension(path);
        string asmPath = outPath + ".s";
        string objPath = outPath + ".o";

        try
        {
            string code = File.ReadAllText(path, Encoding.UTF8);
            var lexer = new Lexer(code);
            var tokens = lexer.Tokenize();
            var parser = new Parser(tokens);
            var program = parser.ParseProgram();

            var codegen = new NyxilumLang.Native.NativeCodegen();
            string asmText = codegen.Compile(program, target);
            File.WriteAllText(asmPath, asmText);
            Console.WriteLine($"Асемблер записано: {asmPath}");

            RunShell("as", $"--32 {asmPath} -o {objPath}");

            if (target == NyxilumLang.Native.NativeTarget.Linux)
            {
                RunShell("ld", $"-m elf_i386 {objPath} -o {outPath}");
                Console.WriteLine($"✅ Скомпільовано в СПРАВЖНІЙ ELF-бінарник: {outPath}");
            }
            else if (target == NyxilumLang.Native.NativeTarget.NyxOSKernel)
            {
                // Фаза N7: НЕ виконуваний файл - звичайний релокований .o
                // з C-ABI-сумісними символами (немає main/_start - жодного
                // лінкування тут НЕ робимо, `as` уже дав готовий .o,
                // призначений влитись у РЕАЛЬНУ збірку ядра поряд з
                // рештою `.o` файлів (дивись build.sh у репозиторії NyxOS)).
                Console.WriteLine($"✅ Скомпільовано в C-ABI-сумісний об'єкт для ядра NyxOS: {objPath}");
                Console.WriteLine("   Додай його до ld-виклику збірки ядра (build.sh у репозиторії NyxOS) поряд з рештою .o файлів.");
            }
            else
            {
                // Той самий підхід, що programs/user.ld у репозиторії
                // NyxOS - СЕКЦІЇ БЕЗ вирівнювання на межу сторінки
                // (стандартне `-Ttext=...` роздуло б файл нулями між
                // .text/.rodata/.bss - РЕАЛЬНИЙ БАГ, уже спійманий раніше
                // ЦІЄЮ сесією саме на programs/hello.c в NyxOS - hello.bin
                // тоді вийшов 4360 байтів замість 472 з тієї самої причини).
                string ldScriptPath = outPath + ".nyxos.ld";
                File.WriteAllText(ldScriptPath, """
                    ENTRY(_start)
                    SECTIONS
                    {
                        . = 0xC00000;
                        .text : { *(.text) }
                        .rodata : { *(.rodata*) }
                        .data : { *(.data) }
                        .bss : { *(.bss) }
                    }
                    """);
                string elfPath = outPath + ".elf";
                RunShell("ld", $"-m elf_i386 -T {ldScriptPath} -o {elfPath} {objPath}");
                RunShell("objcopy", $"-O binary {elfPath} {outPath}.bin");
                Console.WriteLine($"✅ Скомпільовано в ПЛАСКИЙ бінарник для NyxOS: {outPath}.bin");
                Console.WriteLine("   Встанови на NyxOS через пакетний менеджер (make-pkg.py у репозиторії NyxOS) чи install-hello-стиль вбудовування, потім \"run <файл>.bin\".");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Native compile error: {ex.Message}");
            Environment.Exit(1);
        }
    }

    private static void RunShell(string cmd, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(cmd, args)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            throw new Exception($"{cmd} {args} -> код {proc.ExitCode}\n{stderr}");
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
