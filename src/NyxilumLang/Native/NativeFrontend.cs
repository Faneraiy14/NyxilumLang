using System.Text;
using NyxilumLang.AST;

namespace NyxilumLang.Native;

// Спільна "передня частина" нативних бекендів (x86 - NativeCodegen,
// ARM64 - NativeCodegenArm64): стан компіляції функції (слоти змінних,
// типи, структури, методи, лямбди, вивід асемблера) і АНАЛІЗ, що не
// залежить від процесора - статичний вивід типів (InferExprType), збір
// змінних і слотів кадру (CollectVarsAndTypes), вільні змінні замикань
// (FindFreeVars), sized-int/ptr-анотації. Винесено ДОСЛІВНО з
// NativeCodegen.cs (30.09.2026) - x86-асемблер для всіх tests/native і
// native-examples перевірено побайтово ідентичним до й після.
public abstract class NativeFrontend
{
    // Розмір "кадру обробника" try/catch у слотах кадру функції: x86 -
    // 4 поля по 4 байти (esp, ebp, мітка catch, попередній обробник),
    // ARM64 - ті самі 4 поля по 8 байтів.
    protected virtual int TryFrameBytes => 16;

    protected readonly StringBuilder _asm = new();

    // Кожна функція компілюється ОКРЕМО зі СВОЄЮ картою змінних (локальні
    // змінні однієї функції НЕ мають бачити слоти іншої) - ці поля
    // скидаються на початку CompileFunction() для КОЖНОЇ функції.
    protected Dictionary<string, int> _varOffsets = new();
    protected Dictionary<string, ValType> _varTypes = new();
    // ЛИШЕ для ValType.Struct - яка САМЕ структура (StructDeclaration.Name),
    // щоб знати offset'и полів при member-доступі. НЕ скидається між
    // функціями окремо (скидається разом із _varTypes в CompileFunction).
    protected Dictionary<string, string> _varStructName = new();
    // ЛИШЕ для ValType.Ptr (Фаза N14) - тип ЕЛЕМЕНТА, на який вказує
    // ця ЛОКАЛЬНА змінна/параметр (ptr<int32> -> ValType.Int32 тощо) -
    // той самий патерн, що _varStructName вище. Скидається разом із
    // _varTypes (НЕ для globals/return-типів функцій - ті в
    // _globalPtrElementType нижче, живуть довше одного компільованого
    // тіла функції).
    protected Dictionary<string, ValType> _ptrElementType = new();
    // try/catch (Фаза N4) - для КОЖНОГО TryStatement-вузла в ЦІЙ функції
    // (за посиланням на сам AST-вузол, не за іменем - їх немає) offset
    // 16-байтового "кадру обробника" (setjmp/longjmp-стиль, дивись
    // коментар над CompileTryStatement). _tryDepth - чи компілюємо
    // ЗАРАЗ щось УСЕРЕДИНІ try/catch-блоку (для чесної заборони
    // return/break/continue звідти - дивись коментар нижче).
    protected Dictionary<TryStatement, int> _tryFrameOffsets = new();
    protected int _tryDepth;
    protected int _nextLocalOffset;
    protected string _epilogueLabel = "";
    protected bool _isMain;
    // Чи компілюємо ЗАРАЗ функцію для --target nyxos-kernel (Фаза N7) -
    // впливає на конвенцію return (int у %eax через C ABI, а не
    // xmm0/exit-syscall) у ReturnStatement нижче.
    protected bool _isKernelExport;

    // Глобальні змінні верхнього рівня (Фаза N7, лише --target
    // nyxos-kernel - справжні kernel-модулі, як gconsole.c, тримають
    // стан МІЖ викликами функцій - той самий сенс, що static-змінні в
    // C). ІМ'Я -> тип; мітка в асемблері - завжди "__g_{ім'я}". НЕ
    // скидається між функціями (на відміну від _varOffsets) -
    // заповнюється ОДИН РАЗ у CompileKernelObject.
    protected readonly Dictionary<string, ValType> _globalVars = new();
    protected readonly StringBuilder _globalData = new();
    // ЛИШЕ для ValType.Ptr (Фаза N14) - тип елемента для ГЛОБАЛЬНИХ
    // вказівникових змінних (ІМ'Я змінної) ТА для kernel-функцій, чий
    // ОГОЛОШЕНИЙ РЕЗУЛЬТАТ - ptr<T> (ІМ'Я функції) - об'єднано в ОДИН
    // словник, оскільки простори імен змінних/функцій тут і так ніколи
    // не перетинаються (той самий факт, яким уже користуються
    // _globalVars/_kernelFuncsByName нарізно). НІКОЛИ не скидається -
    // заповнюється ОДИН РАЗ (глобальні - у циклі глобальних нижче,
    // функції - разом із _kernelFuncsByName).
    protected readonly Dictionary<string, ValType> _globalPtrElementType = new();

    // Заповнюється ОДИН РАЗ на весь файл у Compile() (НЕ скидається між
    // функціями, на відміну від _varOffsets/_varTypes) - структури
    // оголошуються на верхньому рівні, доступні звідусіль.
    protected Dictionary<string, Dictionary<string, int>> _structFieldOffsets = new();

    // structName -> methodName -> оголошення (Фаза N4). Асемблерна мітка
    // методу - завжди "{structName}__{methodName}" (уникає колізій між
    // однойменними методами РІЗНИХ структур, напр. Dog.speak() і
    // Cat.speak()).
    protected Dictionary<string, Dictionary<string, FunctionDeclaration>> _structMethods = new();

    // Лямбди (Фаза N4) - виявляються ЛІНИВО, під час компіляції виразів
    // (CompileExpression{FunctionExpression}), і ставляться в ЧЕРГУ на
    // компіляцію тіла ПІЗНІШЕ (не можна компілювати тіло лямбди ПРЯМО
    // ПОСЕРЕД тіла функції, що її створює - зламало б .text-структуру
    // поточної функції, що компілюється). Черга, а не єдиний прохід,
    // бо тіло ОДНІЄЇ лямбди може містити ЩЕ ОДНУ вкладену лямбду.
    protected readonly List<(string Label, FunctionExpression Expr, List<string> FreeVars)> _pendingLambdas = new();
    protected int _lambdaCounter;

    protected int _labelCounter;
    protected readonly Stack<(string Start, string End)> _loopLabels = new();
    protected HashSet<string> _knownFunctions = new();

    // Фаза N8.5 (18.09.2026): kernel-target функції можуть ОГОЛОСИТИ тип
    // результату явно (func f(...) -> string {...}) - Parser.cs це вже
    // давно парсить у FunctionDeclaration.ReturnType, але NativeCodegen
    // досі це поле НІКОЛИ не читав і завжди вважав будь-яку відому
    // функцію Number-результатом (та сама межа, що вже впиралась у
    // kheap.c - kmalloc-подібна функція не могла повернути покажчик).
    // Заповнюється ОДИН РАЗ у CompileKernelObject - лише для kernel-target
    // (звичайні функції й так СТРОГО Number, дивись ReturnStatement).
    protected Dictionary<string, FunctionDeclaration> _kernelFuncsByName = new();
    // Яку kernel-функцію зараз компілюємо - потрібно в ReturnStatement,
    // щоб звірити РЕАЛЬНИЙ тип значення з явною анотацією (якщо вона є).
    protected FunctionDeclaration? _currentKernelFunc;

    // Рядкові й дробові літерали - у .rodata, кожен під СВОЄЮ міткою
    // (.Lstr0/.Ldbl0, ...) - записуємо в НАКОПИЧЕНУ секцію одразу, коли
    // зустрічаємо (не окремий прохід по AST заздалегідь). Спільний
    // лічильник для обох - безпечно, бо повна мітка включає префікс.
    protected readonly StringBuilder _rodata = new();
    protected int _stringLabelCounter;
    protected NativeTarget _target;


    // Виділяє слот КОЖНІЙ локальній змінній (8 байтів - Фаза N3) і
    // ОДРАЗУ визначає її статичний тип із власного ініціалізатора -
    // працює коректно, бо мова вимагає оголошення ЗМІННОЇ ДО
    // використання, а обхід тут іде в тому ж порядку, що й виконання.
    protected void CollectVarsAndTypes(BlockStatement block)
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
                        if (type == ValType.Ptr && v.Initializer != null)
                        {
                            // Фаза N14: "var p = otherPtr + 1;" - тип
                            // ЕЛЕМЕНТА не можна вивести з самого ValType
                            // (bare enum) - переносимо його з
                            // ІНІЦІАЛІЗАТОРА, той самий принцип, що
                            // _varStructName щойно вище для Struct.
                            var pet = InferPtrElementType(v.Initializer)
                                ?? throw new Exception($"native codegen (Фаза N14): не вдалося визначити тип елемента вказівника для '{v.Name}'");
                            _ptrElementType[v.Name] = pet;
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
                        _nextLocalOffset -= TryFrameBytes;
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

    // Вільні змінні лямбди - усі VariableExpression-посилання в тілі,
    // що НЕ є ні власним параметром, ні власною локальною змінною
    // (оголошеною ВСЕРЕДИНІ тіла). СПРОЩЕННЯ: НЕ рекурсує у тіло
    // ВКЛАДЕНОЇ лямбди (її власні захоплення обробляються окремо, коли
    // компілюється ВОНА САМА, використовуючи БЕЗПОСЕРЕДНЬО охоплюючий
    // контекст, у якому і captured-змінні зовнішньої лямбди - на той
    // момент уже ЗВИЧАЙНІ локальні слоти - дивись коментар над
    // ValType.Closure) - багаторівневе вкладення тому "просто працює"
    // без додаткового коду.
    protected List<string> FindFreeVars(FunctionExpression fn)
    {
        var bound = new HashSet<string>(fn.Parameters.Select(p => p.Name));
        var free = new List<string>();
        var seen = new HashSet<string>();
        CollectFreeVarsInBlock(fn.Body, bound, free, seen);
        return free;
    }

    protected void CollectFreeVarsInBlock(BlockStatement block, HashSet<string> bound, List<string> free, HashSet<string> seen)
    {
        foreach (var stmt in block.Statements)
        {
            CollectFreeVarsInStmt(stmt, bound, free, seen);
        }
    }

    protected void CollectFreeVarsInStmt(StatementNode stmt, HashSet<string> bound, List<string> free, HashSet<string> seen)
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

    protected void CollectFreeVarsInExpr(ExpressionNode expr, HashSet<string> bound, List<string> free, HashSet<string> seen)
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
    protected ValType InferExprType(ExpressionNode expr) => expr switch
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
        // Фаза N12/N13: '~' на sized-int-операнді лишається тим самим
        // типом (той самий принцип, що унарний '-' вище) - лише "гола"
        // Number-версія (без реальних цілих типів) типізується як
        // Number, як і раніше.
        UnaryExpression { Operator: "~" } uTilde when IsSizedIntType(InferExprType(uTilde.Operand))
            => InferExprType(uTilde.Operand),
        UnaryExpression { Operator: "~" } => ValType.Number,
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
        // toI8/toU8/toI16/toU16/toI32/toU32/toNumber (Фаза N12/N13) -
        // явні конвертери на МЕЖІ між Number-світом (double) і
        // справжніми sized-int типами (GPR) - той самий "convert once
        // at the boundary" принцип, що numToPtr вище (НЕ неявна
        // конвертація десь усередині виразу).
        CallExpression { FunctionName: var toFn } when _target == NativeTarget.NyxOSKernel && SizedIntConversionTarget(toFn) is { } toType => toType,
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
        // Фаза N14: те саме, що String-арифметика вище, для СПРАВЖНІХ
        // типізованих вказівників - "typedPtr + N" ЛИШАЄТЬСЯ Ptr (з
        // ТИМ САМИМ елементом - InferPtrElementType(bin.Left) пропускає
        // його крізь ланцюжок), не занижується до Number.
        BinaryExpression { Operator: "+" or "-" } typedPtrArith when _target == NativeTarget.NyxOSKernel
            && InferExprType(typedPtrArith.Left) == ValType.Ptr && InferExprType(typedPtrArith.Right) == ValType.Number
            => ValType.Ptr,
        // Фаза N12/N13: арифметика/побітові над sized-int-лівим
        // операндом лишаються ТИМ САМИМ типом (не "занижуються" назад
        // до Number) - саме ЦЕ дозволяє ланцюжок "a + b - c" лишатись
        // у GPR-світі без жодного double-round-trip на кожному кроці.
        // Порівняння вище (== < > і т.д.) вже коректно завжди Bool
        // незалежно від типу операндів.
        BinaryExpression intBin when IsSizedIntType(InferExprType(intBin.Left))
            => InferExprType(intBin.Left),
        BinaryExpression => ValType.Number, // + - * %
        ArrayLiteralExpression => ValType.Array,
        // Фаза N14: "typedPtr[i]" (масштабоване, типізоване
        // читання) - ТИП РЕЗУЛЬТАТУ - тип ЕЛЕМЕНТА вказівника, не
        // завжди Number (перевіряється ПЕРШИМ, до блáнкет-випадку
        // масивів/рядків нижче).
        IndexExpression ixPtr when InferExprType(ixPtr.Array) == ValType.Ptr
            => InferPtrElementType(ixPtr.Array) ?? throw new Exception("native codegen (Фаза N14): не вдалося визначити тип елемента для індексування вказівника"),
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
    protected string ResolveStructName(ExpressionNode expr) => expr switch
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
    protected static readonly Dictionary<string, ValType> ExternalKernelReturnTypes = new()
    {
        ["kmalloc"] = ValType.String,          // kheap.c - void* -> вказівник
        ["gfx_is_available"] = ValType.Bool,    // gfx.c - int, семантично bool (0/1)
        ["scheduler_tick"] = ValType.String,    // process.c - повертає СИРУ адресу ESP
                                                 // (uint32_t, що isr.s підставляє як
                                                 // новий стек) - НЕ число, cvtsi2sd
                                                 // спотворив би біти адреси (timer.c).
    };

    protected ValType ExternalKernelReturnType(string functionName) =>
        ExternalKernelReturnTypes.TryGetValue(functionName, out var t) ? t : ValType.Number;

    // Той самий словник типів, що вже й для kernel-параметрів (дивись
    // CompileKernelFunction) - "string" -> покажчик, числові псевдоніми
    // -> Number, bool -> Bool. Немає анотації (ReturnType == null) ->
    // старий типовий Number (без анотації - без зміни поведінки,
    // ЖОДНА раніше робоча kernel-функція цим фіксом не ламається).
    protected ValType KernelReturnTypeFromAnnotation(FunctionDeclaration func) => func.ReturnType switch
    {
        null => ValType.Number,
        "string" => ValType.String,
        "bool" => ValType.Bool,
        "any" or "i32" or "f64" or "int" or "number" or "size_t" or "u32" or "usize" => ValType.Number,
        _ when PtrElementTypeFromAnnotation(func.ReturnType) != null => ValType.Ptr,
        _ => SizedIntFromAnnotation(func.ReturnType)
            ?? throw new Exception($"native codegen (Фаза N7): тип результату '{func.ReturnType}' функції '{func.Name}' не підтримується для --target nyxos-kernel - лише string/bool/int8/uint8/int16/uint16/int32/uint32/ptr<T>/число (напр. -> string)")
    };

    // Фаза N12/N13: чи це взагалі один із "справжніх" сирих
    // цілочисельних типів (GPR, не double) - і його (знаковість,
    // ширина_в_байтах). Один-єдиний джерело істини, яким користуються
    // ВСІ місця файлу, що мають розрізняти ці типи (замість
    // розкиданих "== ValType.Int32 || == ValType.UInt32" по всьому
    // файлу - Фаза N13 додала 4 нових варіанти, і без цієї
    // централізації довелось би правити кожне таке місце вручну ще
    // раз).
    protected static bool IsSizedIntType(ValType t) => t is ValType.Int8 or ValType.UInt8
        or ValType.Int16 or ValType.UInt16 or ValType.Int32 or ValType.UInt32;

    protected static (bool IsSigned, int WidthBytes) IntTypeInfo(ValType t) => t switch
    {
        ValType.Int8 => (true, 1),
        ValType.UInt8 => (false, 1),
        ValType.Int16 => (true, 2),
        ValType.UInt16 => (false, 2),
        ValType.Int32 => (true, 4),
        ValType.UInt32 => (false, 4),
        _ => throw new Exception($"native codegen (Фаза N12/N13): {t} - не цілочисельний sized-тип")
    };

    // Компілятивно-часова версія EmitNarrowExtend - для СТАЛИХ значень
    // глобальних змінних (немає рантайм-%eax, обрізаємо С#-стороною,
    // той самий двійково-доповнений результат, що дало б viконання на
    // процесорі).
    protected static int TruncateConstToIntType(double d, ValType t)
    {
        int full = unchecked((int)(long)d);
        var (isSigned, width) = IntTypeInfo(t);
        return width switch
        {
            1 => isSigned ? (sbyte)full : (byte)full,
            2 => isSigned ? (short)full : (ushort)full,
            _ => full
        };
    }

    // Той самий словник типів, що вже є для kernel-параметрів/return-
    // типів/глобальних (Фаза N7+) - винесено ОДИН раз (Фаза N13), щоб
    // 3 місця файлу, що читають анотацію "int8"/"uint16"/тощо, не
    // дублювали один і той самий switch утретє. null, якщо рядок - НЕ
    // назва sized-int типу (виклик-майданчик сам вирішує, що робити
    // далі - кинути помилку чи спробувати іншу гілку).
    protected static ValType? SizedIntFromAnnotation(string? annotation) => annotation switch
    {
        "int8" => ValType.Int8,
        "uint8" => ValType.UInt8,
        "int16" => ValType.Int16,
        "uint16" => ValType.UInt16,
        "int32" => ValType.Int32,
        "uint32" => ValType.UInt32,
        _ => null
    };

    // toI8/toU8/.../toU32 (Фаза N12/N13) - назва функції-конвертера ->
    // цільовий тип. Окремо від SizedIntFromAnnotation вище (та мапить
    // ТИПОВІ анотації, ця - ІМЕНА функцій-інтринзиків) - різні простори
    // імен, що лише збігаються буквами.
    protected static ValType? SizedIntConversionTarget(string functionName) => functionName switch
    {
        "toI8" => ValType.Int8,
        "toU8" => ValType.UInt8,
        "toI16" => ValType.Int16,
        "toU16" => ValType.UInt16,
        "toI32" => ValType.Int32,
        "toU32" => ValType.UInt32,
        _ => null
    };

    // Фаза N14: "ptr<T>" - ЄДИНА нова форма анотації (не окреме слово,
    // як "int32" тощо) - розпізнається ЧИСТО текстовим префіксом/
    // суфіксом (Parser.cs і так зберігає анотації як вільний рядок,
    // ЖОДНИХ змін Lexer/Parser не треба, той самий принцип, що всі
    // sized-int анотації). T - лише ОДИН із уже наявних sized-int
    // типів чи "number" (String/Bool/вкладені ptr<ptr<T>> елементи -
    // СВІДОМО поза обсягом першої версії, чесна помилка компіляції).
    protected static ValType? PtrElementTypeFromAnnotation(string? annotation)
    {
        if (annotation == null || !annotation.StartsWith("ptr<") || !annotation.EndsWith(">")) return null;
        string inner = annotation.Substring(4, annotation.Length - 5);
        return inner == "number"
            ? ValType.Number
            : SizedIntFromAnnotation(inner)
                ?? throw new Exception($"native codegen (Фаза N14): непідтримуваний тип елемента вказівника 'ptr<{inner}>' - лише ptr<number>/ptr<int8>/ptr<uint8>/ptr<int16>/ptr<uint16>/ptr<int32>/ptr<uint32>");
    }

    // Реєструє тип елемента ЛОКАЛЬНОЇ/параметра (_ptrElementType) чи
    // ГЛОБАЛЬНОЇ/функції-результату (_globalPtrElementType) і повертає
    // ValType.Ptr - зручно викликати ПРЯМО всередині switch-виразу
    // (побічний ефект + повернене значення одним викликом), той самий
    // трюк, що вже дав компактний "int32"/"uint32"-код у Фазах N12/N13.
    protected ValType RegisterLocalPtrType(string name, ValType elementType)
    {
        _ptrElementType[name] = elementType;
        return ValType.Ptr;
    }

    protected ValType RegisterGlobalPtrType(string name, ValType elementType)
    {
        _globalPtrElementType[name] = elementType;
        return ValType.Ptr;
    }

    // Тип ЕЛЕМЕНТА (не самого вказівника - той уже ValType.Ptr) для
    // ДОВІЛЬНОГО виразу, що дає вказівник (Фаза N14) - потрібен окремо
    // від InferExprType, бо ValType (bare enum) не може нести параметр
    // типу. Локальна змінна/параметр затіняє глобальну/функцію (той
    // самий порядок пошуку, що VariableExpression у InferExprType) -
    // арифметика над вказівником (ptr +- N) ЗБЕРІГАЄ тип елемента
    // ЛІВОГО операнда (як у C: T* + int лишається T*).
    protected ValType? InferPtrElementType(ExpressionNode expr) => expr switch
    {
        VariableExpression v when _ptrElementType.TryGetValue(v.Name, out var lt) => lt,
        VariableExpression v when _globalPtrElementType.TryGetValue(v.Name, out var gt) => gt,
        BinaryExpression { Operator: "+" or "-" } bin when InferExprType(bin.Left) == ValType.Ptr => InferPtrElementType(bin.Left),
        CallExpression call when _globalPtrElementType.TryGetValue(call.FunctionName, out var ct) => ct,
        _ => null
    };
}
