using System.Text;
using NyxilumLang.AST;

namespace NyxilumLang.Native;

// NativeCodegen — Фаза N1-N2 (NATIVE_ROADMAP.md): НАЙМЕНШИЙ можливий доказ,
// що NyxilumLang здатна компілюватись у СПРАВЖНІЙ x86 машинний код, а не
// лише в байткод для VM (VirtualMachine.cs), якій самій потрібна повна
// ОС/.NET під собою.
//
// Підмножина мови зараз: КІЛЬКА функцій (не лише main) з параметрами й
// return, var з цілими числами/арифметикою (+ - * / %), if/else, while,
// break/continue, присвоєння (x = ...), порівняння (== != < <= > >=),
// логічні (&& || !), print() з ОДНИМ цілим аргументом. Усі числові
// літерали в AST - `double` (Parser.cs: `double.Parse`), тут свідомо
// ЗРІЗАЄМО до 32-бітного цілого - плаваюча кома, рядки, масиви,
// структури, замикання - НАСТУПНІ фази, не ця.
//
// Виводить ТЕКСТ GAS-асемблера (AT&T-синтаксис, 32-біт) - той самий
// інструментарій (`as`/`ld`), що вже збирає ЦІЛЕ ядро NyxOS - жодного
// власного асемблера не винаходимо.
public class NativeCodegen
{
    private readonly StringBuilder _asm = new();

    // Кожна функція компілюється ОКРЕМО зі СВОЄЮ картою змінних (локальні
    // змінні однієї функції НЕ мають бачити слоти іншої) - ці три поля
    // скидаються на початку CompileFunction() для КОЖНОЇ функції.
    private Dictionary<string, int> _varOffsets = new();
    private int _nextLocalOffset;
    private string _epilogueLabel = "";
    private bool _isMain;

    private int _labelCounter;
    private readonly Stack<(string Start, string End)> _loopLabels = new();
    private HashSet<string> _knownFunctions = new();

    public string Compile(ProgramNode program)
    {
        var allFuncs = program.Statements.OfType<FunctionDeclaration>().ToList();
        var mainFunc = allFuncs.FirstOrDefault(f => f.Name == "main")
            ?? throw new Exception("native codegen (Фаза N1-N2): у файлі немає func main()");
        _knownFunctions = allFuncs.Select(f => f.Name).ToHashSet();

        _asm.AppendLine("# Згенеровано NativeCodegen.cs (NyxilumLang, Фаза N1-N2) - НЕ редагувати вручну.");
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

        EmitPrintIntHelper();
        EmitBssSection();

        return _asm.ToString();
    }

    private void CompileFunction(FunctionDeclaration func, bool isMain)
    {
        _varOffsets = new Dictionary<string, int>();
        _nextLocalOffset = 0;
        _isMain = isMain;

        // Параметри - ПОЗИТИВНІ зсуви від %ebp (за return-адресою й
        // збереженим %ebp викликаючої функції - той самий стандартний
        // cdecl-макет, яким користується GCC/будь-який x86-компілятор).
        for (int i = 0; i < func.Parameters.Count; i++)
        {
            _varOffsets[func.Parameters[i].Name] = 8 + i * 4;
        }

        var allVarNames = new List<string>();
        CollectVarNames(func.Body, allVarNames);
        foreach (var name in allVarNames.Distinct())
        {
            if (_varOffsets.ContainsKey(name)) continue; // ім'я параметра - НЕ заводимо ще й локальний слот
            _nextLocalOffset -= 4;
            _varOffsets[name] = _nextLocalOffset;
        }

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
        // виходить з кодом 0, звичайна функція повертає 0 (той самий
        // дефолт, що C - "falling off the end" неявно означає return 0).
        _asm.AppendLine("    mov $0, %eax");
        _asm.AppendLine($"{_epilogueLabel}:");
        if (isMain)
        {
            _asm.AppendLine("    mov %eax, %ebx"); // код виходу = те, що лишив return (чи 0)
            _asm.AppendLine("    mov $1, %eax");   // syscall exit
            _asm.AppendLine("    int $0x80");
        }
        else
        {
            _asm.AppendLine("    mov %ebp, %esp");
            _asm.AppendLine("    pop %ebp");
            _asm.AppendLine("    ret");
        }
    }

    private static void CollectVarNames(BlockStatement block, List<string> names)
    {
        foreach (var stmt in block.Statements)
        {
            switch (stmt)
            {
                case VariableDeclaration v:
                    names.Add(v.Name);
                    break;
                case IfStatement ifs:
                    CollectVarNames(ifs.ThenBlock, names);
                    if (ifs.ElseBlock != null) CollectVarNames(ifs.ElseBlock, names);
                    break;
                case WhileStatement ws:
                    CollectVarNames(ws.Body, names);
                    break;
                case BlockStatement b:
                    CollectVarNames(b, names);
                    break;
            }
        }
    }

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
                    // Слот УЖЕ виділено в CompileFunction() (CollectVarNames) -
                    // тут лише записуємо ПОЧАТКОВЕ значення, якщо воно є.
                    if (varDecl.Initializer != null)
                    {
                        int offset = _varOffsets[varDecl.Name];
                        CompileExpression(varDecl.Initializer); // результат -> %eax
                        _asm.AppendLine($"    mov %eax, {offset}(%ebp)");
                    }
                    break;
                }

            case PrintStatement printStmt:
                {
                    // РЕАЛЬНА ПОМИЛКА Фази N1: `print(x)` у NyxilumLang НЕ
                    // виклик функції (CallExpression) - Parser.cs розбирає
                    // його як ОКРЕМИЙ вузол AST, PrintStatement.
                    CompileExpression(printStmt.Expression); // -> %eax
                    _asm.AppendLine("    call print_int");
                    break;
                }

            case ReturnStatement returnStmt:
                {
                    if (returnStmt.Value != null)
                    {
                        CompileExpression(returnStmt.Value);
                    }
                    else
                    {
                        _asm.AppendLine("    mov $0, %eax");
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
                throw new Exception($"native codegen (Фаза N1-N2): непідтримуваний оператор - {stmt.GetType().Name} (підмножина мови ще МІНІМАЛЬНА, дивись NATIVE_ROADMAP.md)");
        }
    }

    // Результат УСІХ виразів - у %eax, за домовленістю (той самий підхід,
    // що будь-який реальний компілятор - "де лежить результат" МАЄ бути
    // однозначним правилом, а не вгадуватись з контексту).
    private void CompileExpression(ExpressionNode expr)
    {
        switch (expr)
        {
            case LiteralExpression { Value: double d }:
                // РЕАЛЬНА ПОМИЛКА, знайдена живим тестом: попередня версія
                // мовчки ОБРІЗАЛА дробову частину ((int)3.5 == 3) замість
                // чесної відмови - НЕПРАВИЛЬНИЙ результат без жодного
                // попередження гірший за явну помилку компіляції.
                // Плаваюча кома - окрема, значно більша Фаза N3
                // (NATIVE_ROADMAP.md - потрібні SSE2-інструкції/XMM-
                // регістри, окреме представлення значень) - навмисно ще
                // не тут.
                if (d != Math.Truncate(d))
                {
                    throw new Exception($"native codegen (Фаза N1-N2): дробові числа ({d}) ще не підтримуються - лише цілі (Фаза N3, дивись NATIVE_ROADMAP.md)");
                }
                _asm.AppendLine($"    mov ${(int) d}, %eax");
                break;

            // РЕАЛЬНА ПОМИЛКА, знайдена живим тестом: `true`/`false` -
            // ОКРЕМИЙ тип значення в AST (bool), НЕ double, як усі
            // числа - "while true {...}" падав на невловленому case.
            case LiteralExpression { Value: bool boolVal }:
                _asm.AppendLine($"    mov ${(boolVal ? 1 : 0)}, %eax");
                break;

            case VariableExpression varExpr:
                if (!_varOffsets.TryGetValue(varExpr.Name, out int offset))
                {
                    throw new Exception($"native codegen (Фаза N1-N2): змінна '{varExpr.Name}' використана до оголошення (масиви/структури - наступна фаза)");
                }
                _asm.AppendLine($"    mov {offset}(%ebp), %eax");
                break;

            case UnaryExpression { Operator: "-" } unaryNeg:
                CompileExpression(unaryNeg.Operand);
                _asm.AppendLine("    neg %eax");
                break;

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
                        throw new Exception($"native codegen (Фаза N1-N2): невідома функція '{call.FunctionName}' (лише вбудований print() і функції з ЦЬОГО Ж файлу - стандартна бібліотека/імпорти - значно пізніша фаза)");
                    }
                    // cdecl: аргументи - СПРАВА НАЛІВО (останній - першим),
                    // тому після всіх push'ів ПЕРШИЙ аргумент лежить
                    // НАЙБЛИЖЧЕ до вершини стека - callee побачить його
                    // РІВНО за 8(%ebp) (одразу за return-адресою й
                    // збереженим %ebp) - той самий макет, що GCC генерує.
                    for (int i = call.Arguments.Count - 1; i >= 0; i--)
                    {
                        CompileExpression(call.Arguments[i]);
                        _asm.AppendLine("    push %eax");
                    }
                    _asm.AppendLine($"    call {call.FunctionName}");
                    if (call.Arguments.Count > 0)
                    {
                        _asm.AppendLine($"    add ${call.Arguments.Count * 4}, %esp"); // ВИКЛИКАЧ прибирає аргументи (cdecl, не stdcall)
                    }
                    break;
                }

            case BinaryExpression { Operator: "=" } assign:
                {
                    if (assign.Left is not VariableExpression target)
                    {
                        throw new Exception("native codegen (Фаза N1-N2): присвоєння підтримується лише у звичайну змінну (масиви/структури - наступна фаза)");
                    }
                    if (!_varOffsets.TryGetValue(target.Name, out int targetOffset))
                    {
                        throw new Exception($"native codegen: змінна '{target.Name}' не оголошена");
                    }
                    CompileExpression(assign.Right); // -> %eax
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
                        throw new Exception($"native codegen (Фаза N1-N2): оператор '{bin.Operator}' ще не підтримується");
                }
                break;

            default:
                throw new Exception($"native codegen (Фаза N1-N2): непідтримуваний вираз - {expr.GetType().Name}");
        }
    }

    // print_int(value у %eax) - НАПИСАНО ВРУЧНУ асемблером (не C-функція,
    // яку компілятор десь бере готовою) - конвертує ЗНАКОВЕ 32-бітне ціле
    // в десяткові ASCII-цифри (в БУФЕРІ, задом наперед, бо цифри виходять
    // у зворотньому порядку - молодша спершу), потім пише через syscall
    // write(1, buf, len) напряму, БЕЗ libc/printf.
    private void EmitPrintIntHelper()
    {
        _asm.AppendLine("""
            print_int:
                push %ebp
                mov %esp, %ebp
                push %ebx
                push %ecx
                push %edx
                push %esi

                mov %eax, %esi          # зберегти оригінал (треба знати знак)
                mov $print_buf+11, %edi # писати цифри з КІНЦЯ буфера до початку
                movb $10, (%edi)        # символ переносу рядка - в самому кінці виводу
                dec %edi

                cmp $0, %esi
                jge 1f
                neg %eax                # |value| для ділення - знак додамо окремо в кінці
            1:
                mov $10, %ecx
            2:                          # цикл: eax / 10, остача - чергова цифра (з кінця)
                xor %edx, %edx
                div %ecx
                add $'0', %edx
                movb %dl, (%edi)
                dec %edi
                cmp $0, %eax
                jnz 2b

                cmp $0, %esi
                jge 3f
                movb $'-', (%edi)
                dec %edi
            3:
                inc %edi                # %edi зараз на 1 позицію РАНІШЕ першого записаного байта
                mov $print_buf+12, %eax
                sub %edi, %eax           # довжина = (кінець буфера) - (початок реального тексту)
                mov %eax, %edx           # довжина - третій аргумент write()

                mov $4, %eax             # syscall write
                mov $1, %ebx             # fd = stdout
                mov %edi, %ecx           # buf
                int $0x80

                pop %esi
                pop %edx
                pop %ecx
                pop %ebx
                pop %ebp
                ret
            """);
    }

    private void EmitBssSection()
    {
        _asm.AppendLine(".section .bss");
        _asm.AppendLine(".lcomm print_buf, 12"); // макс. 32-бітне signed int - до 11 цифр+знак, +1 \n
    }
}
