using System.Text;
using NyxilumLang.AST;

namespace NyxilumLang.Native;

// Куди компілюємо - ОБИДВІ цілі використовують ТОЙ САМИЙ механізм
// (int 0x80), різні лише НОМЕРИ й КОНВЕНЦІЯ системних викликів:
//   Linux: exit=1(ebx=код), write=4(ebx=fd,ecx=buf,edx=len)
//   NyxOS: exit=0, putc=1(ebx=символ), print_string=2(ebx=NUL-
//          термінований UTF-8 вказівник), draw_pixel=3, get_ticks=4
//          (див. src/usermode.c у репозиторії NyxOS - той самий
//          контракт, що вже використовує programs/libnyx.h там).
public enum NativeTarget { Linux, NyxOS }

// Статичний тип виразу, визначений НА ЕТАПІ КОМПІЛЯЦІЇ (без цього
// компілятор не знав би, у якому регістрі шукати результат виразу -
// %xmm0 для чисел, %eax для bool/рядка - і які інструкції генерувати).
// String (Фаза N3, друга частина) - вказівник (32-біт, як і Bool) на
// NUL-термінований UTF-8 буфер: або статичний .rodata-літерал, або
// (наступний крок) купа - для ЦЬОГО кроку досить статичних літералів,
// присвоєних змінній, - купа/розподілювач пам'яті ще НЕ потрібні.
enum ValType { Number, Bool, String }

// NativeCodegen — Фази N1-N3 (NATIVE_ROADMAP.md): справжня x86-компіляція
// NyxilumLang, БЕЗ жодної VM під час виконання (на відміну від
// VirtualMachine.cs, якій самій потрібна повна ОС/.NET під собою).
//
// Підмножина мови зараз: КІЛЬКА функцій (не лише main) з параметрами й
// return, var із ЧИСЛАМИ (тепер СПРАВЖНІ double, а не обрізані до int -
// Фаза N3) і bool, арифметика (+ - * / %), if/else, while, break/continue,
// присвоєння (x = ...), порівняння (== != < <= > >=), логічні (&& || !),
// print() з числом/bool чи прямим рядковим ЛІТЕРАЛОМ. Рядки-як-змінні,
// масиви, структури, замикання - НАСТУПНІ фази, не ця.
//
// Значення: ОБИДВА типи, що зараз підтримуються, мають ОКРЕМІ регістрові
// конвенції (визначаються статично через InferExprType, а не через єдине
// динамічне представлення на кшталт tagged union - той більший крок
// свідомо відкладено, дивись NATIVE_ROADMAP.md item 8):
//   - Number (усі числові літерали й арифметика) -> %xmm0 (SSE2,
//     8-байтовий double - те саме двійкове представлення, що вже
//     використовує сама мова: усі числові літерали в AST - double,
//     Parser.cs::double.Parse).
//   - Bool -> %eax (0/1), як і раніше у Фазах N1-N2.
// Локальні змінні/параметри - УНІФІКОВАНО 8-байтові слоти на стеку
// (навіть під bool - простіше й безпечніше єдиного правила зсувів, ніж
// різна ширина слота залежно від типу).
//
// Виводить ТЕКСТ GAS-асемблера (AT&T-синтаксис, 32-біт) - той самий
// інструментарій (`as`/`ld`), що вже збирає ЦІЛЕ ядро NyxOS - жодного
// власного асемблера не винаходимо.
public class NativeCodegen
{
    private readonly StringBuilder _asm = new();

    // Кожна функція компілюється ОКРЕМО зі СВОЄЮ картою змінних (локальні
    // змінні однієї функції НЕ мають бачити слоти іншої) - ці поля
    // скидаються на початку CompileFunction() для КОЖНОЇ функції.
    private Dictionary<string, int> _varOffsets = new();
    private Dictionary<string, ValType> _varTypes = new();
    private int _nextLocalOffset;
    private string _epilogueLabel = "";
    private bool _isMain;

    private int _labelCounter;
    private readonly Stack<(string Start, string End)> _loopLabels = new();
    private HashSet<string> _knownFunctions = new();

    // Рядкові й дробові літерали - у .rodata, кожен під СВОЄЮ міткою
    // (.Lstr0/.Ldbl0, ...) - записуємо в НАКОПИЧЕНУ секцію одразу, коли
    // зустрічаємо (не окремий прохід по AST заздалегідь). Спільний
    // лічильник для обох - безпечно, бо повна мітка включає префікс.
    private readonly StringBuilder _rodata = new();
    private int _stringLabelCounter;
    private NativeTarget _target;

    public string Compile(ProgramNode program, NativeTarget target = NativeTarget.Linux)
    {
        _target = target;
        var allFuncs = program.Statements.OfType<FunctionDeclaration>().ToList();
        var mainFunc = allFuncs.FirstOrDefault(f => f.Name == "main")
            ?? throw new Exception("native codegen: у файлі немає func main()");
        _knownFunctions = allFuncs.Select(f => f.Name).ToHashSet();

        _asm.AppendLine("# Згенеровано NativeCodegen.cs (NyxilumLang, Фаза N1-N3) - НЕ редагувати вручну.");
        _asm.AppendLine(".section .text");
        _asm.AppendLine(".global _start");

        // main - ОСОБЛИВИЙ випадок: НЕ звичайна функція, викликана через
        // `call` (нікому й нема куди "повертатись" - вона ЄДИНА точка
        // входу всієї програми) - її епілог РОБИТЬ САМ SYSCALL exit(),
        // а НЕ `ret`, на відміну від УСІХ інших функцій нижче.
        CompileFunction(mainFunc, isMain: true);

        foreach (var func in allFuncs.Where(f => f.Name != "main"))
        {
            CompileFunction(func, isMain: false);
        }

        EmitPrintCharHelper();
        EmitPrintDoubleHelper();
        EmitPrintStringValueHelper();

        if (_rodata.Length > 0)
        {
            _asm.AppendLine(".section .rodata");
            _asm.AppendLine("print_newline: .byte 10");
            _asm.Append(_rodata);
        }

        EmitBssSection();

        return _asm.ToString();
    }

    private void CompileFunction(FunctionDeclaration func, bool isMain)
    {
        _varOffsets = new Dictionary<string, int>();
        _varTypes = new Dictionary<string, ValType>();
        _nextLocalOffset = 0;
        _isMain = isMain;

        // Параметри - ПОЗИТИВНІ зсуви від %ebp (за return-адресою й
        // збереженим %ebp викликаючої функції - той самий стандартний
        // cdecl-макет, яким користується GCC/будь-який x86-компілятор),
        // тепер із КРОКОМ 8 байтів (double) замість 4 - Bool-параметри
        // поки НЕ підтримуються (див. CallExpression нижче).
        for (int i = 0; i < func.Parameters.Count; i++)
        {
            _varOffsets[func.Parameters[i].Name] = 8 + i * 8;
            _varTypes[func.Parameters[i].Name] = ValType.Number;
        }

        CollectVarsAndTypes(func.Body);

        _epilogueLabel = isMain ? ".Lmain_exit" : $".L{func.Name}_epilogue";

        _asm.AppendLine(isMain ? "_start:" : $"{func.Name}:");
        _asm.AppendLine("    push %ebp");
        _asm.AppendLine("    mov %esp, %ebp");
        int localBytes = -_nextLocalOffset;
        if (localBytes > 0)
        {
            _asm.AppendLine($"    sub ${localBytes}, %esp");
        }

        foreach (var stmt in func.Body.Statements)
        {
            CompileStatement(stmt);
        }

        // Якщо тіло "провалилось" за кінець без явного return - main
        // виходить з кодом 0, звичайна функція повертає 0.0 (той самий
        // дефолт, що C - "falling off the end" неявно означає return 0);
        // pxor лише для НЕ-main, бо main використовує %eax напряму як
        // код виходу (дивись нижче), а не конвертує з %xmm0.
        _asm.AppendLine("    mov $0, %eax");
        if (!isMain)
        {
            _asm.AppendLine("    pxor %xmm0, %xmm0");
        }
        _asm.AppendLine($"{_epilogueLabel}:");
        if (isMain)
        {
            _asm.AppendLine("    mov %eax, %ebx"); // код виходу = те, що лишив return (чи 0)
            _asm.AppendLine($"    mov ${(_target == NativeTarget.Linux ? 1 : 0)}, %eax"); // syscall exit: Linux=1, NyxOS=0
            _asm.AppendLine("    int $0x80");
        }
        else
        {
            _asm.AppendLine("    mov %ebp, %esp");
            _asm.AppendLine("    pop %ebp");
            _asm.AppendLine("    ret");
        }
    }

    // Виділяє слот КОЖНІЙ локальній змінній (8 байтів - Фаза N3) і
    // ОДРАЗУ визначає її статичний тип із власного ініціалізатора -
    // працює коректно, бо мова вимагає оголошення ЗМІННОЇ ДО
    // використання, а обхід тут іде в тому ж порядку, що й виконання.
    private void CollectVarsAndTypes(BlockStatement block)
    {
        foreach (var stmt in block.Statements)
        {
            switch (stmt)
            {
                case VariableDeclaration v:
                    {
                        var type = v.Initializer != null ? InferExprType(v.Initializer) : ValType.Number;
                        _varTypes[v.Name] = type;
                        if (!_varOffsets.ContainsKey(v.Name)) // ім'я параметра - НЕ заводимо ще й локальний слот
                        {
                            _nextLocalOffset -= 8;
                            _varOffsets[v.Name] = _nextLocalOffset;
                        }
                        break;
                    }
                case IfStatement ifs:
                    CollectVarsAndTypes(ifs.ThenBlock);
                    if (ifs.ElseBlock != null) CollectVarsAndTypes(ifs.ElseBlock);
                    break;
                case WhileStatement ws:
                    CollectVarsAndTypes(ws.Body);
                    break;
                case BlockStatement b:
                    CollectVarsAndTypes(b);
                    break;
            }
        }
    }

    // Статичний тип виразу - НАЙБІЛЬШЕ архітектурне рішення Фази N3
    // (замість повноцінного динамічного представлення значень/tagged
    // union, свідомо відкладеного - дивись NATIVE_ROADMAP.md item 8):
    // визначаємо ЩЕ НА ЕТАПІ КОМПІЛЯЦІЇ, у якому регістрі шукати
    // результат кожного виразу.
    private ValType InferExprType(ExpressionNode expr) => expr switch
    {
        LiteralExpression { Value: double } => ValType.Number,
        LiteralExpression { Value: bool } => ValType.Bool,
        LiteralExpression { Value: string } => ValType.String,
        VariableExpression v => _varTypes.TryGetValue(v.Name, out var t)
            ? t
            : throw new Exception($"native codegen: змінна '{v.Name}' використана до оголошення"),
        UnaryExpression { Operator: "-" } u => InferExprType(u.Operand),
        UnaryExpression { Operator: "!" } => ValType.Bool,
        // Спрощення Фази N3: УСІ функції вважаються Number-, bool-
        // функції поки не підтримуються (дивись ReturnStatement нижче).
        CallExpression => ValType.Number,
        BinaryExpression { Operator: "=" } assign => InferExprType(assign.Right),
        BinaryExpression { Operator: "&&" or "||" } => ValType.Bool,
        BinaryExpression { Operator: "==" or "!=" or "<" or "<=" or ">" or ">=" } => ValType.Bool,
        BinaryExpression => ValType.Number, // + - * %
        _ => throw new Exception($"native codegen: неможливо визначити тип виразу - {expr.GetType().Name}")
    };

    private void CompileBlock(BlockStatement block)
    {
        foreach (var stmt in block.Statements)
        {
            CompileStatement(stmt);
        }
    }

    private void CompileStatement(StatementNode stmt)
    {
        switch (stmt)
        {
            case VariableDeclaration varDecl:
                {
                    // Слот УЖЕ виділено в CompileFunction() (CollectVarsAndTypes) -
                    // тут лише записуємо ПОЧАТКОВЕ значення, якщо воно є.
                    if (varDecl.Initializer != null)
                    {
                        int offset = _varOffsets[varDecl.Name];
                        CompileExpression(varDecl.Initializer);
                        if (_varTypes[varDecl.Name] == ValType.Number)
                            _asm.AppendLine($"    movsd %xmm0, {offset}(%ebp)");
                        else
                            _asm.AppendLine($"    mov %eax, {offset}(%ebp)");
                    }
                    break;
                }

            case PrintStatement printStmt:
                {
                    // РЕАЛЬНА ПОМИЛКА Фази N1: `print(x)` у NyxilumLang НЕ
                    // виклик функції (CallExpression) - Parser.cs розбирає
                    // його як ОКРЕМИЙ вузол AST, PrintStatement.
                    var t = InferExprType(printStmt.Expression);
                    CompileExpression(printStmt.Expression);
                    switch (t)
                    {
                        case ValType.Number:
                            _asm.AppendLine("    call print_double");
                            break;

                        case ValType.String:
                            // Рядковий ЛІТЕРАЛ і рядкова ЗМІННА йдуть ТЕПЕР
                            // ОДНИМ шляхом (Фаза N3, друга частина) -
                            // раніше прямий print("...") мав окремий
                            // спецвипадок тут (адреса й довжина відомі на
                            // етапі компіляції) - усунено, бо для
                            // print(рядкова_змінна) довжина ЗАЗДАЛЕГІДЬ НЕ
                            // відома (значення могло змінитись через
                            // присвоєння) і все одно вираховується в
                            // print_string_value у рантаймі.
                            _asm.AppendLine("    call print_string_value");
                            break;

                        default: // Bool
                            // Друкуємо ТЕКСТОМ "True"/"False" - РЕАЛЬНА
                            // невідповідність, знайдена живим тестом: VM
                            // друкує bool через C#-типове object.ToString()
                            // (boxed bool), яке дає "True"/"False" з великої
                            // літери, НЕ "true"/"false" - перша версія цього
                            // коду мовчки не збігалась із VM, поки не
                            // звірено побайтово.
                            int id = _labelCounter++;
                            _asm.AppendLine("    cmp $0, %eax");
                            _asm.AppendLine($"    je .Lpfalse{id}");
                            EmitPrintStringLiteral("True\n");
                            _asm.AppendLine($"    jmp .Lpdone{id}");
                            _asm.AppendLine($".Lpfalse{id}:");
                            EmitPrintStringLiteral("False\n");
                            _asm.AppendLine($".Lpdone{id}:");
                            break;
                    }
                    break;
                }

            case ReturnStatement returnStmt:
                {
                    if (returnStmt.Value != null)
                    {
                        var t = InferExprType(returnStmt.Value);
                        CompileExpression(returnStmt.Value); // -> %xmm0 (Number) чи %eax (Bool)
                        if (_isMain)
                        {
                            // Код виходу ЗАВЖДИ ціле число (syscall exit
                            // чекає його в %ebx) - Number-результат
                            // конвертуємо, Bool - уже готовий int.
                            if (t == ValType.Number)
                                _asm.AppendLine("    cvttsd2si %xmm0, %eax");
                        }
                        else if (t != ValType.Number)
                        {
                            throw new Exception("native codegen (Фаза N3): звичайні функції можуть повертати лише число - bool-результат поки підтримано тільки для return у main() (як код виходу 0/1)");
                        }
                    }
                    else
                    {
                        _asm.AppendLine("    mov $0, %eax");
                        if (!_isMain) _asm.AppendLine("    pxor %xmm0, %xmm0");
                    }
                    _asm.AppendLine($"    jmp {_epilogueLabel}");
                    break;
                }

            case ExpressionStatement exprStmt:
                // Напр. "x = x + 1" чи виклик функції як самостійний рядок -
                // результат виразу просто відкидаємо.
                CompileExpression(exprStmt.Expression);
                break;

            case IfStatement ifStmt:
                {
                    // РЕАЛЬНА небезпека Фази N3: якщо умова НЕ Bool, вона
                    // прийде у %xmm0 (не %eax) - `cmp $0,%eax` нижче
                    // порівняв би зі СТАРИМ сміттям у %eax і "працював"
                    // би НЕПРАВИЛЬНО без жодної помилки - тому явна
                    // перевірка типу тут, а не тихе хибне порівняння.
                    if (InferExprType(ifStmt.Condition) != ValType.Bool)
                        throw new Exception("native codegen: умова if має бути булевою (порівняння/&&/||/!) - число напряму як умова ще не підтримується");
                    int id = _labelCounter++;
                    CompileExpression(ifStmt.Condition);
                    _asm.AppendLine("    cmp $0, %eax");
                    if (ifStmt.ElseBlock != null)
                    {
                        _asm.AppendLine($"    je .Lelse{id}");
                        CompileBlock(ifStmt.ThenBlock);
                        _asm.AppendLine($"    jmp .Lendif{id}");
                        _asm.AppendLine($".Lelse{id}:");
                        CompileBlock(ifStmt.ElseBlock);
                        _asm.AppendLine($".Lendif{id}:");
                    }
                    else
                    {
                        _asm.AppendLine($"    je .Lendif{id}");
                        CompileBlock(ifStmt.ThenBlock);
                        _asm.AppendLine($".Lendif{id}:");
                    }
                    break;
                }

            case WhileStatement whileStmt:
                {
                    if (InferExprType(whileStmt.Condition) != ValType.Bool)
                        throw new Exception("native codegen: умова while має бути булевою (порівняння/&&/||/!) - число напряму як умова ще не підтримується");
                    int id = _labelCounter++;
                    string start = $".Lloopstart{id}";
                    string end = $".Lloopend{id}";
                    _loopLabels.Push((start, end));
                    _asm.AppendLine($"{start}:");
                    CompileExpression(whileStmt.Condition);
                    _asm.AppendLine("    cmp $0, %eax");
                    _asm.AppendLine($"    je {end}");
                    CompileBlock(whileStmt.Body);
                    _asm.AppendLine($"    jmp {start}");
                    _asm.AppendLine($"{end}:");
                    _loopLabels.Pop();
                    break;
                }

            case BreakStatement:
                if (_loopLabels.Count == 0)
                {
                    throw new Exception("native codegen: break поза циклом");
                }
                _asm.AppendLine($"    jmp {_loopLabels.Peek().End}");
                break;

            case ContinueStatement:
                if (_loopLabels.Count == 0)
                {
                    throw new Exception("native codegen: continue поза циклом");
                }
                _asm.AppendLine($"    jmp {_loopLabels.Peek().Start}");
                break;

            case BlockStatement block:
                CompileBlock(block);
                break;

            default:
                throw new Exception($"native codegen: непідтримуваний оператор - {stmt.GetType().Name} (підмножина мови ще МІНІМАЛЬНА, дивись NATIVE_ROADMAP.md)");
        }
    }

    // Результат виразу - у %xmm0 (Number) чи %eax (Bool), залежно від
    // статичного типу (InferExprType) - ДВІ окремі конвенції замість
    // єдиної "все в %eax" з Фаз N1-N2, бо double тепер СПРАВЖНІЙ, а не
    // обрізаний до 32-бітного цілого.
    private void CompileExpression(ExpressionNode expr)
    {
        switch (expr)
        {
            case LiteralExpression { Value: double d }:
                {
                    // РЕАЛЬНА ПОМИЛКА Фази N1-N2, ВИПРАВЛЕНА у Фазі N3:
                    // попередня версія мовчки ОБРІЗАЛА дробову частину
                    // ((int)3.5 == 3) - тепер double зберігається ТОЧНО,
                    // через SSE2/%xmm0 (те саме 8-байтове представлення,
                    // що вже використовує сама мова - Parser.cs::double.Parse).
                    string label = EmitDoubleLiteral(d);
                    _asm.AppendLine($"    movsd {label}, %xmm0");
                    break;
                }

            // `true`/`false` - ОКРЕМИЙ тип значення в AST (bool), НЕ double,
            // як усі числа - лишається в %eax (Bool-конвенція).
            case LiteralExpression { Value: bool boolVal }:
                _asm.AppendLine($"    mov ${(boolVal ? 1 : 0)}, %eax");
                break;

            case LiteralExpression { Value: string strVal }:
                {
                    // Рядок-ЗНАЧЕННЯ (Фаза N3, друга частина) - вказівник
                    // (32-біт, як Bool) на NUL-термінований .rodata-буфер.
                    // Лише СТАТИЧНІ літерали поки що - купа/розподілювач
                    // пам'яті (для рантайм-побудованих рядків) - наступний
                    // крок (Фаза N4+).
                    string label = EmitStringLiteral(strVal, nullTerminate: true);
                    _asm.AppendLine($"    mov ${label}, %eax");
                    break;
                }

            case VariableExpression varExpr:
                {
                    if (!_varOffsets.TryGetValue(varExpr.Name, out int offset))
                    {
                        throw new Exception($"native codegen: змінна '{varExpr.Name}' використана до оголошення (масиви/структури - наступна фаза)");
                    }
                    if (_varTypes[varExpr.Name] == ValType.Number)
                        _asm.AppendLine($"    movsd {offset}(%ebp), %xmm0");
                    else
                        _asm.AppendLine($"    mov {offset}(%ebp), %eax");
                    break;
                }

            case UnaryExpression { Operator: "-" } unaryNeg:
                {
                    var t = InferExprType(unaryNeg.Operand);
                    CompileExpression(unaryNeg.Operand);
                    if (t == ValType.Number)
                    {
                        // -x через SSE2: немає прямої "negate" інструкції
                        // для XMM без sign-mask константи - простіше й
                        // так само коректно порахувати 0.0 - x.
                        _asm.AppendLine("    movsd %xmm0, %xmm1");
                        _asm.AppendLine("    pxor %xmm0, %xmm0");
                        _asm.AppendLine("    subsd %xmm1, %xmm0");
                    }
                    else
                    {
                        _asm.AppendLine("    neg %eax");
                    }
                    break;
                }

            case UnaryExpression { Operator: "!" } unaryNot:
                CompileExpression(unaryNot.Operand);
                _asm.AppendLine("    cmp $0, %eax");
                _asm.AppendLine("    sete %al");
                _asm.AppendLine("    movzbl %al, %eax");
                break;

            case CallExpression call:
                {
                    if (!_knownFunctions.Contains(call.FunctionName))
                    {
                        throw new Exception($"native codegen: невідома функція '{call.FunctionName}' (лише вбудований print() і функції з ЦЬОГО Ж файлу - стандартна бібліотека/імпорти - значно пізніша фаза)");
                    }
                    // cdecl: аргументи - СПРАВА НАЛІВО (останній - першим),
                    // тому після всіх push'ів ПЕРШИЙ аргумент лежить
                    // НАЙБЛИЖЧЕ до вершини стека - callee побачить його
                    // РІВНО за 8(%ebp) (одразу за return-адресою й
                    // збереженим %ebp) - той самий макет, що GCC генерує,
                    // тепер із КРОКОМ 8 байтів (double) замість 4.
                    for (int i = call.Arguments.Count - 1; i >= 0; i--)
                    {
                        if (InferExprType(call.Arguments[i]) != ValType.Number)
                            throw new Exception("native codegen (Фаза N3): аргументи функцій підтримуються лише числові - bool-параметри ще не підтримуються");
                        CompileExpression(call.Arguments[i]); // -> %xmm0
                        _asm.AppendLine("    sub $8, %esp");
                        _asm.AppendLine("    movsd %xmm0, (%esp)");
                    }
                    _asm.AppendLine($"    call {call.FunctionName}");
                    if (call.Arguments.Count > 0)
                    {
                        _asm.AppendLine($"    add ${call.Arguments.Count * 8}, %esp"); // ВИКЛИКАЧ прибирає аргументи (cdecl, не stdcall)
                    }
                    // Результат - уже в %xmm0 (усі функції Number-, за
                    // конвенцією ReturnStatement/InferExprType вище).
                    break;
                }

            case BinaryExpression { Operator: "=" } assign:
                {
                    if (assign.Left is not VariableExpression target)
                    {
                        throw new Exception("native codegen: присвоєння підтримується лише у звичайну змінну (масиви/структури - наступна фаза)");
                    }
                    if (!_varOffsets.TryGetValue(target.Name, out int targetOffset))
                    {
                        throw new Exception($"native codegen: змінна '{target.Name}' не оголошена");
                    }
                    CompileExpression(assign.Right);
                    if (_varTypes[target.Name] == ValType.Number)
                        _asm.AppendLine($"    movsd %xmm0, {targetOffset}(%ebp)");
                    else
                        _asm.AppendLine($"    mov %eax, {targetOffset}(%ebp)");
                    break;
                }

            case BinaryExpression { Operator: "&&" } andExpr:
                {
                    int id = _labelCounter++;
                    CompileExpression(andExpr.Left);
                    _asm.AppendLine("    cmp $0, %eax");
                    _asm.AppendLine($"    je .Lfalse{id}");
                    CompileExpression(andExpr.Right);
                    _asm.AppendLine("    cmp $0, %eax");
                    _asm.AppendLine($"    je .Lfalse{id}");
                    _asm.AppendLine("    mov $1, %eax");
                    _asm.AppendLine($"    jmp .Lend{id}");
                    _asm.AppendLine($".Lfalse{id}:");
                    _asm.AppendLine("    mov $0, %eax");
                    _asm.AppendLine($".Lend{id}:");
                    break;
                }

            case BinaryExpression { Operator: "||" } orExpr:
                {
                    int id = _labelCounter++;
                    CompileExpression(orExpr.Left);
                    _asm.AppendLine("    cmp $0, %eax");
                    _asm.AppendLine($"    jne .Ltrue{id}");
                    CompileExpression(orExpr.Right);
                    _asm.AppendLine("    cmp $0, %eax");
                    _asm.AppendLine($"    jne .Ltrue{id}");
                    _asm.AppendLine("    mov $0, %eax");
                    _asm.AppendLine($"    jmp .Lend{id}");
                    _asm.AppendLine($".Ltrue{id}:");
                    _asm.AppendLine("    mov $1, %eax");
                    _asm.AppendLine($".Lend{id}:");
                    break;
                }

            case BinaryExpression bin:
                {
                    var leftType = InferExprType(bin.Left);
                    if (leftType == ValType.Number)
                    {
                        CompileNumberBinary(bin);
                    }
                    else if (leftType == ValType.String)
                    {
                        // ЖОДНИХ операцій над рядками ще не підтримуємо -
                        // навіть ==/!= НЕ додаємо тут: CompileBoolBinary
                        // порівняв би просто АДРЕСИ (identity), а НЕ ЗМІСТ
                        // (потрібен strcmp), і незрозуміло, чи це взагалі
                        // збігається з тим, як порівнює рядки VM - чесна
                        // помилка компіляції краща за неперевірену,
                        // можливо хибну поведінку (Фаза N4+).
                        throw new Exception($"native codegen (Фаза N3): оператор '{bin.Operator}' для рядків ще не підтримується (потрібне порівняння ЗМІСТУ/strcmp - Фаза N4+)");
                    }
                    else
                    {
                        CompileBoolBinary(bin);
                    }
                    break;
                }

            default:
                throw new Exception($"native codegen: непідтримуваний вираз - {expr.GetType().Name}");
        }
    }

    // Арифметика/порівняння над Number (Фаза N3, SSE2/%xmm0-%xmm1) -
    // ліве значення тимчасово ЗБЕРІГАЄМО НА СТЕКУ (8 байтів), поки
    // рахуємо праве - той самий підхід, що push/pop %eax у
    // CompileBoolBinary нижче, лише подвоєний під double.
    private void CompileNumberBinary(BinaryExpression bin)
    {
        CompileExpression(bin.Left);           // -> %xmm0
        _asm.AppendLine("    sub $8, %esp");
        _asm.AppendLine("    movsd %xmm0, (%esp)");
        CompileExpression(bin.Right);           // -> %xmm0 (праве)
        _asm.AppendLine("    movsd %xmm0, %xmm1");
        _asm.AppendLine("    movsd (%esp), %xmm0");
        _asm.AppendLine("    add $8, %esp");
        switch (bin.Operator)
        {
            case "+": _asm.AppendLine("    addsd %xmm1, %xmm0"); break;
            case "-": _asm.AppendLine("    subsd %xmm1, %xmm0"); break;
            case "*": _asm.AppendLine("    mulsd %xmm1, %xmm0"); break;
            case "/": _asm.AppendLine("    divsd %xmm1, %xmm0"); break;
            case "%":
                // fmod через a - b*trunc(a/b) - та сама "обрізана до
                // нуля" семантика, що вже дає idiv у CompileBoolBinary
                // (узгоджено з цілочисельною поведінкою Фази N2).
                _asm.AppendLine("    movsd %xmm0, %xmm2");   // a
                _asm.AppendLine("    movsd %xmm1, %xmm3");   // b
                _asm.AppendLine("    divsd %xmm1, %xmm0");   // a/b
                _asm.AppendLine("    cvttsd2si %xmm0, %eax");
                _asm.AppendLine("    cvtsi2sd %eax, %xmm0"); // trunc(a/b)
                _asm.AppendLine("    mulsd %xmm3, %xmm0");   // b*trunc(a/b)
                _asm.AppendLine("    movsd %xmm2, %xmm1");
                _asm.AppendLine("    subsd %xmm0, %xmm1");   // a - b*trunc(a/b)
                _asm.AppendLine("    movsd %xmm1, %xmm0");
                break;
            // Порівняння - результат BOOL, тому в %eax (не %xmm0), як і
            // всюди в решті компілятора. ucomisd НЕ обробляє NaN
            // коректно тут (PF-прапорець ігнорується) - відоме,
            // задокументоване обмеження цієї фази.
            case "==": _asm.AppendLine("    ucomisd %xmm1, %xmm0"); _asm.AppendLine("    sete %al"); _asm.AppendLine("    movzbl %al, %eax"); break;
            case "!=": _asm.AppendLine("    ucomisd %xmm1, %xmm0"); _asm.AppendLine("    setne %al"); _asm.AppendLine("    movzbl %al, %eax"); break;
            case "<": _asm.AppendLine("    ucomisd %xmm1, %xmm0"); _asm.AppendLine("    setb %al"); _asm.AppendLine("    movzbl %al, %eax"); break;
            case "<=": _asm.AppendLine("    ucomisd %xmm1, %xmm0"); _asm.AppendLine("    setbe %al"); _asm.AppendLine("    movzbl %al, %eax"); break;
            case ">": _asm.AppendLine("    ucomisd %xmm1, %xmm0"); _asm.AppendLine("    seta %al"); _asm.AppendLine("    movzbl %al, %eax"); break;
            case ">=": _asm.AppendLine("    ucomisd %xmm1, %xmm0"); _asm.AppendLine("    setae %al"); _asm.AppendLine("    movzbl %al, %eax"); break;
            default:
                throw new Exception($"native codegen (Фаза N3): оператор '{bin.Operator}' для чисел ще не підтримується");
        }
    }

    // Той самий цілочисельний шлях, що Фази N1-N2 (%eax/%ebx) - тепер
    // застосовується лише коли ЛІВИЙ операнд статично Bool (напр.
    // true == false) - для чисел використовується CompileNumberBinary вище.
    private void CompileBoolBinary(BinaryExpression bin)
    {
        CompileExpression(bin.Left);
        _asm.AppendLine("    push %eax");
        CompileExpression(bin.Right);
        _asm.AppendLine("    mov %eax, %ebx"); // праве значення -> ebx
        _asm.AppendLine("    pop %eax");        // ліве значення назад -> eax
        switch (bin.Operator)
        {
            case "+": _asm.AppendLine("    add %ebx, %eax"); break;
            case "-": _asm.AppendLine("    sub %ebx, %eax"); break;
            case "*": _asm.AppendLine("    imul %ebx, %eax"); break;
            case "/":
                _asm.AppendLine("    cdq"); // знакове розширення eax->edx:eax, ОБОВ'ЯЗКОВО перед idiv
                _asm.AppendLine("    idiv %ebx");
                break;
            case "%":
                _asm.AppendLine("    cdq");
                _asm.AppendLine("    idiv %ebx");
                _asm.AppendLine("    mov %edx, %eax"); // остача (edx) - результат %, а не частка
                break;
            case "==": _asm.AppendLine("    cmp %ebx, %eax"); _asm.AppendLine("    sete %al"); _asm.AppendLine("    movzbl %al, %eax"); break;
            case "!=": _asm.AppendLine("    cmp %ebx, %eax"); _asm.AppendLine("    setne %al"); _asm.AppendLine("    movzbl %al, %eax"); break;
            case "<": _asm.AppendLine("    cmp %ebx, %eax"); _asm.AppendLine("    setl %al"); _asm.AppendLine("    movzbl %al, %eax"); break;
            case "<=": _asm.AppendLine("    cmp %ebx, %eax"); _asm.AppendLine("    setle %al"); _asm.AppendLine("    movzbl %al, %eax"); break;
            case ">": _asm.AppendLine("    cmp %ebx, %eax"); _asm.AppendLine("    setg %al"); _asm.AppendLine("    movzbl %al, %eax"); break;
            case ">=": _asm.AppendLine("    cmp %ebx, %eax"); _asm.AppendLine("    setge %al"); _asm.AppendLine("    movzbl %al, %eax"); break;
            default:
                throw new Exception($"native codegen: оператор '{bin.Operator}' для bool ще не підтримується");
        }
    }

    // print_char(символ у %al) - ОДИН байт за раз, НАПИСАНО ВРУЧНУ -
    // допоміжна підпрограма для print_double нижче (цифри й крапка
    // друкуються по одному символу, а не єдиним буфером, як print_int
    // робив раніше - простіше, коли кількість символів наперед невідома).
    // Зберігає %ebx/%ecx/%edx (лише %eax руйнується, як завжди з
    // "чужими" підпрограмами) - виклики з print_double покладаються на це.
    private void EmitPrintCharHelper()
    {
        string tail = _target == NativeTarget.Linux
            ? """
                    movb %al, char_buf
                    mov $4, %eax             # syscall write
                    mov $1, %ebx             # fd = stdout
                    mov $char_buf, %ecx
                    mov $1, %edx
                    int $0x80
            """
            : """
                    movzbl %al, %ebx         # syscall putc (NyxOS, usermode.c) - символ напряму в %ebx
                    mov $1, %eax
                    int $0x80
            """;

        _asm.AppendLine($$"""
            print_char:
                push %ebp
                mov %esp, %ebp
                push %ebx
                push %ecx
                push %edx
            {{tail}}
                pop %edx
                pop %ecx
                pop %ebx
                pop %ebp
                ret
            """);
    }

    // print_double(значення у %xmm0) - НАПИСАНО ВРУЧНУ, конвертує
    // ЗНАКОВИЙ double у десятковий ASCII-текст: ціла частина - тим
    // самим "буфер із кінця" трюком, що print_int мав у Фазах N1-N2
    // (лише без вбудованого переносу рядка - друкуємо ЧЕРЕЗ print_char),
    // дробова - НАЇВНИМ множенням на 10 (коректно й ТОЧНО для дробів,
    // що рівно закінчуються у двійковому вигляді - 3.5, 1.25 і т.п.,
    // САМЕ ЦЕ живцем перевірено). ВІДОМЕ обмеження (задокументовано,
    // не приховано): для дробів БЕЗ точного двійкового представлення
    // (напр. 0.1) це НЕ дає короткий round-trip запис, який видає
    // C#/VM (Grisu/Ryu-подібні алгоритми - значно більша окрема
    // задача) - обрізаємо на 15 знаках як запобіжник, а не тихо
    // видаємо хибний результат без обмеження.
    private void EmitPrintDoubleHelper()
    {
        _asm.AppendLine("""
            print_double:
                push %ebp
                mov %esp, %ebp
                push %ebx
                push %ecx
                push %edx
                push %esi
                push %edi

                pxor %xmm2, %xmm2
                ucomisd %xmm2, %xmm0
                jae .Lpd_nonneg
                mov $'-', %al
                call print_char
                movsd %xmm0, %xmm1
                pxor %xmm0, %xmm0
                subsd %xmm1, %xmm0          # xmm0 = |xmm0|
            .Lpd_nonneg:
                cvttsd2si %xmm0, %esi       # ціла частина (обрізана до нуля) - зберігаємо, знадобиться двічі

                mov %esi, %eax
                mov $print_buf+11, %edi
                dec %edi
                mov $10, %ecx
            .Lpd_intloop:
                xor %edx, %edx
                div %ecx
                add $'0', %edx
                movb %dl, (%edi)
                dec %edi
                cmp $0, %eax
                jnz .Lpd_intloop
                inc %edi                    # %edi -> перший записаний байт цілої частини
            .Lpd_printintloop:
                cmp $print_buf+11, %edi
                jae .Lpd_printintdone
                movb (%edi), %al
                call print_char
                inc %edi
                jmp .Lpd_printintloop
            .Lpd_printintdone:

                cvtsi2sd %esi, %xmm1
                subsd %xmm1, %xmm0          # xmm0 = дробова частина (0 <= x < 1)

                pxor %xmm2, %xmm2
                ucomisd %xmm2, %xmm0
                je .Lpd_nofrac              # дробова частина точно 0 - крапку НЕ друкуємо (4.0 -> "4", як C# double.ToString())

                mov $'.', %al
                call print_char

                mov $10, %eax
                cvtsi2sd %eax, %xmm3        # xmm3 = 10.0
                mov $15, %ecx               # запобіжник - максимум 15 знаків (дивись коментар над функцією)
            .Lpd_fracloop:
                mulsd %xmm3, %xmm0
                cvttsd2si %xmm0, %edx       # чергова цифра (0-9)
                cvtsi2sd %edx, %xmm1
                subsd %xmm1, %xmm0          # залишок дробової частини
                mov %edx, %eax
                add $'0', %eax
                call print_char
                pxor %xmm2, %xmm2
                ucomisd %xmm2, %xmm0
                je .Lpd_fracdone            # залишок точно 0 - решта цифр однаково були б нулями
                dec %ecx
                jnz .Lpd_fracloop
            .Lpd_fracdone:
            .Lpd_nofrac:
                movb $10, %al               # '\n' - той самий контракт, що print(рядок)/старий print_int
                call print_char

                pop %edi
                pop %esi
                pop %edx
                pop %ecx
                pop %ebx
                pop %ebp
                ret
            """);
    }

    // print_string_value(вказівник у %eax) - друкує NUL-термінований
    // рядок-ЗНАЧЕННЯ (Фаза N3, друга частина - рядкові ЗМІННІ, не лише
    // прямий літерал). Довжина рядка НЕ відома на етапі компіляції
    // (змінна могла бути перевизначена іншим рядком іншої довжини),
    // тому Linux-ціль вираховує її В РАНТАЙМІ (strlen-цикл) перед
    // write() - на відміну від старого спецвипадку для прямого
    // print("літерал"), де довжина була відома заздалегідь. NyxOS-ціль
    // (syscall #2) довжини взагалі не потребує - сканує до NUL сама.
    // Перенесення рядка ТЕПЕР окремим print_char('\n') для ОБОХ цілей
    // (раніше NyxOS-шлях вбудовував "\n" у САМІ байти літералу - не
    // підходить для змінної, що може вказувати на РІЗНІ рядки).
    private void EmitPrintStringValueHelper()
    {
        string tail = _target == NativeTarget.Linux
            ? """
                    mov %esi, %edi           # зберігаємо початок буфера
                    mov %esi, %ecx           # курсор для strlen-циклу
                .Lpsv_strlen:
                    cmpb $0, (%ecx)
                    je .Lpsv_strlen_done
                    inc %ecx
                    jmp .Lpsv_strlen
                .Lpsv_strlen_done:
                    mov %ecx, %edx
                    sub %edi, %edx           # довжина = курсор - початок

                    mov $4, %eax             # syscall write
                    mov $1, %ebx             # fd = stdout
                    mov %edi, %ecx           # buf
                    int $0x80
            """
            : """
                    mov $2, %eax             # syscall print_string (NyxOS, usermode.c) - сама сканує до NUL
                    mov %esi, %ebx
                    int $0x80
            """;

        _asm.AppendLine($$"""
            print_string_value:
                push %ebp
                mov %esp, %ebp
                push %ebx
                push %ecx
                push %edx
                push %esi
                push %edi

                mov %eax, %esi
            {{tail}}

                movb $10, %al
                call print_char

                pop %edi
                pop %esi
                pop %edx
                pop %ecx
                pop %ebx
                pop %ebp
                ret
            """);
    }

    // Друкує ВЖЕ ВІДОМИЙ на етапі компіляції рядок (напр. "true\n") -
    // той самий цільово-залежний механізм, що прямий рядковий літерал
    // у CompileStatement вище, винесений сюди окремо для повторного
    // використання з print(bool).
    private void EmitPrintStringLiteral(string text)
    {
        if (_target == NativeTarget.Linux)
        {
            string label = EmitStringLiteral(text);
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            _asm.AppendLine("    mov $4, %eax");
            _asm.AppendLine("    mov $1, %ebx");
            _asm.AppendLine($"    mov ${label}, %ecx");
            _asm.AppendLine($"    mov ${utf8.Length}, %edx");
            _asm.AppendLine("    int $0x80");
        }
        else
        {
            string label = EmitStringLiteral(text, nullTerminate: true);
            _asm.AppendLine("    mov $2, %eax");
            _asm.AppendLine($"    mov ${label}, %ebx");
            _asm.AppendLine("    int $0x80");
        }
    }

    private void EmitBssSection()
    {
        _asm.AppendLine(".section .bss");
        _asm.AppendLine(".lcomm print_buf, 13"); // макс. 32-бітна ціла частина (обрізана з double) - до 11 цифр+знак
        _asm.AppendLine(".lcomm char_buf, 1");   // скретч-байт для print_char (лише Linux-ціль пише через нього)
    }

    // Записує UTF-8-байти рядка в .rodata як `.byte` (НЕ `.ascii "..."` -
    // уникаємо будь-яких проблем з екрануванням лапок/спецсимволів
    // ВСЕРЕДИНІ .s-файлу, і коректно обробляємо кирилицю - UTF-8 напряму
    // з C#-рядка, а не текстовий escape). Повертає МІТКУ для звернення.
    private string EmitStringLiteral(string value, bool nullTerminate = false)
    {
        string label = $".Lstr{_stringLabelCounter++}";
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        _rodata.AppendLine($"{label}:");
        var byteList = utf8.Select(b => b.ToString()).ToList();
        if (nullTerminate)
        {
            byteList.Add("0"); // NyxOS syscall #2 (vga_print) читає C-рядок до NUL - без цього читало б за межі рядка (сміття)
        }
        if (byteList.Count > 0)
        {
            _rodata.AppendLine("    .byte " + string.Join(", ", byteList));
        }
        return label;
    }

    // Записує 8-байтовий double як `.double` (GAS сам конвертує
    // десятковий текст у IEEE-754 біти - не робимо це вручну, як
    // довелось для рядків). G17 - ГАРАНТОВАНО round-trip-точний
    // десятковий запис будь-якого double (.NET-документація), інакше
    // ризикуємо втратити точність ще ДО того, як число потрапить у
    // згенерований асемблер.
    private string EmitDoubleLiteral(double value)
    {
        string label = $".Ldbl{_stringLabelCounter++}";
        _rodata.AppendLine($"{label}:");
        _rodata.AppendLine($"    .double {value.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)}");
        return label;
    }
}
