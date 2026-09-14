using System.Text;
using NyxilumLang.AST;

namespace NyxilumLang.Native;

// NativeCodegen — Фаза N1-N2 (NATIVE_ROADMAP.md): НАЙМЕНШИЙ можливий доказ,
// що NyxilumLang здатна компілюватись у СПРАВЖНІЙ x86 машинний код, а не
// лише в байткод для VM (VirtualMachine.cs), якій самій потрібна повна
// ОС/.NET під собою.
//
// Підмножина мови зараз: func main() без параметрів, var з цілими
// числами/арифметикою (+ - * / %), if/else, while, break/continue,
// присвоєння (x = ...), порівняння (== != < <= > >=), логічні (&& || !),
// print() з ОДНИМ цілим аргументом. Усі числові літерали в AST -
// `double` (Parser.cs: `double.Parse`), тут свідомо ЗРІЗАЄМО до
// 32-бітного цілого - плаваюча кома, функції з параметрами, рядки,
// масиви, структури - НАСТУПНІ фази, не ця.
//
// Виводить ТЕКСТ GAS-асемблера (AT&T-синтаксис, 32-біт) - той самий
// інструментарій (`as`/`ld`), що вже збирає ЦІЛЕ ядро NyxOS - жодного
// власного асемблера не винаходимо.
public class NativeCodegen
{
    private readonly StringBuilder _asm = new();
    private readonly Dictionary<string, int> _varOffsets = new();
    private int _nextLocalOffset; // зростає на 4 з кожною НОВОЮ змінною; offset(%ebp) - ЗАВЖДИ від'ємний
    private int _labelCounter;
    // (стартова мітка, кінцева мітка) НАЙБЛИЖЧОГО активного циклу - break
    // стрибає на кінець, continue - на старт (перевірку умови). Стек, а
    // не одна змінна - цикли можуть бути ВКЛАДЕНІ.
    private readonly Stack<(string Start, string End)> _loopLabels = new();

    public string Compile(ProgramNode program)
    {
        var mainFunc = program.Statements
            .OfType<FunctionDeclaration>()
            .FirstOrDefault(f => f.Name == "main")
            ?? throw new Exception("native codegen (Фаза N1): у файлі немає func main()");

        // РЕАЛЬНА ПОМИЛКА Фази N1, виправлена в N2: перший прохід рахував
        // ЛИШЕ ПРЯМІ var у тілі main - щойно з'явились if/while (Фаза N2),
        // var усередині ЇХНІХ блоків не отримували слот на стеку ВЗАГАЛІ
        // (компілятор впав би з KeyNotFoundException на першому ж
        // "if (x) { var y = 1 }"). Тепер збираємо імена РЕКУРСИВНО з
        // УСІХ вкладених блоків - той самий підхід, яким "справжні"
        // компілятори роблять hoisting локальних змінних.
        var allVarNames = new List<string>();
        CollectVarNames(mainFunc.Body, allVarNames);
        foreach (var name in allVarNames.Distinct())
        {
            _nextLocalOffset -= 4;
            _varOffsets[name] = _nextLocalOffset;
        }

        _asm.AppendLine("# Згенеровано NativeCodegen.cs (NyxilumLang, Фаза N1-N2) - НЕ редагувати вручну.");
        _asm.AppendLine(".section .text");
        _asm.AppendLine(".global _start");
        _asm.AppendLine("_start:");
        _asm.AppendLine("    push %ebp");
        _asm.AppendLine("    mov %esp, %ebp");
        if (allVarNames.Count > 0)
        {
            _asm.AppendLine($"    sub ${allVarNames.Distinct().Count() * 4}, %esp");
        }

        foreach (var stmt in mainFunc.Body.Statements)
        {
            CompileStatement(stmt);
        }

        // exit(0) - syscall 1 (Linux 32-біт, int 0x80), ebx=код виходу.
        _asm.AppendLine("    mov $1, %eax");
        _asm.AppendLine("    xor %ebx, %ebx");
        _asm.AppendLine("    int $0x80");

        EmitPrintIntHelper();
        EmitBssSection();

        return _asm.ToString();
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
                    // Слот УЖЕ виділено в Compile() (CollectVarNames) -
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

            case ExpressionStatement exprStmt:
                // Напр. "x = x + 1" як самостійний рядок - результат
                // виразу (значення присвоєння) просто відкидаємо.
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
                    throw new Exception($"native codegen (Фаза N1-N2): змінна '{varExpr.Name}' використана до оголошення (чи не var, а щось складніше - функції з параметрами - наступна фаза)");
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
                    // "=" - теж ВИРАЗ (не лише statement) - лишає присвоєне
                    // значення в %eax, як і в самій мові (той самий
                    // контракт, що C-подібні мови).
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
                    // Порівняння - cmp + setCC (запис 0/1 у молодший байт
                    // %al) + movzbl (обнулити решту %eax - setCC ЧІПАЄ
                    // ЛИШЕ %al, вищі 24 біти лишились би СМІТТЯМ від
                    // попередньої операції без цього).
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
