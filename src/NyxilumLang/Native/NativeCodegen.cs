using System.Text;
using NyxilumLang.AST;

namespace NyxilumLang.Native;

// NativeCodegen — Фаза N1 (NATIVE_ROADMAP.md): НАЙМЕНШИЙ можливий доказ,
// що NyxilumLang здатна компілюватись у СПРАВЖНІЙ x86 машинний код, а не
// лише в байткод для VM (VirtualMachine.cs), якій самій потрібна повна
// ОС/.NET під собою.
//
// Підмножина мови зараз: func main() без параметрів, var з цілими
// числами/арифметикою (+ - * /), print() з ОДНИМ цілим аргументом. Усі
// числові літерали в AST - `double` (Parser.cs: `double.Parse`), тут
// свідомо ЗРІЗАЄМО до 32-бітного цілого - плаваюча кома, if/while,
// функції з параметрами, рядки, масиви - НАСТУПНІ фази, не ця.
//
// Виводить ТЕКСТ GAS-асемблера (AT&T-синтаксис, 32-біт) - той самий
// інструментарій (`as`/`ld`), що вже збирає ЦІЛЕ ядро NyxOS - жодного
// власного асемблера не винаходимо.
public class NativeCodegen
{
    private readonly StringBuilder _asm = new();
    private readonly Dictionary<string, int> _varOffsets = new();
    private int _nextLocalOffset; // зростає на 4 з кожною НОВОЮ змінною; offset(%ebp) - ЗАВЖДИ від'ємний

    public string Compile(ProgramNode program)
    {
        var mainFunc = program.Statements
            .OfType<FunctionDeclaration>()
            .FirstOrDefault(f => f.Name == "main")
            ?? throw new Exception("native codegen (Фаза N1): у файлі немає func main()");

        // Перший прохід - порахувати, СКІЛЬКИ локальних змінних узагалі є
        // (лише прямі var у тілі main, без вкладених блоків - Фаза N1 ще
        // не підтримує if/while/вкладені блоки) - щоб виділити РІВНО
        // стільки місця на стеку заздалегідь, однією інструкцією `sub`.
        int localCount = mainFunc.Body.Statements.OfType<VariableDeclaration>().Count();

        _asm.AppendLine("# Згенеровано NativeCodegen.cs (NyxilumLang, Фаза N1) - НЕ редагувати вручну.");
        _asm.AppendLine(".section .text");
        _asm.AppendLine(".global _start");
        _asm.AppendLine("_start:");
        _asm.AppendLine("    push %ebp");
        _asm.AppendLine("    mov %esp, %ebp");
        if (localCount > 0)
        {
            _asm.AppendLine($"    sub ${localCount * 4}, %esp");
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

    private void CompileStatement(StatementNode stmt)
    {
        switch (stmt)
        {
            case VariableDeclaration varDecl:
                {
                    if (!_varOffsets.TryGetValue(varDecl.Name, out int offset))
                    {
                        _nextLocalOffset -= 4;
                        offset = _nextLocalOffset;
                        _varOffsets[varDecl.Name] = offset;
                    }
                    if (varDecl.Initializer != null)
                    {
                        CompileExpression(varDecl.Initializer); // результат -> %eax
                        _asm.AppendLine($"    mov %eax, {offset}(%ebp)");
                    }
                    break;
                }
            case PrintStatement printStmt:
                {
                    // РЕАЛЬНА ПОМИЛКА, знайдена живим тестом: `print(x)` у
                    // NyxilumLang НЕ виклик функції (CallExpression) - це
                    // ОКРЕМИЙ вузол AST, PrintStatement (Parser.cs розбирає
                    // "print" як спеціальну синтаксичну форму, не звичайний
                    // виклик). Перша версія кодогенератора шукала
                    // неіснуючий випадок і завжди падала на будь-якому
                    // print().
                    CompileExpression(printStmt.Expression); // -> %eax
                    _asm.AppendLine("    call print_int");
                    break;
                }
            default:
                throw new Exception($"native codegen (Фаза N1): непідтримуваний вираз/оператор - {stmt.GetType().Name} (підмножина мови ще МІНІМАЛЬНА, дивись NATIVE_ROADMAP.md)");
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

            case VariableExpression varExpr:
                if (!_varOffsets.TryGetValue(varExpr.Name, out int offset))
                {
                    throw new Exception($"native codegen (Фаза N1): змінна '{varExpr.Name}' використана до оголошення (чи не var, а щось складніше - функції з параметрами - наступна фаза)");
                }
                _asm.AppendLine($"    mov {offset}(%ebp), %eax");
                break;

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
                    default:
                        throw new Exception($"native codegen (Фаза N1): оператор '{bin.Operator}' ще не підтримується (лише + - * /)");
                }
                break;

            default:
                throw new Exception($"native codegen (Фаза N1): непідтримуваний вираз - {expr.GetType().Name}");
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
