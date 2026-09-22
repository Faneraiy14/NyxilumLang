using System.Text;
using NyxilumLang.AST;

namespace NyxilumLang.Native;

// Куди компілюємо - Linux/NyxOS використовують ТОЙ САМИЙ механізм
// (int 0x80), різні лише НОМЕРИ й КОНВЕНЦІЯ системних викликів:
//   Linux: exit=1(ebx=код), write=4(ebx=fd,ecx=buf,edx=len)
//   NyxOS: exit=0, putc=1(ebx=символ), print_string=2(ebx=NUL-
//          термінований UTF-8 вказівник), draw_pixel=3, get_ticks=4
//          (див. src/usermode.c у репозиторії NyxOS - той самий
//          контракт, що вже використовує programs/libnyx.h там).
// NyxOSKernel (Фаза N7, 15.09.2026) - ЗОВСІМ ІНША модель: НЕ самостійний
// виконуваний файл (немає main/_start, немає syscall'ів - Ring0-код не
// робить syscall сам на себе), а звичайний РЕЛОКОВАНИЙ .o-об'єкт із
// C-ABI-сумісними символами (як gcc -ffreestanding компілює реальне
// ядро NyxOS - дивись NATIVE_ROADMAP.md, дослідження build.sh/kstring.c)
// - кожна функція верхнього рівня стає незалежним, викликаним ІЗ C
// символом, готовим додати до `ld`-виклику збірки ядра поряд з рештою
// `.o`.
public enum NativeTarget { Linux, NyxOS, NyxOSKernel }

// Статичний тип виразу, визначений НА ЕТАПІ КОМПІЛЯЦІЇ (без цього
// компілятор не знав би, у якому регістрі шукати результат виразу -
// %xmm0 для чисел, %eax для bool/рядка - і які інструкції генерувати).
// String (Фаза N3, друга частина) - вказівник (32-біт, як і Bool) на
// NUL-термінований UTF-8 буфер: або статичний .rodata-літерал, або
// (наступний крок) купа - для ЦЬОГО кроку досить статичних літералів,
// присвоєних змінній, - купа/розподілювач пам'яті ще НЕ потрібні.
// Array (Фаза N3, третя частина) - вказівник на купу (heap_alloc,
// власний bump-розподілювач через syscall brk - Linux-ціль лише,
// дивись коментар над HeapAlloc нижче): [довжина:4 байти][padding:4
// байти][елементи - по 8 байтів, СПРОЩЕННЯ - лише Number, не змішані
// типи, бо немає повноцінного tagged union].
// Struct (Фаза N3, четверта частина) - вказівник на купу: поля по 8
// байтів кожне, offset = індекс поля в StructDeclaration.Fields * 8
// (обчислюється ОДИН РАЗ на весь файл у Compile()). СПРОЩЕННЯ (як і в
// Array) - лише Number-поля; успадкування (extends) - ЩЕ НЕ
// підтримується цим бекендом; методи (func Struct.method) - ЗРОБЛЕНО
// (Фаза N4).
// Closure (Фаза N4) - вказівник на купу: заголовок {code_addr:4,
// env_ptr:4}. Захоплення - ЗА ЗНАЧЕННЯМ, підтверджено живим тестом
// проти VM (tests/test_closures.nx: зміна зовнішньої змінної ПІСЛЯ
// створення замикання НЕ впливає на вже створене; власний тест -
// зміна ВСЕРЕДИНІ тіла замикання теж НЕ зберігається між викликами й
// НЕ впливає на зовнішню змінну) - тому реалізовано як "скопіювати
// значення ОДИН РАЗ у момент СТВОРЕННЯ в env-блок на купі, а тоді на
// ПОЧАТКУ КОЖНОГО виклику скопіювати з env у ЗВИЧАЙНІ локальні слоти
// тіла замикання" - жодної непрямої адресації при кожному
// читанні/записі не треба, захоплена змінна для решти тіла - просто
// ще один локальний слот. СПРОЩЕННЯ: замикання можна СТВОРИТИ й
// ВИКЛИКАТИ через локальну змінну (var f = func(...){...}; f(...)),
// але НЕ передати як аргумент і НЕ повернути з функції - для цього
// знадобився б статичний вивід типу РЕЗУЛЬТАТУ виклику будь-якої
// функції, а не універсальне припущення "усі функції - Number", яке
// InferExprType(CallExpression) досі робить (Фаза N4+, майбутнє).
// Int32/UInt32 (Фаза N12, 22.09.2026) - ЛИШЕ --target nyxos-kernel,
// той самий принцип, що peek/poke/asm/портовий I/O (Фаза N8-N11):
// ДОДАНО поруч з double-based Number, ніколи не замінюючи його -
// справжні 32-бітні GPR-значення (не double з латками), з коректною
// сигнатурою (signed/unsigned) для /, %, порівнянь, >>. Дивись
// EmitDoubleToUInt32/EmitUInt32ToDouble нижче для межі з Number-світом
// (toI32/toU32/toNumber-функції, а НЕ неявна конвертація).
enum ValType { Number, Bool, String, Array, Struct, Closure, Int32, UInt32 }

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
    // ЛИШЕ для ValType.Struct - яка САМЕ структура (StructDeclaration.Name),
    // щоб знати offset'и полів при member-доступі. НЕ скидається між
    // функціями окремо (скидається разом із _varTypes в CompileFunction).
    private Dictionary<string, string> _varStructName = new();
    // try/catch (Фаза N4) - для КОЖНОГО TryStatement-вузла в ЦІЙ функції
    // (за посиланням на сам AST-вузол, не за іменем - їх немає) offset
    // 16-байтового "кадру обробника" (setjmp/longjmp-стиль, дивись
    // коментар над CompileTryStatement). _tryDepth - чи компілюємо
    // ЗАРАЗ щось УСЕРЕДИНІ try/catch-блоку (для чесної заборони
    // return/break/continue звідти - дивись коментар нижче).
    private Dictionary<TryStatement, int> _tryFrameOffsets = new();
    private int _tryDepth;
    private int _nextLocalOffset;
    private string _epilogueLabel = "";
    private bool _isMain;
    // Чи компілюємо ЗАРАЗ функцію для --target nyxos-kernel (Фаза N7) -
    // впливає на конвенцію return (int у %eax через C ABI, а не
    // xmm0/exit-syscall) у ReturnStatement нижче.
    private bool _isKernelExport;

    // Глобальні змінні верхнього рівня (Фаза N7, лише --target
    // nyxos-kernel - справжні kernel-модулі, як gconsole.c, тримають
    // стан МІЖ викликами функцій - той самий сенс, що static-змінні в
    // C). ІМ'Я -> тип; мітка в асемблері - завжди "__g_{ім'я}". НЕ
    // скидається між функціями (на відміну від _varOffsets) -
    // заповнюється ОДИН РАЗ у CompileKernelObject.
    private readonly Dictionary<string, ValType> _globalVars = new();
    private readonly StringBuilder _globalData = new();

    // Заповнюється ОДИН РАЗ на весь файл у Compile() (НЕ скидається між
    // функціями, на відміну від _varOffsets/_varTypes) - структури
    // оголошуються на верхньому рівні, доступні звідусіль.
    private Dictionary<string, Dictionary<string, int>> _structFieldOffsets = new();

    // structName -> methodName -> оголошення (Фаза N4). Асемблерна мітка
    // методу - завжди "{structName}__{methodName}" (уникає колізій між
    // однойменними методами РІЗНИХ структур, напр. Dog.speak() і
    // Cat.speak()).
    private Dictionary<string, Dictionary<string, FunctionDeclaration>> _structMethods = new();

    // Лямбди (Фаза N4) - виявляються ЛІНИВО, під час компіляції виразів
    // (CompileExpression{FunctionExpression}), і ставляться в ЧЕРГУ на
    // компіляцію тіла ПІЗНІШЕ (не можна компілювати тіло лямбди ПРЯМО
    // ПОСЕРЕД тіла функції, що її створює - зламало б .text-структуру
    // поточної функції, що компілюється). Черга, а не єдиний прохід,
    // бо тіло ОДНІЄЇ лямбди може містити ЩЕ ОДНУ вкладену лямбду.
    private readonly List<(string Label, FunctionExpression Expr, List<string> FreeVars)> _pendingLambdas = new();
    private int _lambdaCounter;

    private int _labelCounter;
    private readonly Stack<(string Start, string End)> _loopLabels = new();
    private HashSet<string> _knownFunctions = new();

    // Фаза N8.5 (18.09.2026): kernel-target функції можуть ОГОЛОСИТИ тип
    // результату явно (func f(...) -> string {...}) - Parser.cs це вже
    // давно парсить у FunctionDeclaration.ReturnType, але NativeCodegen
    // досі це поле НІКОЛИ не читав і завжди вважав будь-яку відому
    // функцію Number-результатом (та сама межа, що вже впиралась у
    // kheap.c - kmalloc-подібна функція не могла повернути покажчик).
    // Заповнюється ОДИН РАЗ у CompileKernelObject - лише для kernel-target
    // (звичайні функції й так СТРОГО Number, дивись ReturnStatement).
    private Dictionary<string, FunctionDeclaration> _kernelFuncsByName = new();
    // Яку kernel-функцію зараз компілюємо - потрібно в ReturnStatement,
    // щоб звірити РЕАЛЬНИЙ тип значення з явною анотацією (якщо вона є).
    private FunctionDeclaration? _currentKernelFunc;

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
        _knownFunctions = allFuncs.Select(f => f.Name).ToHashSet();

        // Фаза N7: --target nyxos-kernel - ЗОВСІМ ІНША модель (немає
        // main/_start, немає syscall'ів) - повністю окрема гілка, а не
        // ще один if усередині коду нижче, розрахованого на "один
        // виконуваний файл з main()".
        if (target == NativeTarget.NyxOSKernel)
        {
            return CompileKernelObject(program, allFuncs);
        }

        var mainFunc = allFuncs.FirstOrDefault(f => f.Name == "main")
            ?? throw new Exception("native codegen: у файлі немає func main()");

        // РЕАЛЬНА ПОМИЛКА (знайдена живим тестом 16.09.2026): Parser.cs
        // ParseFunctionDeclaration парсить "func Struct.method(...)"
        // ОДНАКОВО незалежно від того, чи ця декларація лежить УСЕРЕДИНІ
        // тіла struct {...} (тоді вона потрапляє в StructDeclaration.
        // Methods), чи як ЗВИЧАЙНА декларація верхнього рівня (тоді вона
        // просто один з program.Statements з крапкою в
        // FunctionDeclaration.Name - StructDeclaration.Methods про неї
        // НІЧОГО не знає). Compiler.cs (VM-шлях) реєструє функції за
        // ПОВНИМ іменем незалежно від джерела, тому обидва синтаксиси
        // там працюють однаково - а тут (нативний бекенд) методи
        // збирались ЛИШЕ з s.Methods, тому метод, оголошений поза тілом
        // структури, ніколи не потрапляв у _structMethods і виклик давав
        // "структура не має методу", хоча метод був.
        var topLevelMethodFuncs = allFuncs.Where(f => f.Name.Contains('.')).ToList();

        // Offset'и полів структур і карта методів - ОДИН РАЗ на весь файл
        // (структури оголошуються на верхньому рівні, а не всередині
        // функцій). Успадковані (extends) поля/методи батька НЕ
        // додаються сюди - спроба звернутись до успадкованого дасть
        // чесну (хай і не найточнішу) помилку "структура не має поля/методу".
        foreach (var s in program.Statements.OfType<StructDeclaration>())
        {
            var offsets = new Dictionary<string, int>();
            for (int i = 0; i < s.Fields.Count; i++)
                offsets[s.Fields[i].Name] = i * 8;
            _structFieldOffsets[s.Name] = offsets;

            // Ключ у methodMap мусить бути "голим" іменем (те, що прийде
            // в MethodCallExpression.MethodName) - FunctionDeclaration.Name
            // зберігає ПОВНЕ "StructName.methodName" в обох джерелах нижче.
            var methodMap = new Dictionary<string, FunctionDeclaration>();
            foreach (var m in s.Methods)
            {
                string bareName = m.Name.Contains('.') ? m.Name[(m.Name.LastIndexOf('.') + 1)..] : m.Name;
                methodMap[bareName] = m;
            }
            foreach (var m in topLevelMethodFuncs)
            {
                string ownerName = m.Name[..m.Name.LastIndexOf('.')];
                if (ownerName != s.Name) continue;
                string bareName = m.Name[(m.Name.LastIndexOf('.') + 1)..];
                methodMap[bareName] = m;
            }
            _structMethods[s.Name] = methodMap;
        }

        _asm.AppendLine("# Згенеровано NativeCodegen.cs (NyxilumLang, Фаза N1-N4) - НЕ редагувати вручну.");
        _asm.AppendLine(".section .text");
        _asm.AppendLine(".global _start");

        // main - ОСОБЛИВИЙ випадок: НЕ звичайна функція, викликана через
        // `call` (нікому й нема куди "повертатись" - вона ЄДИНА точка
        // входу всієї програми) - її епілог РОБИТЬ САМ SYSCALL exit(),
        // а НЕ `ret`, на відміну від УСІХ інших функцій нижче.
        CompileFunction(mainFunc, isMain: true);

        // Функції-методи (ім'я з крапкою, "StructName.methodName") НЕ
        // компілюються тут як звичайні функції - вони йдуть нижче, під
        // міткою "StructName__methodName", через _structMethods (яка вже
        // об'єднала обидва синтаксиси реєстрації методу вище).
        foreach (var func in allFuncs.Where(f => f.Name != "main" && !f.Name.Contains('.')))
        {
            CompileFunction(func, isMain: false);
        }

        // Методи структур (Фаза N4) - КОЖЕН під власною, УНІКАЛЬНОЮ
        // міткою "StructName__methodName" (labelOverride), інакше
        // однойменні методи РІЗНИХ структур (напр. Dog.speak() і
        // Cat.speak()) зіткнулися б в одній .text-мітці. Ітеруємо вже
        // зібрану _structMethods (а не s.Methods напряму), щоб методи,
        // оголошені ПОЗА тілом структури, теж скомпілювались.
        foreach (var s in program.Statements.OfType<StructDeclaration>())
        {
            foreach (var (bareName, m) in _structMethods[s.Name])
            {
                CompileFunction(m, isMain: false, labelOverride: $"{s.Name}__{bareName}");
            }
        }

        // Лямбди (Фаза N4) - чергу заповнюють CompileFunction() виклики
        // вище (main/функції/методи), а КОЖНА скомпільована лямбда сама
        // може додати в чергу ЩЕ - тому цикл, не один прохід, поки
        // черга не спорожніє.
        while (_pendingLambdas.Count > 0)
        {
            var (label, fnExpr, freeVars) = _pendingLambdas[0];
            _pendingLambdas.RemoveAt(0);
            CompileLambda(label, fnExpr, freeVars);
        }

        EmitPrintCharHelper();
        EmitPrintDoubleHelper();
        EmitPrintStringValueHelper();
        EmitHeapAllocHelper();
        EmitUnhandledThrowHelper();

        if (_rodata.Length > 0)
        {
            _asm.AppendLine(".section .rodata");
            _asm.AppendLine("print_newline: .byte 10");
            _asm.Append(_rodata);
        }

        EmitBssSection();

        return _asm.ToString();
    }

    private void CompileFunction(FunctionDeclaration func, bool isMain, string? labelOverride = null)
    {
        _varOffsets = new Dictionary<string, int>();
        _varTypes = new Dictionary<string, ValType>();
        _varStructName = new Dictionary<string, string>();
        _tryFrameOffsets = new Dictionary<TryStatement, int>();
        _tryDepth = 0;
        _nextLocalOffset = 0;
        _isMain = isMain;

        // Параметри - ПОЗИТИВНІ зсуви від %ebp (за return-адресою й
        // збереженим %ebp викликаючої функції - той самий стандартний
        // cdecl-макет, яким користується GCC/будь-який x86-компілятор),
        // тепер із КРОКОМ 8 байтів (double) замість 4 - Bool-параметри
        // поки НЕ підтримуються (див. CallExpression нижче). Параметр
        // (включно з неявним self у методах структур - Фаза N4) типу
        // ВІДОМОЇ структури - ValType.Struct/вказівник, а не Number
        // (передається так само, як self у MethodCallExpression нижче -
        // сирий 32-бітний вказівник у нижніх 4 байтах 8-байтового слота).
        for (int i = 0; i < func.Parameters.Count; i++)
        {
            var param = func.Parameters[i];
            _varOffsets[param.Name] = 8 + i * 8;
            if (_structFieldOffsets.ContainsKey(param.Type))
            {
                _varTypes[param.Name] = ValType.Struct;
                _varStructName[param.Name] = param.Type;
            }
            else
            {
                _varTypes[param.Name] = ValType.Number;
            }
        }

        CollectVarsAndTypes(func.Body);

        string entryLabel = labelOverride ?? func.Name;
        _epilogueLabel = isMain ? ".Lmain_exit" : $".L{entryLabel}_epilogue";

        _asm.AppendLine(isMain ? "_start:" : $"{entryLabel}:");
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

    // Фаза N7 (--target nyxos-kernel): весь файл - ОДИН релокований
    // .o з C-ABI-сумісними символами, БЕЗ main/_start, БЕЗ syscall'ів.
    // СПРОЩЕННЯ ПЕРШОГО кроку (доказ концепції на kstring.c-подібних
    // модулях - лише чиста логіка з покажчиками): жодних структур/
    // масивів/замикань/try-catch/print() тут - усі вони або
    // потребували б heap_alloc чи syscall'ів (яких немає сенсу
    // включати в об'єкт, призначений влитись у РЕАЛЬНЕ ядро), або
    // (try/catch) самі по собі безпечні, але їхній helper
    // "неперехопленого throw" робить syscall exit(), що в Ring0
    // взагалі не має сенсу - тому й try/catch поки що виключено, а не
    // лише heap-залежні речі.
    private string CompileKernelObject(ProgramNode program, List<FunctionDeclaration> allFuncs)
    {
        if (allFuncs.Count == 0)
            throw new Exception("native codegen (Фаза N7): у файлі немає жодної функції для --target nyxos-kernel");

        _kernelFuncsByName = allFuncs.ToDictionary(f => f.Name);

        // Глобальні змінні верхнього рівня (потрібно для модулів зі
        // станом, напр. gconsole.c - grid/col/row/fg_color/bg_color
        // живуть МІЖ викликами функцій, той самий сенс, що C static).
        // СПРОЩЕННЯ: ініціалізатор (якщо є) МАЄ бути сталим літералом -
        // kernel-ціль НЕ має "точки входу", що виконала б довільний
        // код ІНІЦІАЛІЗАЦІЇ перед першим викликом будь-якої функції
        // (на відміну від main()-цілей) - складніша логіка (виклик
        // функції як ініціалізатор тощо) МАЄ жити У ЗВИЧАЙНІЙ функції,
        // як і в самому gconsole.c (fg_color присвоюється ВСЕРЕДИНІ
        // gconsole_init(), а НЕ в оголошенні).
        foreach (var v in program.Statements.OfType<VariableDeclaration>())
        {
            ValType type;
            if (v.Initializer == null)
            {
                // Без ініціалізатора тип раніше завжди був Number - та
                // сама прогалина, що колись була з ReturnType функцій
                // (Parser.cs давно парсить `var x: string`, кодоген просто
                // не читав TypeAnnotation). Потрібно для полів-вказівників
                // зі станом "ще нічого немає" (напр. kheap.c
                // heap_first_block: string - справжній нульовий вказівник,
                // а не Number 0.0/double).
                type = v.TypeAnnotation switch
                {
                    null => ValType.Number,
                    "string" => ValType.String,
                    "bool" => ValType.Bool,
                    // Фаза N12 (22.09.2026) - "i32"/"u32" ЛИШАЮТЬСЯ
                    // синонімами Number (як і завжди були) - НОВІ,
                    // однозначні імена "int32"/"uint32" для справжніх
                    // цілих типів, щоб не міняти поведінку вже наявного
                    // коду, що використовує i32/u32 як псевдоніми числа.
                    "int32" => ValType.Int32,
                    "uint32" => ValType.UInt32,
                    "any" or "i32" or "f64" or "int" or "number" or "size_t" or "u32" or "usize" => ValType.Number,
                    _ => throw new Exception($"native codegen (Фаза N8.5): тип глобальної '{v.Name}' ('{v.TypeAnnotation}') не підтримується для --target nyxos-kernel - лише string/bool/int32/uint32/число")
                };
            }
            else if (v.Initializer is LiteralExpression { Value: double } && v.TypeAnnotation == "int32")
            {
                // "var x: int32 = 5;" - анотація МАЄ перемогти голе
                // InferExprType нижче (яке для double-літерала завжди
                // дало б Number) - інакше анотація тут мовчки
                // ігнорувалась би, коли є ініціалізатор.
                type = ValType.Int32;
            }
            else if (v.Initializer is LiteralExpression { Value: double } && v.TypeAnnotation == "uint32")
            {
                type = ValType.UInt32;
            }
            else if (v.Initializer is LiteralExpression { Value: double or bool or string })
            {
                type = InferExprType(v.Initializer);
            }
            else
            {
                throw new Exception($"native codegen (Фаза N7): ініціалізатор глобальної '{v.Name}' має бути сталим літералом (число/bool/рядок) - складнішу логіку виконайте ВСЕРЕДИНІ якоїсь функції, не в оголошенні (kernel-ціль не має точки входу)");
            }
            _globalVars[v.Name] = type;

            string label = $"__g_{v.Name}";
            _globalData.AppendLine($"{label}:");
            switch (type)
            {
                case ValType.Number:
                    double dv = v.Initializer is LiteralExpression { Value: double d } ? d : 0.0;
                    _globalData.AppendLine($"    .double {dv.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)}");
                    break;
                case ValType.Bool:
                    bool bv = v.Initializer is LiteralExpression { Value: bool b } && b;
                    _globalData.AppendLine($"    .long {(bv ? 1 : 0)}");
                    break;
                case ValType.String:
                    if (v.Initializer == null)
                    {
                        // Немає ініціалізатора -> СПРАВЖНІЙ нульовий
                        // вказівник (адреса 0), а НЕ вказівник на порожній
                        // рядок - інакше `p == 0`-перевірка (Фаза N8) на
                        // такій глобальній завжди була б хибною.
                        _globalData.AppendLine("    .long 0");
                    }
                    else
                    {
                        string sv = v.Initializer is LiteralExpression { Value: string s } ? s : "";
                        string strLabel = EmitStringLiteral(sv, nullTerminate: true);
                        _globalData.AppendLine($"    .long {strLabel}");
                    }
                    break;
                case ValType.Int32:
                case ValType.UInt32:
                    // Немає ініціалізатора -> 0 (як "string" -> null
                    // вище). З ініціалізатором - лише double-літерал,
                    // обрізаний до 32-бітного бітового патерна ТУТ, на
                    // етапі компіляції (unchecked - той самий принцип, що
                    // й double<->int32 конвертація в рантаймі, лише
                    // виконана заздалегідь для константи).
                    int i32v = v.Initializer is LiteralExpression { Value: double d32 } ? unchecked((int)(long)d32) : 0;
                    _globalData.AppendLine($"    .long {i32v}");
                    break;
                default:
                    throw new Exception($"native codegen (Фаза N7): непідтримуваний тип глобальної '{v.Name}'");
            }
        }

        _asm.AppendLine("# Згенеровано NativeCodegen.cs (NyxilumLang, Фаза N7 - C-ABI kernel-об'єкт) - НЕ редагувати вручну.");
        _asm.AppendLine(".section .text");
        foreach (var func in allFuncs)
        {
            CompileKernelFunction(func);
        }

        if (_rodata.Length > 0)
        {
            _asm.AppendLine(".section .rodata");
            _asm.Append(_rodata);
        }

        if (_globalData.Length > 0)
        {
            _asm.AppendLine(".section .data");
            _asm.Append(_globalData);
        }

        return _asm.ToString();
    }

    // Компілює ОДНУ функцію верхнього рівня як незалежний, C-ABI-
    // сумісний символ - той самий макет, що GCC генерує для
    // `size_t k_strlen(const char* s)` тощо. КЛЮЧОВА відмінність від
    // CompileFunction/CompileLambda: параметри - 4-БАЙТОВІ cdecl-слоти
    // (НЕ 8-байтові Number-слоти, якими компілятор користується
    // всюди-інде), а результат - ЦІЛЕ ЧИСЛО в %eax (НЕ %xmm0), як і
    // очікує звичайний C-виклик. Рядкові параметри - вже готовий
    // покажчик (String і так завжди char*-сумісний, конвертація не
    // потрібна). Числові параметри КОНВЕРТУЮТЬСЯ з C int у Number/
    // double РІВНО ОДИН РАЗ на вході (і назад - у ReturnStatement) -
    // РЕШТА ТІЛА далі компілюється ЗВИЧАЙНИМ, уже перевіреним шляхом
    // без жодного нового коду (той самий "перетворити на межі, а
    // всередині - як завжди" підхід, що self/env-вказівник в
    // методах/замиканнях).
    private void CompileKernelFunction(FunctionDeclaration func)
    {
        _varOffsets = new Dictionary<string, int>();
        _varTypes = new Dictionary<string, ValType>();
        _varStructName = new Dictionary<string, string>();
        _tryFrameOffsets = new Dictionary<TryStatement, int>();
        _tryDepth = 0;
        _nextLocalOffset = 0;
        _isMain = false;
        _isKernelExport = true;
        _currentKernelFunc = func;

        var numericParams = new List<(string Name, int CabiOffset)>();
        for (int i = 0; i < func.Parameters.Count; i++)
        {
            var param = func.Parameters[i];
            int cabiOffset = 8 + i * 4; // C ABI: 4 байти на параметр, НЕ 8
            if (param.Type == "string")
            {
                _varOffsets[param.Name] = cabiOffset; // сирий покажчик - без конвертації
                _varTypes[param.Name] = ValType.String;
            }
            else if (param.Type == "int32" || param.Type == "uint32")
            {
                // Фаза N12: C-ABI параметр УЖЕ рівно 4-байтове сире
                // значення - те саме, що string-параметр (сирий
                // покажчик), жодної double-конвертації не треба.
                _varOffsets[param.Name] = cabiOffset;
                _varTypes[param.Name] = param.Type == "int32" ? ValType.Int32 : ValType.UInt32;
            }
            else
            {
                if (param.Type is not ("any" or "i32" or "f64" or "int" or "number" or "size_t" or "u32" or "usize"))
                    throw new Exception($"native codegen (Фаза N7): kernel-параметр '{param.Name}' типу '{param.Type}' не підтримується - лише string, int32/uint32 чи числові типи");
                numericParams.Add((param.Name, cabiOffset));
                _nextLocalOffset -= 8;
                _varOffsets[param.Name] = _nextLocalOffset;
                _varTypes[param.Name] = ValType.Number;
            }
        }

        CollectVarsAndTypes(func.Body);

        _epilogueLabel = $".L{func.Name}_epilogue";
        _asm.AppendLine($".global {func.Name}");
        _asm.AppendLine($"{func.Name}:");
        _asm.AppendLine("    push %ebp");
        _asm.AppendLine("    mov %esp, %ebp");
        int localBytes = -_nextLocalOffset;
        if (localBytes > 0)
        {
            _asm.AppendLine($"    sub ${localBytes}, %esp");
        }

        // Числові C-ABI параметри (4-байтовий int) -> звичайні Number-
        // локалі (8-байтовий double) - ОДИН РАЗ на вході.
        foreach (var (name, cabiOffset) in numericParams)
        {
            _asm.AppendLine($"    mov {cabiOffset}(%ebp), %eax");
            _asm.AppendLine("    cvtsi2sd %eax, %xmm0");
            _asm.AppendLine($"    movsd %xmm0, {_varOffsets[name]}(%ebp)");
        }

        foreach (var stmt in func.Body.Statements)
        {
            CompileStatement(stmt);
        }

        _asm.AppendLine("    mov $0, %eax"); // дефолт при "провалі" за кінець без явного return (як C - falling off the end)
        _asm.AppendLine($"{_epilogueLabel}:");
        _asm.AppendLine("    mov %ebp, %esp");
        _asm.AppendLine("    pop %ebp");
        _asm.AppendLine("    ret");

        _isKernelExport = false;
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
                        if (type == ValType.Struct && v.Initializer is StructInitExpression si)
                        {
                            _varStructName[v.Name] = si.StructName;
                        }
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
                case TryStatement ts:
                    {
                        // Кадр обробника (Фаза N4, setjmp/longjmp-стиль) -
                        // 16 анонімних байтів на КОЖЕН try (не за іменем -
                        // за самим AST-вузлом, дивись _tryFrameOffsets).
                        _nextLocalOffset -= 16;
                        _tryFrameOffsets[ts] = _nextLocalOffset;

                        // catch-змінна - СПРОЩЕННЯ: завжди Number (throw
                        // підтримує лише числові значення - дивись
                        // ThrowStatement нижче).
                        if (!_varOffsets.ContainsKey(ts.CatchVariableName))
                        {
                            _nextLocalOffset -= 8;
                            _varOffsets[ts.CatchVariableName] = _nextLocalOffset;
                        }
                        _varTypes[ts.CatchVariableName] = ValType.Number;

                        CollectVarsAndTypes(ts.TryBlock);
                        CollectVarsAndTypes(ts.CatchBlock);
                        break;
                    }
                case BlockStatement b:
                    CollectVarsAndTypes(b);
                    break;
            }
        }
    }

    // Компілює ТІЛО лямбди (Фаза N4) - викликається з черги
    // _pendingLambdas у Compile(), НЕ напряму з CompileExpression (де
    // лямбда лише СТВОРЮЄТЬСЯ, а не виконується). Неявний env-вказівник
    // - ЗАВЖДИ перший параметр (16(%ebp) далі - явні параметри лямбди),
    // навіть для лямбд БЕЗ захоплень (уніфікована конвенція виклику -
    // виклик боку НЕ знає заздалегідь, чи ця КОНКРЕТНА лямбда щось
    // захопила). Захоплені змінні копіюються З env У ЗВИЧАЙНІ локальні
    // слоти ОДИН РАЗ на початку тіла (дивись коментар над
    // ValType.Closure - чому це коректно відтворює by-value семантику
    // VM) - решта тіла компілюється як завжди, без жодної спеціальної
    // обробки captured-імен.
    private void CompileLambda(string label, FunctionExpression fnExpr, List<string> freeVars)
    {
        _varOffsets = new Dictionary<string, int>();
        _varTypes = new Dictionary<string, ValType>();
        _varStructName = new Dictionary<string, string>();
        _tryFrameOffsets = new Dictionary<TryStatement, int>();
        _tryDepth = 0;
        _nextLocalOffset = 0;
        _isMain = false;

        for (int i = 0; i < fnExpr.Parameters.Count; i++)
        {
            _varOffsets[fnExpr.Parameters[i].Name] = 16 + i * 8; // +8 через неявний env-параметр
            _varTypes[fnExpr.Parameters[i].Name] = ValType.Number; // СПРОЩЕННЯ: лише числові параметри лямбд
        }

        var capturedOffsets = new Dictionary<string, int>();
        foreach (var fv in freeVars)
        {
            _nextLocalOffset -= 8;
            _varOffsets[fv] = _nextLocalOffset;
            _varTypes[fv] = ValType.Number;
            capturedOffsets[fv] = _nextLocalOffset;
        }

        CollectVarsAndTypes(fnExpr.Body); // звичайні var-оголошення ВСЕРЕДИНІ тіла - слоти ПІСЛЯ захоплених

        _epilogueLabel = $".L{label.TrimStart('.')}_epilogue";
        _asm.AppendLine($"{label}:");
        _asm.AppendLine("    push %ebp");
        _asm.AppendLine("    mov %esp, %ebp");
        int localBytes = -_nextLocalOffset;
        if (localBytes > 0)
        {
            _asm.AppendLine($"    sub ${localBytes}, %esp");
        }

        if (freeVars.Count > 0)
        {
            _asm.AppendLine("    mov 8(%ebp), %eax"); // неявний env-вказівник
            for (int i = 0; i < freeVars.Count; i++)
            {
                _asm.AppendLine($"    movsd {i * 8}(%eax), %xmm0");
                _asm.AppendLine($"    movsd %xmm0, {capturedOffsets[freeVars[i]]}(%ebp)");
            }
        }

        foreach (var stmt in fnExpr.Body.Statements)
        {
            CompileStatement(stmt);
        }

        _asm.AppendLine("    pxor %xmm0, %xmm0"); // дефолт при "провалі" за кінець - лямбди повертають лише Number
        _asm.AppendLine($"{_epilogueLabel}:");
        _asm.AppendLine("    mov %ebp, %esp");
        _asm.AppendLine("    pop %ebp");
        _asm.AppendLine("    ret");
    }

    // Вільні змінні лямбди - усі VariableExpression-посилання в тілі,
    // що НЕ є ні власним параметром, ні власною локальною змінною
    // (оголошеною ВСЕРЕДИНІ тіла). СПРОЩЕННЯ: НЕ рекурсує у тіло
    // ВКЛАДЕНОЇ лямбди (її власні захоплення обробляються окремо, коли
    // компілюється ВОНА САМА, використовуючи БЕЗПОСЕРЕДНЬО охоплюючий
    // контекст, у якому і captured-змінні зовнішньої лямбди - на той
    // момент уже ЗВИЧАЙНІ локальні слоти - дивись коментар над
    // ValType.Closure) - багаторівневе вкладення тому "просто працює"
    // без додаткового коду.
    private List<string> FindFreeVars(FunctionExpression fn)
    {
        var bound = new HashSet<string>(fn.Parameters.Select(p => p.Name));
        var free = new List<string>();
        var seen = new HashSet<string>();
        CollectFreeVarsInBlock(fn.Body, bound, free, seen);
        return free;
    }

    private void CollectFreeVarsInBlock(BlockStatement block, HashSet<string> bound, List<string> free, HashSet<string> seen)
    {
        foreach (var stmt in block.Statements)
        {
            CollectFreeVarsInStmt(stmt, bound, free, seen);
        }
    }

    private void CollectFreeVarsInStmt(StatementNode stmt, HashSet<string> bound, List<string> free, HashSet<string> seen)
    {
        switch (stmt)
        {
            case VariableDeclaration v:
                if (v.Initializer != null) CollectFreeVarsInExpr(v.Initializer, bound, free, seen);
                bound.Add(v.Name); // ПІСЛЯ ініціалізатора - "var x = x" мало б означати ЗОВНІШНІЙ x
                break;
            case PrintStatement p:
                CollectFreeVarsInExpr(p.Expression, bound, free, seen);
                break;
            case ReturnStatement r:
                if (r.Value != null) CollectFreeVarsInExpr(r.Value, bound, free, seen);
                break;
            case ExpressionStatement e:
                CollectFreeVarsInExpr(e.Expression, bound, free, seen);
                break;
            case IfStatement ifs:
                CollectFreeVarsInExpr(ifs.Condition, bound, free, seen);
                CollectFreeVarsInBlock(ifs.ThenBlock, bound, free, seen);
                if (ifs.ElseBlock != null) CollectFreeVarsInBlock(ifs.ElseBlock, bound, free, seen);
                break;
            case WhileStatement ws:
                CollectFreeVarsInExpr(ws.Condition, bound, free, seen);
                CollectFreeVarsInBlock(ws.Body, bound, free, seen);
                break;
            case BlockStatement b:
                CollectFreeVarsInBlock(b, bound, free, seen);
                break;
        }
    }

    private void CollectFreeVarsInExpr(ExpressionNode expr, HashSet<string> bound, List<string> free, HashSet<string> seen)
    {
        switch (expr)
        {
            case VariableExpression v:
                if (!bound.Contains(v.Name) && seen.Add(v.Name)) free.Add(v.Name);
                break;
            case BinaryExpression b:
                CollectFreeVarsInExpr(b.Left, bound, free, seen);
                CollectFreeVarsInExpr(b.Right, bound, free, seen);
                break;
            case UnaryExpression u:
                CollectFreeVarsInExpr(u.Operand, bound, free, seen);
                break;
            case CallExpression c:
                foreach (var a in c.Arguments) CollectFreeVarsInExpr(a, bound, free, seen);
                break;
            case CallValueExpression cv:
                CollectFreeVarsInExpr(cv.Callee, bound, free, seen);
                foreach (var a in cv.Arguments) CollectFreeVarsInExpr(a, bound, free, seen);
                break;
            case IndexExpression ix:
                CollectFreeVarsInExpr(ix.Array, bound, free, seen);
                CollectFreeVarsInExpr(ix.Index, bound, free, seen);
                break;
            case MemberAccessExpression m:
                CollectFreeVarsInExpr(m.Object, bound, free, seen);
                break;
            case MethodCallExpression mc:
                CollectFreeVarsInExpr(mc.Object, bound, free, seen);
                foreach (var a in mc.Arguments) CollectFreeVarsInExpr(a, bound, free, seen);
                break;
            case ArrayLiteralExpression al:
                foreach (var e in al.Elements) CollectFreeVarsInExpr(e, bound, free, seen);
                break;
            case StructInitExpression si:
                foreach (var f in si.Fields) CollectFreeVarsInExpr(f.Value, bound, free, seen);
                break;
            case FunctionExpression nested:
                {
                    // РЕАЛЬНА ПОМИЛКА, знайдена живим тестом: вкладена
                    // лямбда, якій потрібна змінна з "дідівської" (не
                    // безпосередньо охоплюючої) області - ЗОВНІШНЯ
                    // лямбда мусить ТЕЖ захопити цю змінну (щоб
                    // передати її далі через звичайний локальний слот -
                    // дивись коментар над ValType.Closure), інакше на
                    // момент компіляції ВКЛАДЕНОЇ лямбди цієї змінної в
                    // _varTypes просто НЕ буде. Тому - рекурсія в
                    // ВЛАСНІ вільні змінні вкладеної лямбди (мінус те,
                    // що вона сама зв'язує), а НЕ повний обхід її тіла -
                    // стандартний алгоритм "closure conversion".
                    var nestedFree = FindFreeVars(nested);
                    foreach (var nf in nestedFree)
                    {
                        if (!bound.Contains(nf) && seen.Add(nf)) free.Add(nf);
                    }
                    break;
                }
            // LiteralExpression - немає вільних змінних.
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
            : _globalVars.TryGetValue(v.Name, out var gt)
                ? gt // Фаза N7: глобальна (лише --target nyxos-kernel) - локальна/параметр ЗАВЖДИ затіняє
                : _target == NativeTarget.NyxOSKernel && _knownFunctions.Contains(v.Name)
                    // Фаза N8.5c (18.09.2026): "гола" назва функції (БЕЗ
                    // виклику) - її АДРЕСА, потрібна для реєстрації
                    // callback'ів у зовнішньому C-коді ядра (напр.
                    // pci_scan(callback), isr_register_handler(vec, fn)) -
                    // те саме String/вказівник, що й будь-який інший
                    // покажчик у kernel-цілі.
                    ? ValType.String
                    : throw new Exception($"native codegen: змінна '{v.Name}' використана до оголошення"),
        UnaryExpression { Operator: "-" } u => InferExprType(u.Operand),
        UnaryExpression { Operator: "!" } => ValType.Bool,
        // Фаза N12: '~' на Int32/UInt32-операнді лишається тим самим
        // типом (той самий принцип, що унарний '-' вище) - лише "гола"
        // Number-версія (без реальних цілих типів) типізується як
        // Number, як і раніше.
        UnaryExpression { Operator: "~" } uTilde => InferExprType(uTilde.Operand) is ValType.Int32 or ValType.UInt32
            ? InferExprType(uTilde.Operand)
            : ValType.Number,
        // peek32/poke32 (Фаза N8, 16.09.2026) - ВБУДОВАНІ інтринзики
        // компілятора (НЕ зовнішні символи ядра - лінкер про них НІЧОГО
        // не знає, компілятор сам вставляє сирі mov-інструкції), тому
        // перевіряються ПЕРШИМИ, ще ДО загального "невідоме ім'я -
        // зовнішня функція" припущення нижче. peekPtr читає покажчик
        // (String), peekNum/pokeNum/pokePtr - прості числа/покажчики.
        CallExpression { FunctionName: "peekPtr" or "numToPtr" } when _target == NativeTarget.NyxOSKernel => ValType.String,
        CallExpression { FunctionName: "peekNum" or "pokeNum" or "pokePtr" } when _target == NativeTarget.NyxOSKernel => ValType.Number,
        // Портовий ввід-вивід (Фаза N8.5d) - ті самі компілятор-
        // інтринзики, що peek/poke, завжди Number (порт/значення - і
        // так лише 8/16/32-бітні цілі, тут це double).
        CallExpression { FunctionName: "inb" or "inw" or "inl" or "outb" or "outw" or "outl" } when _target == NativeTarget.NyxOSKernel => ValType.Number,
        // hlt() (Фаза N8.5e, 18.09.2026) - той самий клас інтринзика, що
        // порти вище: без аргументів, "повертає" 0 (результат ніколи не
        // використовується - викликається лише заради побічного ефекту,
        // сну CPU до наступного переривання). Потрібен для auth.c-
        // подібного коду (while (!line_ready) { __asm__("hlt"); }).
        CallExpression { FunctionName: "hlt" } when _target == NativeTarget.NyxOSKernel => ValType.Number,
        // asm(...) (Фаза N11) - той самий клас, що hlt()/inb/outb: завжди
        // Number, результат ніколи реально не використовується.
        CallExpression { FunctionName: "asm" } when _target == NativeTarget.NyxOSKernel => ValType.Number,
        // toI32/toU32/toNumber (Фаза N12) - явні конвертери на МЕЖІ між
        // Number-світом (double) і справжніми Int32/UInt32 (GPR) - той
        // самий "convert once at the boundary" принцип, що numToPtr вище
        // (НЕ неявна конвертація десь усередині виразу).
        CallExpression { FunctionName: "toI32" } when _target == NativeTarget.NyxOSKernel => ValType.Int32,
        CallExpression { FunctionName: "toU32" } when _target == NativeTarget.NyxOSKernel => ValType.UInt32,
        CallExpression { FunctionName: "toNumber" } when _target == NativeTarget.NyxOSKernel => ValType.Number,
        // Спрощення Фази N3: УСІ функції вважаються Number-, bool-
        // функції поки не підтримуються (дивись ReturnStatement нижче).
        // ВИНЯТОК (Фаза N7): відомі ЗОВНІШНІ примітиви ядра NyxOS (НЕ
        // функції з ЦЬОГО файлу - реальні C-функції з kheap.c/gfx.c/
        // vga_font.c, лінкер резолвить сам) - дивись ExternalKernelReturnType.
        CallExpression callExpr when _target == NativeTarget.NyxOSKernel && !_knownFunctions.Contains(callExpr.FunctionName)
            => ExternalKernelReturnType(callExpr.FunctionName),
        // Фаза N8.5: виклик ВЛАСНОЇ (.nx) kernel-функції з явною анотацією
        // результату (func f(...) -> string {...}) - дивись
        // KernelReturnTypeFromAnnotation. Без анотації - той самий
        // Number, що й завжди.
        CallExpression callExpr2 when _target == NativeTarget.NyxOSKernel && _kernelFuncsByName.TryGetValue(callExpr2.FunctionName, out var calledDecl)
            => KernelReturnTypeFromAnnotation(calledDecl),
        CallExpression => ValType.Number,
        BinaryExpression { Operator: "=" } assign => InferExprType(assign.Right),
        BinaryExpression { Operator: "&&" or "||" } => ValType.Bool,
        BinaryExpression { Operator: "==" or "!=" or "<" or "<=" or ">" or ">=" } => ValType.Bool,
        // Фаза N8.5: InferExprType не знав, що "ptr + N"/"ptr - N" (Фаза
        // N8, арифметика вказівників) дає String - сама КОМПІЛЯЦІЯ це
        // вже вміла (дивись CompileExpression/BinaryExpression нижче),
        // але тип-вивід досі мовчки казав Number для БУДЬ-ЯКОГО +/-,
        // тому напр. "func f(p: string) -> string { return p + 1 }"
        // падало б з хибним "оголошено -> string, але return дає
        // Number" - знайдено ЖИВИМ тестом на самому фіксі N8.5 вище.
        BinaryExpression { Operator: "+" or "-" } ptrArith when _target == NativeTarget.NyxOSKernel
            && InferExprType(ptrArith.Left) == ValType.String && InferExprType(ptrArith.Right) == ValType.Number
            => ValType.String,
        // Фаза N12: арифметика/побітові над Int32/UInt32-лівим операндом
        // лишаються ТИМ САМИМ типом (не "занижуються" назад до Number) -
        // саме ЦЕ дозволяє ланцюжок "a + b - c" лишатись у GPR-світі без
        // жодного double-round-trip на кожному кроці. Порівняння вище
        // (== < > і т.д.) вже коректно завжди Bool незалежно від типу
        // операндів.
        BinaryExpression intBin when InferExprType(intBin.Left) is ValType.Int32 or ValType.UInt32
            => InferExprType(intBin.Left),
        BinaryExpression => ValType.Number, // + - * %
        ArrayLiteralExpression => ValType.Array,
        // СПРОЩЕННЯ: масиви лише з Number-елементів (Фаза N3) - інакше
        // довелось би вирішувати проблему змішаних типів без
        // повноцінного tagged union.
        IndexExpression => ValType.Number,
        StructInitExpression => ValType.Struct,
        // СПРОЩЕННЯ: поля структур лише Number (як і елементи масивів).
        MemberAccessExpression => ValType.Number,
        // Методи (Фаза N4), як і звичайні функції, повертають лише Number.
        MethodCallExpression => ValType.Number,
        FunctionExpression => ValType.Closure,
        _ => throw new Exception($"native codegen: неможливо визначити тип виразу - {expr.GetType().Name}")
    };

    // Яка САМЕ структура (StructDeclaration.Name) стоїть за виразом -
    // потрібно окремо від InferExprType (яка каже лише "це Struct",
    // без деталей), щоб знайти offset потрібного поля. СПРОЩЕННЯ:
    // підтримано лише звичайну змінну й прямий StructName{...} -
    // ланцюжки (obj.inner.field) чи повернення структури з функції -
    // Фаза N4+.
    private string ResolveStructName(ExpressionNode expr) => expr switch
    {
        VariableExpression v => _varStructName.TryGetValue(v.Name, out var sn)
            ? sn
            : throw new Exception($"native codegen: '{v.Name}' не є структурою"),
        StructInitExpression si => si.StructName,
        _ => throw new Exception($"native codegen (Фаза N3): доступ до поля підтримується лише через змінну чи StructName{{...}} напряму, не через {expr.GetType().Name} (Фаза N4+)")
    };

    // Тип РЕЗУЛЬТАТУ відомих зовнішніх примітивів ядра NyxOS (Фаза N7) -
    // НЕ функцій з ЦЬОГО файлу, а справжніх C-функцій з kheap.c/gfx.c/
    // vga_font.c тощо, чиї .o лінкер підключить сам, коли результат
    // цього компілятора влиється в реальну збірку ядра. За
    // ЗАМОВЧУВАННЯМ (тут відсутні) - Number, що покриває більшість
    // (gfx_width/height/rgb, cyrillic_codepoint_to_byte тощо). Лише ті,
    // що ПОВЕРТАЮТЬ ВКАЗІВНИК чи bool, потребують явного запису тут -
    // інакше InferExprType дав би Number, і, наприклад,
    // `!gfx_is_available()` читав би СМІТТЯ з %eax (результат справді
    // лежав би в %xmm0, конвертований як double, а не bool в %eax).
    private static readonly Dictionary<string, ValType> ExternalKernelReturnTypes = new()
    {
        ["kmalloc"] = ValType.String,          // kheap.c - void* -> вказівник
        ["gfx_is_available"] = ValType.Bool,    // gfx.c - int, семантично bool (0/1)
        ["scheduler_tick"] = ValType.String,    // process.c - повертає СИРУ адресу ESP
                                                 // (uint32_t, що isr.s підставляє як
                                                 // новий стек) - НЕ число, cvtsi2sd
                                                 // спотворив би біти адреси (timer.c).
    };

    private ValType ExternalKernelReturnType(string functionName) =>
        ExternalKernelReturnTypes.TryGetValue(functionName, out var t) ? t : ValType.Number;

    // Той самий словник типів, що вже й для kernel-параметрів (дивись
    // CompileKernelFunction) - "string" -> покажчик, числові псевдоніми
    // -> Number, bool -> Bool. Немає анотації (ReturnType == null) ->
    // старий типовий Number (без анотації - без зміни поведінки,
    // ЖОДНА раніше робоча kernel-функція цим фіксом не ламається).
    private ValType KernelReturnTypeFromAnnotation(FunctionDeclaration func) => func.ReturnType switch
    {
        null => ValType.Number,
        "string" => ValType.String,
        "bool" => ValType.Bool,
        "int32" => ValType.Int32,
        "uint32" => ValType.UInt32,
        "any" or "i32" or "f64" or "int" or "number" or "size_t" or "u32" or "usize" => ValType.Number,
        _ => throw new Exception($"native codegen (Фаза N7): тип результату '{func.ReturnType}' функції '{func.Name}' не підтримується для --target nyxos-kernel - лише string/bool/int32/uint32/число (напр. -> string)")
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
                    // Фаза N7: print() робить syscall (int 0x80) - у
                    // Ring0-коді ядра це мало б катастрофічно інший сенс
                    // (не "звернутись до ОС", а буквально програмний
                    // переривання ВСЕРЕДИНІ самої ОС) - чесна заборона,
                    // а не мовчазна генерація небезпечного коду.
                    if (_target == NativeTarget.NyxOSKernel)
                        throw new Exception("native codegen (Фаза N7): print() не підтримується для --target nyxos-kernel (це Ring0-код - syscall тут не має сенсу)");
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

                        case ValType.Array:
                            // БЕЗ явної помилки тут масив надрукувався б
                            // як True/False (потрапивши в гілку Bool
                            // нижче, бо вказівник - теж просто ненульове
                            // число) - неправильно й тихо, тому чесна
                            // відмова замість цього.
                            throw new Exception("native codegen (Фаза N3): print() масиву напряму ще не підтримується - друкуйте елементи через arr[i] у циклі");

                        case ValType.Struct:
                            throw new Exception("native codegen (Фаза N3): print() структури напряму ще не підтримується - друкуйте поля через obj.field");

                        case ValType.Closure:
                            throw new Exception("native codegen (Фаза N4): print() замикання не підтримується");

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
                    // ВІДОМЕ, свідомо НЕ обійдене обмеження Фази N4:
                    // return з СЕРЕДИНИ try/catch стрибнув би повз код,
                    // що деактивує кадр обробника (exc_top лишився б
                    // "висіти" на вже недійсному кадрі стека) - чесна
                    // помилка компіляції краща за тихий крах пізніше.
                    if (_tryDepth > 0)
                        throw new Exception("native codegen (Фаза N4): return усередині try/catch ще не підтримується (обробник не деактивувався б коректно) - винесіть return за межі блоку");
                    if (returnStmt.Value != null)
                    {
                        var t = InferExprType(returnStmt.Value);
                        CompileExpression(returnStmt.Value); // -> %xmm0 (Number) чи %eax (Bool/String)
                        if (_isKernelExport)
                        {
                            // Фаза N7: C ABI - ціле число в %eax (як
                            // звичайний C-return), НЕ %xmm0. Рядок - уже
                            // коректний покажчик у %eax, конвертація не
                            // потрібна (String завжди char*-сумісний).
                            // Bool - УЖЕ 0/1 у %eax (setCC-результат
                            // порівняння) - той самий формат, що C int
                            // (напр. "return a[i] == b[i]" у k_streq).
                            if (t == ValType.Number)
                                _asm.AppendLine("    cvttsd2si %xmm0, %eax");
                            else if (t != ValType.String && t != ValType.Bool && t != ValType.Int32 && t != ValType.UInt32)
                                throw new Exception("native codegen (Фаза N7): kernel-функції можуть повертати лише число (як C int), рядок (як char*), чи int32/uint32");

                            // Фаза N8.5: якщо функція МАЄ явну анотацію
                            // результату (-> string тощо) - звіряємо з
                            // РЕАЛЬНИМ типом значення тут. Без цього
                            // виклик такої функції з ІНШОГО місця (через
                            // KernelReturnTypeFromAnnotation) мовчки
                            // повірив би анотації навіть якщо тіло
                            // насправді повертає щось інше.
                            if (_currentKernelFunc?.ReturnType != null)
                            {
                                var declared = KernelReturnTypeFromAnnotation(_currentKernelFunc);
                                if (declared != t)
                                    throw new Exception($"native codegen (Фаза N8.5): функція '{_currentKernelFunc.Name}' оголошена як -> {_currentKernelFunc.ReturnType}, але цей return дає {t} - виправте анотацію або значення");
                            }
                        }
                        else if (_isMain)
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
                        if (!_isMain && !_isKernelExport) _asm.AppendLine("    pxor %xmm0, %xmm0");
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
                if (_tryDepth > 0)
                    throw new Exception("native codegen (Фаза N4): break усередині try/catch ще не підтримується (обробник не деактивувався б коректно)");
                _asm.AppendLine($"    jmp {_loopLabels.Peek().End}");
                break;

            case ContinueStatement:
                if (_loopLabels.Count == 0)
                {
                    throw new Exception("native codegen: continue поза циклом");
                }
                if (_tryDepth > 0)
                    throw new Exception("native codegen (Фаза N4): continue усередині try/catch ще не підтримується (обробник не деактивувався б коректно)");
                _asm.AppendLine($"    jmp {_loopLabels.Peek().Start}");
                break;

            case TryStatement ts:
                {
                    // Фаза N7: не через якусь небезпеку самого try/catch
                    // (він - чисте стек/регістрове маніпулювання, цілком
                    // безпечне й у Ring0) - а через EmitUnhandledThrowHelper,
                    // який на неперехопленому throw робить syscall exit(),
                    // що в коді ЯДРА взагалі не має сенсу. Чесна заборона
                    // ЗАРАЗ, а не тонкий баг пізніше.
                    if (_target == NativeTarget.NyxOSKernel)
                        throw new Exception("native codegen (Фаза N7): try/catch не підтримується для --target nyxos-kernel (обробник неперехопленого throw робить syscall exit(), якого в коді ядра немає)");
                    // setjmp/longjmp-стиль (Фаза N4): "кадр обробника" -
                    // 16 анонімних байтів на СТЕКУ ЦІЄЇ функції (offset
                    // обчислено заздалегідь у CollectVarsAndTypes,
                    // дивись _tryFrameOffsets): [saved_esp:4][saved_ebp:4]
                    // [catch_label:4][prev_handler_ptr:4]. Глобальний
                    // exc_top (.bss) - вказівник на НАЙБЛИЖЧИЙ активний
                    // кадр - throw (нижче) просто читає його й стрибає,
                    // не знаючи НІЧОГО про те, скільки функцій було
                    // викликано між try і throw (той самий трюк, що
                    // реальний C setjmp/longjmp).
                    int id = _labelCounter++;
                    int frameOffset = _tryFrameOffsets[ts];
                    string catchLabel = $".Ltry{id}_catch";
                    string endLabel = $".Ltry{id}_end";

                    _asm.AppendLine($"    mov %esp, {frameOffset}(%ebp)");
                    _asm.AppendLine($"    mov %ebp, {frameOffset + 4}(%ebp)");
                    _asm.AppendLine($"    mov ${catchLabel}, {frameOffset + 8}(%ebp)");
                    _asm.AppendLine("    mov exc_top, %eax");
                    _asm.AppendLine($"    mov %eax, {frameOffset + 12}(%ebp)"); // prev = старий top
                    _asm.AppendLine($"    lea {frameOffset}(%ebp), %eax");
                    _asm.AppendLine("    mov %eax, exc_top");                   // активуємо ЦЕЙ обробник

                    _tryDepth++;
                    CompileBlock(ts.TryBlock);
                    _tryDepth--;

                    // Нормальне завершення try (БЕЗ throw) - деактивуємо
                    // обробник (повертаємо exc_top до prev) і пропускаємо
                    // catch-блок повністю.
                    _asm.AppendLine($"    mov {frameOffset + 12}(%ebp), %eax");
                    _asm.AppendLine("    mov %eax, exc_top");
                    _asm.AppendLine($"    jmp {endLabel}");

                    _asm.AppendLine($"{catchLabel}:");
                    // Сюди стрибаємо З throw - %esp/%ebp вже ВІДНОВЛЕНІ
                    // (throw сам це зробив перед jmp), тож frameOffset(%ebp)
                    // і далі коректно вказує на ЦЕЙ САМИЙ кадр.
                    _asm.AppendLine($"    mov {frameOffset + 12}(%ebp), %eax");
                    _asm.AppendLine("    mov %eax, exc_top"); // деактивуємо ЦЕЙ обробник і для catch-блоку теж
                    int catchVarOffset = _varOffsets[ts.CatchVariableName];
                    _asm.AppendLine("    movsd exc_value, %xmm0");
                    _asm.AppendLine($"    movsd %xmm0, {catchVarOffset}(%ebp)");

                    _tryDepth++;
                    CompileBlock(ts.CatchBlock);
                    _tryDepth--;

                    _asm.AppendLine($"{endLabel}:");
                    break;
                }

            case ThrowStatement throwStmt:
                {
                    if (_target == NativeTarget.NyxOSKernel)
                        throw new Exception("native codegen (Фаза N7): throw не підтримується для --target nyxos-kernel (той самий обмежувач, що try/catch)");
                    // СПРОЩЕННЯ: throw підтримує лише числові (Number)
                    // значення - catch-змінна статично типізована як
                    // Number завжди (дивись CollectVarsAndTypes вище) -
                    // не можна було б коректно вивести тип для будь-якого
                    // значення без повноцінного tagged union.
                    if (InferExprType(throwStmt.Value) != ValType.Number)
                        throw new Exception("native codegen (Фаза N4): throw підтримує лише числові (Number) значення");
                    CompileExpression(throwStmt.Value); // -> %xmm0
                    _asm.AppendLine("    movsd %xmm0, exc_value");
                    _asm.AppendLine("    mov exc_top, %eax");
                    _asm.AppendLine("    cmp $0, %eax");
                    _asm.AppendLine("    je .Lunhandled_throw"); // немає активного try - чесний аварійний вихід, а НЕ спроба продовжити ніби нічого не сталось
                    _asm.AppendLine("    mov (%eax), %ecx");     // saved_esp
                    _asm.AppendLine("    mov 4(%eax), %edx");    // saved_ebp
                    _asm.AppendLine("    mov 8(%eax), %eax");    // catch_label (перезаписуємо вказівник кадру - він уже прочитаний)
                    _asm.AppendLine("    mov %ecx, %esp");
                    _asm.AppendLine("    mov %edx, %ebp");
                    _asm.AppendLine("    jmp *%eax");
                    break;
                }

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

            case ArrayLiteralExpression arrLit:
                {
                    // Масив - вказівник на купу (Фаза N3, третя частина):
                    // [довжина:4][padding:4][елементи, по 8 байтів кожен].
                    // СПРОЩЕННЯ: лише Number-елементи (немає повноцінного
                    // tagged union для змішаних типів - Фаза N4+). Лише
                    // Linux-ціль - у NyxOS ще немає syscall'у виділення
                    // пам'яті в ABI цього компілятора.
                    if (_target != NativeTarget.Linux)
                        throw new Exception("native codegen (Фаза N3): масиви поки підтримуються лише для --target linux - потрібен syscall виділення пам'яті, якого ще немає в ABI NyxOS (Фаза N6+)");
                    foreach (var el in arrLit.Elements)
                    {
                        if (InferExprType(el) != ValType.Number)
                            throw new Exception("native codegen (Фаза N3): елементи масиву підтримуються лише числові (Number) - змішані типи потребують tagged union (Фаза N4+)");
                    }
                    int count = arrLit.Elements.Count;
                    int totalBytes = 8 + count * 8;
                    _asm.AppendLine($"    mov ${totalBytes}, %eax");
                    _asm.AppendLine("    call heap_alloc");         // -> %eax = новий блок
                    _asm.AppendLine($"    mov ${count}, (%eax)");   // довжина в перших 4 байтах
                    _asm.AppendLine("    push %eax");                // зберігаємо вказівник - компіляція елементів зіпсує %eax/%xmm0
                    for (int i = 0; i < count; i++)
                    {
                        CompileExpression(arrLit.Elements[i]);       // -> %xmm0
                        _asm.AppendLine("    mov (%esp), %eax");     // підглядаємо вказівник, НЕ знімаючи зі стека
                        _asm.AppendLine($"    movsd %xmm0, {8 + i * 8}(%eax)");
                    }
                    _asm.AppendLine("    pop %eax");                  // результат виразу - сам вказівник
                    break;
                }

            case IndexExpression idx:
                {
                    var containerType = InferExprType(idx.Array);
                    if (containerType != ValType.Array && containerType != ValType.String)
                        throw new Exception("native codegen: індексування [..] підтримується лише для масивів чи рядків");
                    if (InferExprType(idx.Index) != ValType.Number)
                        throw new Exception("native codegen: індекс має бути числом");
                    CompileExpression(idx.Array);                    // -> %eax (вказівник)
                    _asm.AppendLine("    push %eax");
                    CompileExpression(idx.Index);                    // -> %xmm0
                    _asm.AppendLine("    cvttsd2si %xmm0, %ecx");    // індекс -> ціле
                    _asm.AppendLine("    pop %eax");
                    // ВІДОМЕ, свідомо НЕ виправлене обмеження цієї фази:
                    // БЕЗ перевірки меж - вихід за [0, довжина) читає за
                    // межі виділеного блоку (Фаза N4+).
                    if (containerType == ValType.Array)
                    {
                        _asm.AppendLine("    movsd 8(%eax,%ecx,8), %xmm0");
                    }
                    else
                    {
                        // s[i] (Фаза N7) - БАЙТ (0-255) за позицією i в
                        // NUL-термінованому UTF-8-буфері (String) -
                        // потрібно для k_strlen-подібної логіки
                        // ("поки s[i] != 0"). movzbl - нуль-розширення
                        // байта до 32-біт ПЕРЕД конвертацією в double
                        // (щоб байти 128-255 не стали від'ємними).
                        _asm.AppendLine("    movzbl (%eax,%ecx,1), %edx");
                        _asm.AppendLine("    cvtsi2sd %edx, %xmm0");
                    }
                    break;
                }

            case StructInitExpression structInit:
                {
                    // Структура - вказівник на купу: поля по 8 байтів,
                    // offset = індекс поля в StructDeclaration.Fields*8
                    // (обчислено один раз у Compile()). СПРОЩЕННЯ: лише
                    // Number-поля, ВСІ поля мають бути ініціалізовані
                    // явно (без значень за замовчуванням) - інакше решта
                    // блоку лишилась би непроініціалізованим сміттям.
                    if (_target != NativeTarget.Linux)
                        throw new Exception("native codegen (Фаза N3): структури поки підтримуються лише для --target linux - потрібен syscall виділення пам'яті, якого ще немає в ABI NyxOS");
                    if (!_structFieldOffsets.TryGetValue(structInit.StructName, out var fieldOffsets))
                        throw new Exception($"native codegen: невідома структура '{structInit.StructName}'");
                    if (structInit.Fields.Count != fieldOffsets.Count)
                        throw new Exception($"native codegen (Фаза N3): усі поля структури '{structInit.StructName}' мають бути ініціалізовані явно (часткова ініціалізація/значення за замовчуванням - Фаза N4+)");
                    foreach (var f in structInit.Fields)
                    {
                        if (!fieldOffsets.ContainsKey(f.Name))
                            throw new Exception($"native codegen: структура '{structInit.StructName}' не має поля '{f.Name}'");
                        if (InferExprType(f.Value) != ValType.Number)
                            throw new Exception("native codegen (Фаза N3): поля структур підтримуються лише числові (Number)");
                    }
                    int totalBytes = fieldOffsets.Count * 8;
                    _asm.AppendLine($"    mov ${totalBytes}, %eax");
                    _asm.AppendLine("    call heap_alloc");
                    _asm.AppendLine("    push %eax");
                    foreach (var f in structInit.Fields)
                    {
                        CompileExpression(f.Value);                     // -> %xmm0
                        _asm.AppendLine("    mov (%esp), %eax");        // вказівник - підглядаємо, НЕ знімаючи
                        _asm.AppendLine($"    movsd %xmm0, {fieldOffsets[f.Name]}(%eax)");
                    }
                    _asm.AppendLine("    pop %eax");
                    break;
                }

            case MemberAccessExpression member:
                {
                    string structName = ResolveStructName(member.Object);
                    if (!_structFieldOffsets.TryGetValue(structName, out var fieldOffsets) || !fieldOffsets.TryGetValue(member.Member, out int fieldOffset))
                        throw new Exception($"native codegen: структура '{structName}' не має поля '{member.Member}'");
                    CompileExpression(member.Object);        // -> %eax (вказівник)
                    _asm.AppendLine($"    movsd {fieldOffset}(%eax), %xmm0");
                    break;
                }

            case MethodCallExpression methodCall:
                {
                    // obj.method(args) - Фаза N4. Метод скомпільовано під
                    // міткою "StructName__methodName" (Compile(),
                    // labelOverride), з НЕЯВНИМ self ПЕРШИМ параметром
                    // (сам Parser.cs так робить - self просто звичайний
                    // FunctionParameter із Type = ім'я структури).
                    string structName = ResolveStructName(methodCall.Object);
                    if (!_structMethods.TryGetValue(structName, out var methods) || !methods.TryGetValue(methodCall.MethodName, out var methodDecl))
                        throw new Exception($"native codegen: структура '{structName}' не має методу '{methodCall.MethodName}'");
                    int expectedArgs = methodDecl.Parameters.Count - 1; // мінус self
                    if (methodCall.Arguments.Count != expectedArgs)
                        throw new Exception($"native codegen: метод '{structName}.{methodCall.MethodName}' очікує {expectedArgs} аргумент(и/ів), отримано {methodCall.Arguments.Count}");

                    // cdecl, справа наліво, як і CallExpression - self
                    // (перший ЛОГІЧНИЙ параметр) мусить лягти НАЙБЛИЖЧЕ
                    // до вершини стека (тобто ОСТАННІМ push'ом), щоб
                    // callee побачив його рівно за 8(%ebp).
                    for (int i = methodCall.Arguments.Count - 1; i >= 0; i--)
                    {
                        if (InferExprType(methodCall.Arguments[i]) != ValType.Number)
                            throw new Exception("native codegen (Фаза N4): аргументи методів підтримуються лише числові (Number)");
                        CompileExpression(methodCall.Arguments[i]);    // -> %xmm0
                        _asm.AppendLine("    sub $8, %esp");
                        _asm.AppendLine("    movsd %xmm0, (%esp)");
                    }
                    if (InferExprType(methodCall.Object) != ValType.Struct)
                        throw new Exception("native codegen: метод можна викликати лише на структурі");
                    CompileExpression(methodCall.Object);              // -> %eax (self-вказівник)
                    _asm.AppendLine("    sub $8, %esp");
                    _asm.AppendLine("    mov %eax, (%esp)");           // self - ЦІЛИЙ вказівник (32-біт), НЕ double
                    _asm.AppendLine($"    call {structName}__{methodCall.MethodName}");
                    _asm.AppendLine($"    add ${(methodCall.Arguments.Count + 1) * 8}, %esp");
                    // Результат - уже в %xmm0 (методи, як і звичайні
                    // функції, повертають лише Number).
                    break;
                }

            case FunctionExpression fnExpr:
                {
                    // var f = func(...) {...} - СТВОРЕННЯ замикання
                    // (Фаза N4). Саме тіло компілюється ПІЗНІШЕ (чергою
                    // _pendingLambdas у Compile()) - тут лише генеруємо
                    // код, що на цьому МІСЦІ виконання (!) знімає
                    // "знімок" (за ЗНАЧЕННЯМ - дивись коментар над
                    // ValType.Closure) поточних значень вільних змінних.
                    if (_target != NativeTarget.Linux)
                        throw new Exception("native codegen (Фаза N4): замикання поки підтримуються лише для --target linux");
                    var freeVars = FindFreeVars(fnExpr);
                    foreach (var fv in freeVars)
                    {
                        if (!_varTypes.TryGetValue(fv, out var fvType) || fvType != ValType.Number)
                            throw new Exception($"native codegen (Фаза N4): замикання можуть захоплювати лише числові (Number) змінні - '{fv}' не підходить");
                    }
                    string label = $".Llambda{_lambdaCounter++}";
                    _pendingLambdas.Add((label, fnExpr, freeVars));

                    if (freeVars.Count > 0)
                    {
                        _asm.AppendLine($"    mov ${freeVars.Count * 8}, %eax");
                        _asm.AppendLine("    call heap_alloc");
                        _asm.AppendLine("    push %eax");                       // [envPtr]
                        for (int i = 0; i < freeVars.Count; i++)
                        {
                            CompileExpression(new VariableExpression(freeVars[i])); // -> %xmm0 (ПОТОЧНЕ значення в ЦЬОМУ контексті)
                            _asm.AppendLine("    mov (%esp), %eax");
                            _asm.AppendLine($"    movsd %xmm0, {i * 8}(%eax)");
                        }
                    }
                    else
                    {
                        _asm.AppendLine("    xor %eax, %eax");
                        _asm.AppendLine("    push %eax");                       // [envPtr = NULL] - тіло лямбди його не читає
                    }

                    // Заголовок замикання {code_addr:4, env_ptr:4} - сам
                    // вказівник на нього і є значенням виразу.
                    _asm.AppendLine("    mov $8, %eax");
                    _asm.AppendLine("    call heap_alloc");
                    _asm.AppendLine("    pop %ecx");             // envPtr назад
                    _asm.AppendLine($"    mov ${label}, (%eax)");
                    _asm.AppendLine("    mov %ecx, 4(%eax)");
                    break;
                }

            case VariableExpression varExpr:
                {
                    if (_varOffsets.TryGetValue(varExpr.Name, out int offset))
                    {
                        if (_varTypes[varExpr.Name] == ValType.Number)
                            _asm.AppendLine($"    movsd {offset}(%ebp), %xmm0");
                        else
                            _asm.AppendLine($"    mov {offset}(%ebp), %eax");
                        break;
                    }
                    // Глобальна (Фаза N7, лише --target nyxos-kernel) -
                    // локальна/параметр ЗАВЖДИ затіняє однойменну
                    // глобальну (перевіряємо ТУТ, ПІСЛЯ невдалого
                    // пошуку в _varOffsets вище - узгоджено з тим, як
                    // мова взагалі поводиться, tests/test_globals.nx).
                    if (_globalVars.TryGetValue(varExpr.Name, out var globalType))
                    {
                        string label = $"__g_{varExpr.Name}";
                        if (globalType == ValType.Number)
                            _asm.AppendLine($"    movsd {label}, %xmm0");
                        else
                            _asm.AppendLine($"    mov {label}, %eax");
                        break;
                    }
                    // Фаза N8.5c: "гола" назва СВОЄЇ функції (без виклику)
                    // - її АДРЕСА (той самий підхід, що kernel-функції
                    // вже й так експортуються під ВЛАСНИМ іменем як
                    // `.global funcName`, C ABI-сумісно) - потрібно для
                    // callback-параметрів (pci_scan, isr_register_handler
                    // тощо). Сирий reinterpret: мітка функції - вже
                    // валідна адреса коду, immediate-завантаження в %eax,
                    // БЕЗ виклику.
                    if (_target == NativeTarget.NyxOSKernel && _knownFunctions.Contains(varExpr.Name))
                    {
                        _asm.AppendLine($"    mov ${varExpr.Name}, %eax");
                        break;
                    }
                    throw new Exception($"native codegen: змінна '{varExpr.Name}' використана до оголошення (масиви/структури - наступна фаза)");
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

            case UnaryExpression { Operator: "~" } unaryBitNot:
                {
                    // Фаза N8 - лише для Number (той самий cvttsd2si/
                    // cvtsi2sd round-trip, що CompileNumberBinary для
                    // &|^<<>>) - на Bool/іншому чесна помилка компіляції,
                    // а не спроба вгадати поведінку.
                    var tNot = InferExprType(unaryBitNot.Operand);
                    if (tNot == ValType.Int32 || tNot == ValType.UInt32)
                    {
                        // Фаза N12: операнд уже сире 4-байтове значення в
                        // %eax (не double) - НІЯКОГО round-trip через
                        // %xmm0 не треба, на відміну від Number-шляху
                        // нижче - оце і є реальна перевага справжніх
                        // цілих типів, не лише "інший ярлик".
                        CompileExpression(unaryBitNot.Operand);
                        _asm.AppendLine("    not %eax");
                        break;
                    }
                    if (tNot != ValType.Number)
                    {
                        throw new Exception("native codegen (Фаза N8): '~' підтримується лише для чисел чи int32/uint32");
                    }
                    CompileExpression(unaryBitNot.Operand);
                    _asm.AppendLine("    cvttsd2si %xmm0, %eax");
                    _asm.AppendLine("    not %eax");
                    _asm.AppendLine("    cvtsi2sd %eax, %xmm0");
                    break;
                }

            case CallExpression call:
                {
                    // Фаза N7: kernel-функції (--target nyxos-kernel)
                    // мають ЗОВСІМ ІНШУ конвенцію виклику - звичайний C
                    // ABI (кожен аргумент - 4 байти, ЗАВЖДИ), а не
                    // уніфікований 8-байтовий Number-слот, яким
                    // користується РЕШТА цього компілятора - тому
                    // окрема гілка, не спроба "втиснути" у код нижче.
                    if (_target == NativeTarget.NyxOSKernel)
                    {
                        // peek32/poke32 (Фаза N8, 16.09.2026) - читання/
                        // запис 4-байтового слова за адресою (String -
                        // той самий "сирий вказівник у %eax", що ВЖЕ дає
                        // kmalloc). Потрібно для kheap.c-подібного коду:
                        // мова НЕ має справжніх структур-як-сирої-пам'яті
                        // й адресної арифметики (Фаза N8, наступний крок
                        // після побітових операторів) - це МІНІМАЛЬНИЙ,
                        // достатній примітив замість повної системи
                        // вказівників: "поле" структури в пам'яті - це
                        // просто (базова_адреса + зсув), а peek/poke
                        // читає/пише РІВНО 4 байти там. ЧОТИРИ варіанти
                        // (не один) - бо статичний ТИП результату
                        // (Number для size/flags, String/вказівник для
                        // полів на кшталт "next") компілятор мусить
                        // знати ЗАЗДАЛЕГІДЬ (InferExprType вище), а не
                        // вгадувати за контекстом використання.
                        // Портовий ввід-вивід (Фаза N8.5d, 18.09.2026) -
                        // компілятор-інтринзики (як peek/poke), НЕ
                        // зовнішні символи - генерують сирі in/out
                        // інструкції напряму. Потрібні для pci.c-
                        // подібного коду (і майже КОЖНОГО іншого файлу,
                        // що торкається заліза напряму - timer.c/rtc.c/
                        // keyboard.c/vga.c/ata.c тощо, ~20 ще лишається).
                        // Номер порту - у %dx (апаратна вимога x86 для
                        // "змінного порту" форми in/out), значення - у
                        // AL/AX/EAX залежно від розміру.
                        if ((call.FunctionName == "outb" || call.FunctionName == "outw" || call.FunctionName == "outl")
                            && call.Arguments.Count == 2)
                        {
                            CompileExpression(call.Arguments[0]); // порт -> %xmm0
                            _asm.AppendLine("    cvttsd2si %xmm0, %edx");
                            _asm.AppendLine("    push %edx");
                            CompileExpression(call.Arguments[1]); // значення -> %xmm0
                            _asm.AppendLine("    cvttsd2si %xmm0, %eax");
                            _asm.AppendLine("    pop %edx");
                            string outReg = call.FunctionName switch { "outb" => "%al", "outw" => "%ax", _ => "%eax" };
                            string outSuffix = call.FunctionName switch { "outb" => "b", "outw" => "w", _ => "l" };
                            _asm.AppendLine($"    out{outSuffix} {outReg}, %dx");
                            _asm.AppendLine("    xor %eax, %eax");
                            _asm.AppendLine("    cvtsi2sd %eax, %xmm0"); // "повертає" 0 - результат ніде реально не використовується, як pokeNum/pokePtr
                            break;
                        }
                        if ((call.FunctionName == "inb" || call.FunctionName == "inw" || call.FunctionName == "inl")
                            && call.Arguments.Count == 1)
                        {
                            CompileExpression(call.Arguments[0]); // порт -> %xmm0
                            _asm.AppendLine("    cvttsd2si %xmm0, %edx");
                            string inReg = call.FunctionName switch { "inb" => "%al", "inw" => "%ax", _ => "%eax" };
                            string inSuffix = call.FunctionName switch { "inb" => "b", "inw" => "w", _ => "l" };
                            _asm.AppendLine("    xor %eax, %eax"); // очищаємо ВЕРХНІ байти ПЕРЕД частковим in - inb/inw їх не чіпають самі
                            _asm.AppendLine($"    in{inSuffix} %dx, {inReg}");
                            _asm.AppendLine("    cvtsi2sd %eax, %xmm0");
                            break;
                        }
                        if (call.FunctionName == "hlt" && call.Arguments.Count == 0)
                        {
                            _asm.AppendLine("    hlt");
                            _asm.AppendLine("    xor %eax, %eax");
                            _asm.AppendLine("    cvtsi2sd %eax, %xmm0"); // "повертає" 0 - результат ніде реально не використовується, як out*/poke*
                            break;
                        }
                        if (call.FunctionName == "numToPtr" && call.Arguments.Count == 1)
                        {
                            // Сире перетворення число->вказівник (Фаза
                            // N8.5, 18.09.2026) - потрібне для kheap.c-
                            // подібного коду: HEAP_START/heap_break -
                            // звичайна адреса-як-число (щоб працювали
                            // ЗВИЧАЙНІ >/< /арифметика через
                            // CompileNumberBinary), а ЩОЙНО з неї треба
                            // зробити СПРАВЖНІЙ вказівник (щоб peek/poke й
                            // "ptr + N" запрацювали) - рівно один
                            // reinterpret-cast %xmm0->%eax, БЕЗ звернення
                            // до пам'яті (на відміну від peekNum/peekPtr).
                            CompileExpression(call.Arguments[0]); // число (адреса) -> %xmm0
                            _asm.AppendLine("    cvttsd2si %xmm0, %eax");
                            break;
                        }
                        if (call.FunctionName == "peekNum" && call.Arguments.Count == 1)
                        {
                            CompileExpression(call.Arguments[0]); // адреса (String) -> %eax
                            _asm.AppendLine("    mov (%eax), %eax");
                            _asm.AppendLine("    cvtsi2sd %eax, %xmm0");
                            break;
                        }
                        if (call.FunctionName == "peekPtr" && call.Arguments.Count == 1)
                        {
                            CompileExpression(call.Arguments[0]); // адреса (String) -> %eax
                            _asm.AppendLine("    mov (%eax), %eax"); // результат - теж String (вказівник), лишається в %eax
                            break;
                        }
                        if (call.FunctionName == "pokeNum" && call.Arguments.Count == 2)
                        {
                            CompileExpression(call.Arguments[0]); // адреса -> %eax
                            _asm.AppendLine("    push %eax");
                            CompileExpression(call.Arguments[1]); // значення (Number) -> %xmm0
                            _asm.AppendLine("    cvttsd2si %xmm0, %ecx");
                            _asm.AppendLine("    pop %eax");
                            _asm.AppendLine("    mov %ecx, (%eax)");
                            _asm.AppendLine("    xor %eax, %eax");
                            _asm.AppendLine("    cvtsi2sd %eax, %xmm0"); // "повертає" 0 - результат ніде реально не використовується
                            break;
                        }
                        if (call.FunctionName == "pokePtr" && call.Arguments.Count == 2)
                        {
                            CompileExpression(call.Arguments[0]); // адреса -> %eax
                            _asm.AppendLine("    push %eax");
                            CompileExpression(call.Arguments[1]); // значення (String/вказівник) -> %eax
                            _asm.AppendLine("    mov %eax, %ecx");
                            _asm.AppendLine("    pop %eax");
                            _asm.AppendLine("    mov %ecx, (%eax)");
                            _asm.AppendLine("    xor %eax, %eax");
                            _asm.AppendLine("    cvtsi2sd %eax, %xmm0");
                            break;
                        }
                        if ((call.FunctionName == "toI32" || call.FunctionName == "toU32") && call.Arguments.Count == 1)
                        {
                            // Однаковий бітовий шлях для обох (Фаза N12) -
                            // "обрізати double до нижніх 32 бітів" не
                            // залежить від того, як ці 32 біти ІНТЕРПРЕТУЮТЬ
                            // ПІЗНІШЕ (signed чи unsigned) - лише ValType-
                            // тег результату відрізняється (вирішує
                            // InferExprType вище), сам код - однаковий.
                            CompileExpression(call.Arguments[0]); // -> %xmm0 (Number)
                            EmitDoubleToUInt32("%xmm0", "%eax");
                            break;
                        }
                        if (call.FunctionName == "toNumber" && call.Arguments.Count == 1)
                        {
                            var srcType = InferExprType(call.Arguments[0]);
                            CompileExpression(call.Arguments[0]); // -> %eax (Int32/UInt32) чи %xmm0 (уже Number)
                            if (srcType == ValType.Int32)
                            {
                                _asm.AppendLine("    cvtsi2sd %eax, %xmm0"); // signed-реконструкція
                            }
                            else if (srcType == ValType.UInt32)
                            {
                                EmitUInt32ToDouble("%eax", "%xmm0"); // unsigned-реконструкція (Фаза N10)
                            }
                            // srcType == Number: CompileExpression вже дав %xmm0 - нічого робити не треба
                            break;
                        }
                        // asm("...") (Фаза N11, 22.09.2026) - Sviatoslav's
                        // прямо названий пріоритет #1 після "чи може моя
                        // мова замінити C/Assembler?": СПРАВЖНІЙ inline-
                        // asm, а не ще один іменований інтринзик на КОЖНУ
                        // нову інструкцію (cpuid/lgdt/rdtsc/lock cmpxchg/
                        // rep movsb і т.д. - список, що ніколи не
                        // закінчується). Кожен аргумент - рядковий ЛІТЕРАЛ
                        // (відомий на етапі компіляції - НЕ змінна й НЕ
                        // обчислюваний вираз), і йде в згенерований .s
                        // ДОСЛІВНО, своїм рядком. Кілька аргументів =
                        // кілька послідовних інструкцій: asm("cli", "hlt",
                        // "sti").
                        //
                        // СВІДОМО без списку "clobbers" (на відміну від
                        // GCC/Clang asm volatile) - компілятор НЕ знає (і
                        // не намагається вгадати), які регістри/прапорці
                        // змінить довільний текст усередині. Це ТОЧНО
                        // такий самий рівень довіри/відповідальності
                        // програміста, що й реальний "голий" inline-asm -
                        // не хиба, а свідома межа мінімальної першої
                        // версії. Kernel-target-only (Ring0-код і так
                        // повністю довірений).
                        if (call.FunctionName == "asm" && call.Arguments.Count >= 1)
                        {
                            foreach (var asmArg in call.Arguments)
                            {
                                if (asmArg is not LiteralExpression { Value: string rawInstr })
                                    throw new Exception("native codegen (Фаза N11): asm(...) приймає лише рядкові літерали, відомі на етапі компіляції - не змінну й не обчислюваний вираз");
                                _asm.AppendLine($"    {rawInstr}");
                            }
                            _asm.AppendLine("    xor %eax, %eax");
                            _asm.AppendLine("    cvtsi2sd %eax, %xmm0"); // "повертає" 0 - результат ніде реально не використовується, як hlt()/out*
                            break;
                        }

                        // Будь-яке ім'я, що НЕ є функцією з ЦЬОГО файлу
                        // (Фаза N7) - вважаємо ЗОВНІШНІМ символом
                        // РЕАЛЬНОГО ядра NyxOS (kheap.c/gfx.c/vga_font.c
                        // тощо - NyxilumLang не має синтаксису "extern",
                        // тож перевірити ЗАЗДАЛЕГІДЬ, що такий символ і
                        // справді існує, тут неможливо) - лінкер (не цей
                        // компілятор) резолвить `call funcName`, коли
                        // .o влиється в реальну збірку ядра; СПРАВЖНЯ
                        // помилка (typo, неіснуюче ім'я) виявиться на
                        // етапі ЛІНКУВАННЯ ("undefined reference") -
                        // пізніше, ніж хотілось би, але не мовчки.
                        // Непрямий виклик через ЗНАЧЕННЯ (Фаза N8.5c,
                        // 18.09.2026): call.FunctionName - НЕ функція
                        // цього файлу, а звичайна локальна/глобальна
                        // ЗМІННА типу String, що зберігає АДРЕСУ функції
                        // (взяту раніше "голою назвою" - дивись
                        // VariableExpression вище). Потрібно, щоб
                        // САМА kernel-функція могла викликати callback,
                        // який їй передали параметром (той самий сенс,
                        // що pci_scan(callback) у NyxOS - callback
                        // виконується ВСЕРЕДИНІ pci_scan, не ззовні).
                        bool isIndirectCall = !_knownFunctions.Contains(call.FunctionName)
                            && ((_varTypes.TryGetValue(call.FunctionName, out var indirectVarType) && indirectVarType == ValType.String)
                                || (!_varOffsets.ContainsKey(call.FunctionName) && _globalVars.TryGetValue(call.FunctionName, out var indirectGlobalType) && indirectGlobalType == ValType.String));

                        if (isIndirectCall)
                        {
                            int pushedIndirect = 0;
                            for (int i = call.Arguments.Count - 1; i >= 0; i--)
                            {
                                var argType = InferExprType(call.Arguments[i]);
                                CompileExpression(call.Arguments[i]);
                                if (argType == ValType.Number)
                                {
                                    _asm.AppendLine("    cvttsd2si %xmm0, %eax");
                                    _asm.AppendLine("    push %eax");
                                }
                                else if (argType == ValType.String || argType == ValType.Int32 || argType == ValType.UInt32)
                                {
                                    _asm.AppendLine("    push %eax");
                                }
                                else
                                {
                                    throw new Exception("native codegen (Фаза N8.5c): аргументи непрямого виклику підтримуються лише числові, рядкові (вказівники) чи int32/uint32");
                                }
                                pushedIndirect += 4;
                            }
                            // Значення змінної (адреса функції) - в %eax,
                            // завантажуємо ОСТАННІМ (після всіх push'ів
                            // аргументів), щоб не затерти їх.
                            CompileExpression(new VariableExpression(call.FunctionName));
                            _asm.AppendLine("    call *%eax");
                            if (pushedIndirect > 0)
                                _asm.AppendLine($"    add ${pushedIndirect}, %esp");
                            // Callback - завжди void у наших сценаріях
                            // (pci_scan-подібні callback'и нічого не
                            // повертають) - Number-конвенція за
                            // замовчуванням, як і для невідомих зовнішніх
                            // викликів без анотації.
                            _asm.AppendLine("    cvtsi2sd %eax, %xmm0");
                            break;
                        }

                        bool isKnownFunc = _knownFunctions.Contains(call.FunctionName);

                        int pushedBytes = 0;
                        for (int i = call.Arguments.Count - 1; i >= 0; i--)
                        {
                            var argType = InferExprType(call.Arguments[i]);
                            CompileExpression(call.Arguments[i]);
                            if (argType == ValType.Number)
                            {
                                _asm.AppendLine("    cvttsd2si %xmm0, %eax"); // C int - 4 байти, НЕ 8-байтовий Number-слот
                                _asm.AppendLine("    push %eax");
                            }
                            else if (argType == ValType.String || argType == ValType.Int32 || argType == ValType.UInt32)
                            {
                                _asm.AppendLine("    push %eax"); // вже 4-байтове значення (вказівник чи int32/uint32)
                            }
                            else
                            {
                                throw new Exception("native codegen (Фаза N7): аргументи kernel-функцій підтримуються лише числові, рядкові (вказівники) чи int32/uint32");
                            }
                            pushedBytes += 4;
                        }
                        _asm.AppendLine($"    call {call.FunctionName}");
                        if (pushedBytes > 0)
                            _asm.AppendLine($"    add ${pushedBytes}, %esp");
                        // Результат callee - у %eax (C ABI). Конвертуємо
                        // до конвенції ВИРАЗУ цього компілятора: String/
                        // Bool - УЖЕ %eax (нічого робити не треба),
                        // Number - конвертуємо в %xmm0. Для ВІДОМИХ
                        // (з цього файлу) функцій - Фаза N8.5: тепер
                        // дивимось на явну анотацію результату
                        // (func f(...) -> string), якщо вона є - без
                        // анотації, як і раніше, Number. Для НЕВІДОМИХ
                        // (зовнішніх) - дивимось у ExternalKernelReturnType.
                        var retType = isKnownFunc
                            ? (_kernelFuncsByName.TryGetValue(call.FunctionName, out var calledDecl2) ? KernelReturnTypeFromAnnotation(calledDecl2) : ValType.Number)
                            : ExternalKernelReturnType(call.FunctionName);
                        if (retType == ValType.Number)
                            _asm.AppendLine("    cvtsi2sd %eax, %xmm0");
                        break;
                    }
                    // f(args), де f - плоский ідентифікатор: АБО справжня
                    // функція з ЦЬОГО Ж файлу (call.FunctionName в
                    // _knownFunctions), АБО (Фаза N4) локальна змінна-
                    // ЗАМИКАННЯ - Parser.cs генерує ОДИН і той самий
                    // CallExpression-вузол для ОБОХ випадків, різницю
                    // бачимо лише тут, за типом.
                    bool isClosureVar = !_knownFunctions.Contains(call.FunctionName)
                        && _varTypes.TryGetValue(call.FunctionName, out var calleeType)
                        && calleeType == ValType.Closure;

                    if (!_knownFunctions.Contains(call.FunctionName) && !isClosureVar)
                    {
                        throw new Exception($"native codegen: невідома функція '{call.FunctionName}' (лише вбудований print(), функції з ЦЬОГО Ж файлу чи локальна змінна-замикання - стандартна бібліотека/імпорти - значно пізніша фаза)");
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
                    if (isClosureVar)
                    {
                        // НЕПРЯМИЙ виклик через заголовок замикання
                        // {code_addr, env_ptr} - env передається як
                        // НЕЯВНИЙ ОСТАННІЙ (найближчий до виклику) аргумент,
                        // той самий підхід, що self у MethodCallExpression.
                        CompileExpression(new VariableExpression(call.FunctionName)); // -> %eax (заголовок)
                        _asm.AppendLine("    mov (%eax), %ecx");   // code_addr
                        _asm.AppendLine("    mov 4(%eax), %eax");  // env_ptr (перезаписуємо заголовок - він більше не потрібен)
                        // РЕАЛЬНА ПОМИЛКА, знайдена живим тестом: звичайний
                        // `push %eax` кладе лише 4 байти, а ВСІ інші
                        // аргументи (і сам callee) очікують УНІФІКОВАНИЙ
                        // 8-байтовий слот на аргумент - зсунуло на 4 байти
                        // ВСЕ, що вже лежало на стеку (параметр x читався
                        // напівсмітям) - лише перший аргумент десь у
                        // ланцюжку "просто пощастило" виявити одразу.
                        _asm.AppendLine("    sub $8, %esp");
                        _asm.AppendLine("    mov %eax, (%esp)");
                        _asm.AppendLine("    call *%ecx");
                        _asm.AppendLine($"    add ${(call.Arguments.Count + 1) * 8}, %esp");
                    }
                    else
                    {
                        _asm.AppendLine($"    call {call.FunctionName}");
                        if (call.Arguments.Count > 0)
                        {
                            _asm.AppendLine($"    add ${call.Arguments.Count * 8}, %esp"); // ВИКЛИКАЧ прибирає аргументи (cdecl, не stdcall)
                        }
                    }
                    // Результат - уже в %xmm0 (усі функції/замикання
                    // Number-, за конвенцією ReturnStatement/InferExprType вище).
                    break;
                }

            case BinaryExpression { Operator: "=" } assign:
                {
                    if (assign.Left is IndexExpression idxTarget)
                    {
                        // arr[i]/s[i] = значення - на відміну від присвоєння
                        // звичайній змінній (нижче), тут ОБИДВА - і
                        // контейнер, і індекс - самі є виразами, що можуть
                        // клáсти будь-що в %eax/%xmm0, тому зберігаємо їх
                        // на стеку (LIFO), поки рахуємо RHS.
                        var containerType = InferExprType(idxTarget.Array);
                        if (containerType != ValType.Array && containerType != ValType.String)
                            throw new Exception("native codegen: індексоване присвоєння підтримується лише для масивів чи рядків");
                        if (InferExprType(idxTarget.Index) != ValType.Number)
                            throw new Exception("native codegen: індекс має бути числом");
                        if (InferExprType(assign.Right) != ValType.Number)
                            throw new Exception("native codegen (Фаза N3): елементи масиву/рядка підтримуються лише числові (Number)");

                        CompileExpression(idxTarget.Array);          // -> %eax
                        _asm.AppendLine("    push %eax");            // [ptr]
                        CompileExpression(idxTarget.Index);          // -> %xmm0
                        _asm.AppendLine("    cvttsd2si %xmm0, %ecx");
                        _asm.AppendLine("    push %ecx");            // [ptr, index]
                        CompileExpression(assign.Right);             // -> %xmm0
                        _asm.AppendLine("    pop %ecx");             // індекс
                        _asm.AppendLine("    pop %eax");             // вказівник
                        if (containerType == ValType.Array)
                        {
                            _asm.AppendLine("    movsd %xmm0, 8(%eax,%ecx,8)");
                        }
                        else
                        {
                            // s[i] = значення (Фаза N7, потрібно для
                            // k_strcpy-подібної логіки) - ЗАПИС ОДНОГО
                            // байта (0-255). ЛИШЕ для kernel-цілі: там
                            // String-параметр - СПРАВЖНІЙ, ПИСАБЕЛЬНИЙ
                            // буфер від виклику (як char* dst у C). Для
                            // Linux/NyxOS-userspace String - ЗАВЖДИ
                            // вказівник на .rodata (лише літерали чи
                            // змінні з літералів - functions там НЕ
                            // приймають String-параметрів) - запис туди
                            // гарантовано впав би (сегфолт на read-only
                            // сторінці), тому чесна заборона замість
                            // "працює, поки не спробуєш".
                            if (_target != NativeTarget.NyxOSKernel)
                                throw new Exception("native codegen (Фаза N7): запис у рядок s[i]=... підтримується лише для --target nyxos-kernel (рядки в інших цілях - завжди .rodata, лише для читання)");
                            _asm.AppendLine("    cvttsd2si %xmm0, %edx");
                            _asm.AppendLine("    movb %dl, (%eax,%ecx,1)");
                        }
                        break;
                    }
                    if (assign.Left is MemberAccessExpression memberTarget)
                    {
                        // obj.field = значення - той самий LIFO-підхід, що
                        // arr[i] = ... вище.
                        string structName = ResolveStructName(memberTarget.Object);
                        if (!_structFieldOffsets.TryGetValue(structName, out var fieldOffsets) || !fieldOffsets.TryGetValue(memberTarget.Member, out int fieldOffset))
                            throw new Exception($"native codegen: структура '{structName}' не має поля '{memberTarget.Member}'");
                        if (InferExprType(assign.Right) != ValType.Number)
                            throw new Exception("native codegen (Фаза N3): поля структур підтримуються лише числові (Number)");

                        CompileExpression(memberTarget.Object);      // -> %eax (вказівник)
                        _asm.AppendLine("    push %eax");
                        CompileExpression(assign.Right);             // -> %xmm0
                        _asm.AppendLine("    pop %eax");
                        _asm.AppendLine($"    movsd %xmm0, {fieldOffset}(%eax)");
                        break;
                    }
                    if (assign.Left is not VariableExpression target)
                    {
                        throw new Exception("native codegen: присвоєння підтримується лише у звичайну змінну, arr[i] чи obj.field");
                    }
                    if (_varOffsets.TryGetValue(target.Name, out int targetOffset))
                    {
                        CompileExpression(assign.Right);
                        if (_varTypes[target.Name] == ValType.Number)
                            _asm.AppendLine($"    movsd %xmm0, {targetOffset}(%ebp)");
                        else
                            _asm.AppendLine($"    mov %eax, {targetOffset}(%ebp)");
                        break;
                    }
                    // Глобальна (Фаза N7) - той самий "локальна затіняє
                    // глобальну" порядок пошуку, що VariableExpression вище.
                    if (_globalVars.TryGetValue(target.Name, out var globalType))
                    {
                        CompileExpression(assign.Right);
                        string label = $"__g_{target.Name}";
                        if (globalType == ValType.Number)
                            _asm.AppendLine($"    movsd %xmm0, {label}");
                        else
                            _asm.AppendLine($"    mov %eax, {label}");
                        break;
                    }
                    throw new Exception($"native codegen: змінна '{target.Name}' не оголошена");
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
                    else if (leftType is ValType.Int32 or ValType.UInt32)
                    {
                        CompileIntBinary(bin, leftType);
                    }
                    else if (leftType == ValType.String && _target == NativeTarget.NyxOSKernel
                             && (bin.Operator == "+" || bin.Operator == "-")
                             && InferExprType(bin.Right) == ValType.Number)
                    {
                        // Арифметика вказівників (Фаза N8, 16.09.2026) -
                        // ЛИШЕ для kernel-target: String тут - це "сирий
                        // вказівник у %eax" (те саме значення, що kmalloc
                        // повертає) - "ptr + N" зсуває адресу на N БАЙТІВ
                        // (як char* у C, НЕ як T* з масштабуванням на
                        // sizeof(T) - той масштаб рахує сам код мовою,
                        // множенням, як kheap.c вже робить руками для
                        // "sizeof(block_header_t)"). СВІДОМО не чіпає
                        // Linux/nyxos-userspace цілі - там рядки лишаються
                        // ЛИШЕ .rodata-текстом, арифметика над ними була б
                        // безглуздою (і небезпечною - .rodata read-only).
                        CompileExpression(bin.Left);   // адреса -> %eax
                        _asm.AppendLine("    push %eax");
                        CompileExpression(bin.Right);  // зсув (Number) -> %xmm0
                        _asm.AppendLine("    cvttsd2si %xmm0, %ecx");
                        _asm.AppendLine("    pop %eax");
                        _asm.AppendLine(bin.Operator == "+" ? "    add %ecx, %eax" : "    sub %ecx, %eax");
                    }
                    else if (leftType == ValType.String && _target == NativeTarget.NyxOSKernel
                             && (bin.Operator == "==" || bin.Operator == "!=")
                             && (InferExprType(bin.Right) == ValType.String || InferExprType(bin.Right) == ValType.Number))
                    {
                        // Порівняння вказівників (Фаза N8, 16.09.2026) -
                        // ЛИШЕ ідентичність АДРЕС (та сама "сира адреса в
                        // %eax"), НЕ порівняння ЗМІСТУ (strcmp) - точнісінько
                        // те, що потрібне для null-перевірок (kheap.c:
                        // "current != 0", "header->next == header" тощо).
                        // Правий операнд МОЖЕ бути Number (звичайний
                        // літерал 0 для "порівняй з null") - тоді
                        // конвертуємо його в ту саму 32-бітну адресну
                        // форму (cvttsd2si), а НЕ навпаки (вказівник як
                        // double не має сенсу). Результат - Bool (0/1 у
                        // %eax), як і всюди в решті компілятора.
                        CompileExpression(bin.Left);
                        _asm.AppendLine("    push %eax");
                        CompileExpression(bin.Right);
                        if (InferExprType(bin.Right) == ValType.Number)
                        {
                            _asm.AppendLine("    cvttsd2si %xmm0, %eax");
                        }
                        _asm.AppendLine("    mov %eax, %ecx");
                        _asm.AppendLine("    pop %eax");
                        _asm.AppendLine("    cmp %ecx, %eax");
                        _asm.AppendLine(bin.Operator == "==" ? "    sete %al" : "    setne %al");
                        _asm.AppendLine("    movzbl %al, %eax");
                    }
                    else if (leftType == ValType.String)
                    {
                        // ЖОДНИХ ІНШИХ операцій над рядками ще не
                        // підтримуємо - навіть ==/!= НЕ додаємо тут:
                        // CompileBoolBinary порівняв би просто АДРЕСИ
                        // (identity), а НЕ ЗМІСТ (потрібен strcmp), і
                        // незрозуміло, чи це взагалі збігається з тим, як
                        // порівнює рядки VM - чесна помилка компіляції
                        // краща за неперевірену, можливо хибну поведінку
                        // (Фаза N4+).
                        throw new Exception($"native codegen (Фаза N3): оператор '{bin.Operator}' для рядків ще не підтримується (потрібне порівняння ЗМІСТУ/strcmp - Фаза N4+)");
                    }
                    else if (leftType == ValType.Array)
                    {
                        throw new Exception($"native codegen (Фаза N3): оператор '{bin.Operator}' для масивів не підтримується (індексуйте arr[i] замість порівняння/арифметики над самим масивом)");
                    }
                    else if (leftType == ValType.Struct)
                    {
                        throw new Exception($"native codegen (Фаза N3): оператор '{bin.Operator}' для структур не підтримується (звертайтесь до полів obj.field замість порівняння/арифметики над самою структурою)");
                    }
                    else if (leftType == ValType.Closure)
                    {
                        throw new Exception($"native codegen (Фаза N4): оператор '{bin.Operator}' для замикань не підтримується (викличте f(...) замість порівняння/арифметики над самим значенням-функцією)");
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
        CompileExpression(bin.Left);           // -> %xmm0 (тип ЛІВОГО - Number, інакше викликач взагалі не потрапив би сюди - див. диспетчер вище)
        _asm.AppendLine("    sub $8, %esp");
        _asm.AppendLine("    movsd %xmm0, (%esp)");
        CompileExpression(bin.Right);           // -> %xmm0 (праве) АБО %eax, якщо праве НЕ Number
        // РЕАЛЬНИЙ БАГ, знайдений живим тестом (Фаза N8, 16.09.2026,
        // "6 & (5==4)" дало 4 замість правильних 0): якщо ПРАВИЙ
        // операнд - Bool (напр. результат ==/!=/</&&), його значення
        // приходить у %eax (0 чи 1), а НЕ в %xmm0 - без цієї конвертації
        // нижче код читав би СМІТТЯ з %xmm0 (що там лишилось з
        // попередньої SSE2-операції), а не 0.0/1.0. Той самий "конвертуй
        // РІВНО раз на межі" принцип, що вже є для kernel-параметрів
        // (Фаза N7).
        if (InferExprType(bin.Right) != ValType.Number)
        {
            _asm.AppendLine("    cvtsi2sd %eax, %xmm0");
        }
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
            // Побітові (Фаза N8, 16.09.2026; ВХІДНА конвертація виправлена
            // у Фазі N10, 22.09.2026 - EmitDoubleToUInt32 нижче) - чисел-
            // як-double тут НЕМАЄ побітової інструкції SSE2, тож округлюємо
            // ОБИДВА операнди в 32-бітні GPR, робимо ЦІЛОЧИСЕЛЬНУ операцію,
            // конвертуємо результат назад ЗВИЧАЙНИМ cvtsi2sd (signed).
            //
            // Фаза N10, РЕАЛЬНИЙ БАГ #1 (виправлено): вхід через ПРОСТИЙ
            // cvttsd2si давав ФІКСОВАНИЙ "integer indefinite" 0x80000000
            // для будь-якого double з |x| >= 2^31 (напр. акумулятор хешу,
            // що росте понад 2^31, - задокументовано раніше у Фазі N8.5e,
            // auth.c) - ВСЯ інформація губилась ще до самої операції.
            // EmitDoubleToUInt32 виправляє САМЕ це - коректно виділяє
            // нижні 32 біти будь-якого double з [0, 2^32).
            //
            // Фаза N10, РЕАЛЬНИЙ БАГ #2 (знайдений і ВІДКОЧЕНИЙ живим
            // тестом одразу після першої версії фіксу): вихід НАЗАД у
            // double спочатку теж переробили на "безанакове" відновлення
            // (EmitUInt32ToDouble) - але це ЛАМАЛО справжні від'ємні числа
            // ("-8 >> 1" давало 4294967292 замість -4, бо 0xFFFFFFFC як
            // бітовий патерн ОДНАКОВИЙ і для -4 signed, і для 4294967292
            // unsigned - без реального типу немає способу розрізнити ЦІ
            // ДВА випадки на виході). Вихід ЛИШЕНО звичайним signed
            // cvtsi2sd (як і VM - C#'s (int) теж signed) - для звичайних
            // чисел (уся регресія) це ІДЕНТИЧНО старій поведінці; для
            // великого акумулятора результат виглядатиме "зі знаком
            // навпаки" при друку (напр. 3000000000 надрукується як
            // -1294967296), АЛЕ лишається БІТОВО коректним і самоузгодженим
            // для будь-яких ПОДАЛЬШИХ побітових операцій/порівнянь/peek-
            // poke - саме так реально використовується хеш у auth.c
            // (порівнюється й далі хешується, не друкується як "гарне"
            // десяткове число). Чесно задокументовано, не приховано.
            case "&":
                EmitDoubleToUInt32("%xmm0", "%eax");
                EmitDoubleToUInt32("%xmm1", "%ecx");
                _asm.AppendLine("    and %ecx, %eax");
                _asm.AppendLine("    cvtsi2sd %eax, %xmm0");
                break;
            case "|":
                EmitDoubleToUInt32("%xmm0", "%eax");
                EmitDoubleToUInt32("%xmm1", "%ecx");
                _asm.AppendLine("    or %ecx, %eax");
                _asm.AppendLine("    cvtsi2sd %eax, %xmm0");
                break;
            case "^":
                EmitDoubleToUInt32("%xmm0", "%eax");
                EmitDoubleToUInt32("%xmm1", "%ecx");
                _asm.AppendLine("    xor %ecx, %eax");
                _asm.AppendLine("    cvtsi2sd %eax, %xmm0");
                break;
            case "<<":
                EmitDoubleToUInt32("%xmm0", "%eax");
                EmitDoubleToUInt32("%xmm1", "%ecx");
                _asm.AppendLine("    shl %cl, %eax");
                _asm.AppendLine("    cvtsi2sd %eax, %xmm0");
                break;
            case ">>":
                EmitDoubleToUInt32("%xmm0", "%eax");
                EmitDoubleToUInt32("%xmm1", "%ecx");
                _asm.AppendLine("    sar %cl, %eax");
                _asm.AppendLine("    cvtsi2sd %eax, %xmm0");
                break;
            default:
                throw new Exception($"native codegen (Фаза N3): оператор '{bin.Operator}' для чисел ще не підтримується");
        }
    }

    // Фаза N12 (22.09.2026): арифметика над СПРАВЖНІМИ Int32/UInt32 -
    // ЖОДНОГО double-round-trip узагалі (на відміну від побітових
    // операторів CompileNumberBinary вище, які мусять йти через
    // EmitDoubleToUInt32/EmitUInt32ToDouble, бо Number - це double) -
    // операнди вже лежать як сирі 4-байтові значення в %eax, звичайні
    // GPR-інструкції напряму. Це й є справжня, а не "обхідна", заміна
    // асемблерної цілочисельної арифметики.
    //
    // Signed (Int32) vs unsigned (UInt32) розрізняються РІВНО там, де це
    // реально впливає на результат - ділення/остача (idiv+cdq vs
    // div+xor %edx,%edx), порівняння (setl/setg/... vs setb/seta/...),
    // зсув вправо (sar - арифметичний, зберігає знак, vs shr - логічний,
    // заповнює нулями) - САМЕ ця остання різниця була НЕМОЖЛИВА у Фазі
    // N10 (там ">>" мусив лишатись ЗАВЖДИ sar, бо double не ніс
    // інформації про знаковість) - реальні типи тут нарешті закривають
    // ту прогалину. Додавання/віднімання/множення (add/sub/imul) дають
    // ІДЕНТИЧНИЙ бітовий результат для обох знаковостей (двійкове
    // доповнення) - той самий факт, що й у C.
    private void CompileIntBinary(BinaryExpression bin, ValType intType)
    {
        bool isSigned = intType == ValType.Int32;
        CompileExpression(bin.Left);   // -> %eax (Int32/UInt32 - без double)
        _asm.AppendLine("    push %eax");
        var rightType = InferExprType(bin.Right);
        CompileExpression(bin.Right);
        if (rightType == ValType.Number)
        {
            // Межа Number <-> Int32/UInt32 (Фаза N12) - конвертуємо РІВНО
            // тут, на межі, той самий "convert once at the boundary"
            // принцип, що вже є для kernel-параметрів (Фаза N7) і
            // побітових операторів (Фаза N10) - усередині ж
            // Int32/UInt32-світу після цього - лише GPR, без SSE2.
            EmitDoubleToUInt32("%xmm0", "%eax");
        }
        else if (rightType != intType)
        {
            throw new Exception($"native codegen (Фаза N12): змішування {intType} і {rightType} в операторі '{bin.Operator}' не підтримується - конвертуйте явно (toI32/toU32)");
        }
        _asm.AppendLine("    mov %eax, %ecx");   // праве -> ecx
        _asm.AppendLine("    pop %eax");         // ліве назад -> eax
        switch (bin.Operator)
        {
            case "+": _asm.AppendLine("    add %ecx, %eax"); break;
            case "-": _asm.AppendLine("    sub %ecx, %eax"); break;
            case "*": _asm.AppendLine("    imul %ecx, %eax"); break; // однаковий результат для signed/unsigned (нижні 32 біти)
            case "/":
                if (isSigned) { _asm.AppendLine("    cdq"); _asm.AppendLine("    idiv %ecx"); }
                else { _asm.AppendLine("    xor %edx, %edx"); _asm.AppendLine("    div %ecx"); }
                break;
            case "%":
                if (isSigned) { _asm.AppendLine("    cdq"); _asm.AppendLine("    idiv %ecx"); }
                else { _asm.AppendLine("    xor %edx, %edx"); _asm.AppendLine("    div %ecx"); }
                _asm.AppendLine("    mov %edx, %eax"); // остача (edx) - результат %, не частка
                break;
            case "==": _asm.AppendLine("    cmp %ecx, %eax"); _asm.AppendLine("    sete %al"); _asm.AppendLine("    movzbl %al, %eax"); break;
            case "!=": _asm.AppendLine("    cmp %ecx, %eax"); _asm.AppendLine("    setne %al"); _asm.AppendLine("    movzbl %al, %eax"); break;
            case "<": _asm.AppendLine("    cmp %ecx, %eax"); _asm.AppendLine(isSigned ? "    setl %al" : "    setb %al"); _asm.AppendLine("    movzbl %al, %eax"); break;
            case "<=": _asm.AppendLine("    cmp %ecx, %eax"); _asm.AppendLine(isSigned ? "    setle %al" : "    setbe %al"); _asm.AppendLine("    movzbl %al, %eax"); break;
            case ">": _asm.AppendLine("    cmp %ecx, %eax"); _asm.AppendLine(isSigned ? "    setg %al" : "    seta %al"); _asm.AppendLine("    movzbl %al, %eax"); break;
            case ">=": _asm.AppendLine("    cmp %ecx, %eax"); _asm.AppendLine(isSigned ? "    setge %al" : "    setae %al"); _asm.AppendLine("    movzbl %al, %eax"); break;
            case "&": _asm.AppendLine("    and %ecx, %eax"); break;
            case "|": _asm.AppendLine("    or %ecx, %eax"); break;
            case "^": _asm.AppendLine("    xor %ecx, %eax"); break;
            case "<<": _asm.AppendLine("    shl %cl, %eax"); break;
            case ">>": _asm.AppendLine(isSigned ? "    sar %cl, %eax" : "    shr %cl, %eax"); break;
            default:
                throw new Exception($"native codegen (Фаза N12): оператор '{bin.Operator}' для {intType} ще не підтримується");
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
            """);
        // Фаза N10 (22.09.2026) - РЕАЛЬНИЙ БАГ, знайдений живим тестом
        // одразу після виправлення побітових операторів: звичайний
        // cvttsd2si на |xmm0| >= 2^31 (напр. 3000000000.0) давав
        // ФІКСОВАНИЙ "integer indefinite" 0x80000000 замість правильних
        // 32 бітів - print(3000000000.0) друкував "2147483648" МОВЧКИ
        // НЕВІРНО, і це НЕ мало жодного стосунку до побітових операторів
        // (той самий баг, просто в ЦІЙ функції). Той самий
        // EmitDoubleToUInt32, що й побітові оператори вище (тут xmm0
        // ГАРАНТОВАНО невід'ємний - модуль уже взято вище).
        EmitDoubleToUInt32("%xmm0", "%esi"); // esi - ціла частина, обрізана до нуля, знадобиться двічі
        _asm.AppendLine("""
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
            """);
        // esi може мати встановленим старший біт (значення >= 2^31, той
        // самий Фаза N10 випадок) - звичайний cvtsi2sd прочитав би це ЯК
        // ВІД'ЄМНЕ (знакова конвертація), зламавши обчислення дробової
        // частини нижче (на відміну від побітових операторів, тут xmm0 -
        // КОНКРЕТНЕ відоме значення, яке треба надрукувати ТОЧНО, тому
        // неоднозначність "signed чи unsigned" з коментаря вище для
        // побітових операторів тут НЕ застосовна - потрібне справжнє
        // числове значення esi, а не просто узгоджений бітовий патерн).
        EmitUInt32ToDouble("%esi", "%xmm1");
        _asm.AppendLine("""
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
        _asm.AppendLine(".lcomm heap_ptr, 4");   // поточний bump-покажчик heap_alloc (0 = ще не ініціалізовано)
        _asm.AppendLine(".lcomm heap_end, 4");   // поточна межа (brk) виділеної області
        _asm.AppendLine(".lcomm exc_top, 4");    // вказівник на НАЙБЛИЖЧИЙ активний try-обробник (0 = немає)
        _asm.AppendLine(".lcomm exc_value, 8");  // значення останнього throw (Number - double)
    }

    // Аварійне завершення при throw БЕЗ жодного активного try/catch -
    // Фаза N4, спільна мітка (емітується ЗАВЖДИ, як і решта хелперів -
    // мертвий код, якщо throw у файлі взагалі немає). Свідомо НЕ
    // намагаємось "продовжити ніби нічого не сталось" - неперехоплений
    // виняток чесно завершує процес, як throw/uncaught у самій VM.
    private void EmitUnhandledThrowHelper()
    {
        _asm.AppendLine(".Lunhandled_throw:");
        _asm.AppendLine("    mov $1, %ebx");
        _asm.AppendLine($"    mov ${(_target == NativeTarget.Linux ? 1 : 0)}, %eax");
        _asm.AppendLine("    int $0x80");
    }

    // heap_alloc(розмір у %eax) -> %eax = вказівник на новий блок.
    // ВЛАСНИЙ, написаний ВРУЧНУ bump-розподілювач через syscall brk
    // (Linux, #45) - НЕ malloc/free з libc: цей компілятор свідомо НЕ
    // лінкується з libc ніде (сирі syscall'и всюди, ближче до того, що
    // знадобиться на freestanding NyxOS - дивись NATIVE_ROADMAP.md,
    // Фаза N3 item 9). Розмір округлюється до кратного 8 (вирівнювання
    // під double). ЧЕСНО, як і задокументовано в плані: НЕМАЄ free() -
    // пам'ять просто "тече" (прийнятно для коротких скриптових програм,
    // справжнє збирання сміття/lst - окрема, значно пізніша Фаза N4+).
    private void EmitHeapAllocHelper()
    {
        _asm.AppendLine("""
            heap_alloc:
                push %ebp
                mov %esp, %ebp
                push %ebx
                push %ecx

                add $7, %eax
                and $0xfffffff8, %eax
                mov %eax, %ecx              # ecx = запитаний розмір (округлений)

                cmpl $0, heap_ptr
                jne .Lha_inited
                # перша ініціалізація - brk(0) повертає ПОТОЧНИЙ break
                push %ecx
                mov $45, %eax                # syscall brk
                xor %ebx, %ebx
                int $0x80
                mov %eax, heap_ptr
                mov %eax, heap_end
                pop %ecx
            .Lha_inited:
                mov heap_ptr, %eax
                add %ecx, %eax
                cmp heap_end, %eax
                jbe .Lha_have_space

                # недостатньо місця - розширюємо через brk одразу з
                # запасом (64КБ), щоб не викликати syscall на КОЖНЕ
                # (навіть маленьке) виділення.
                push %ecx
                mov heap_end, %ebx
                add $65536, %ebx
                add %ecx, %ebx
                mov $45, %eax
                int $0x80
                mov %eax, heap_end
                pop %ecx
            .Lha_have_space:
                mov heap_ptr, %eax           # результат - старий bump-покажчик
                push %eax
                mov heap_ptr, %ebx
                add %ecx, %ebx
                mov %ebx, heap_ptr
                pop %eax

                pop %ecx
                pop %ebx
                pop %ebp
                ret
            """);
    }

    // Фаза N10 (22.09.2026, пряме прохання Sviatoslav'а закрити цю
    // прогалину по-справжньому в компіляторі): double<->32-біт для
    // побітових операторів. cvttsd2si на double ПОЗА діапазоном signed
    // int32 (тобто |x| >= 2^31) дає ФІКСОВАНИЙ "integer indefinite"
    // патерн 0x80000000, а НЕ обрізку до нижніх 32 бітів - саме це
    // ламало auth.c-подібний код із акумуляторами, що ростуть понад
    // 2^31 (задокументовано у Фазі N8.5e, обійдено там 24-бітною
    // маскою замість справжнього виправлення). Тут - стандартний трюк
    // GCC/Clang для double<->uint32 БЕЗ SSE4.1 cvttsd2usi (якої немає
    // в базовому SSE2, на якому тримається увесь компілятор): для
    // x у [2^31, 2^32) віднімаємо 2^31 (тепер влазить у ЗНАКОВИЙ
    // діапазон), конвертуємо, повертаємо старший біт назад через xor;
    // симетрично в інший бік.
    //
    // Це НЕ порушує VM-паритет для жодного значення, що раніше
    // проходило регресійні тести: для x у [-2^31, 2^31) обидва шляхи
    // (старий і новий) дають ІДЕНТИЧНИЙ результат (гілка "уже влазить"
    // нижче спрацьовує без жодної зміни). Розбіжність можлива ЛИШЕ для
    // |x| >= 2^31 - а там VM (C#, unchecked double->int) сама дає
    // НЕВИЗНАЧЕНУ платформозалежну поведінку, тобто усталеного VM-
    // еталону, з яким тут можна було б розійтись, просто не існує.
    private string? _u32ConstLabel;
    private string U32ConstLabel => _u32ConstLabel ??= EmitDoubleLiteral(2147483648.0); // 2^31, одна спільна константа на весь файл

    private void EmitDoubleToUInt32(string srcXmm, string dstReg)
    {
        int id = _labelCounter++;
        _asm.AppendLine($"    movsd {U32ConstLabel}, %xmm2");
        _asm.AppendLine($"    ucomisd %xmm2, {srcXmm}");
        _asm.AppendLine($"    jb .Lu32lo{id}");
        _asm.AppendLine($"    subsd %xmm2, {srcXmm}");
        _asm.AppendLine($"    cvttsd2si {srcXmm}, {dstReg}");
        _asm.AppendLine($"    xor $0x80000000, {dstReg}");
        _asm.AppendLine($"    jmp .Lu32done{id}");
        _asm.AppendLine($".Lu32lo{id}:");
        _asm.AppendLine($"    cvttsd2si {srcXmm}, {dstReg}");
        _asm.AppendLine($".Lu32done{id}:");
    }

    private void EmitUInt32ToDouble(string srcReg, string dstXmm)
    {
        int id = _labelCounter++;
        _asm.AppendLine($"    test {srcReg}, {srcReg}");
        _asm.AppendLine($"    js .Lu32hi{id}");
        _asm.AppendLine($"    cvtsi2sd {srcReg}, {dstXmm}");
        _asm.AppendLine($"    jmp .Lu32bdone{id}");
        _asm.AppendLine($".Lu32hi{id}:");
        _asm.AppendLine($"    mov {srcReg}, %edx");
        _asm.AppendLine($"    and $0x7fffffff, %edx");
        _asm.AppendLine($"    cvtsi2sd %edx, {dstXmm}");
        _asm.AppendLine($"    movsd {U32ConstLabel}, %xmm2");
        _asm.AppendLine($"    addsd %xmm2, {dstXmm}");
        _asm.AppendLine($".Lu32bdone{id}:");
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
