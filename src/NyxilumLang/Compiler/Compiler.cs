using NyxilumLang.AST;
using NyxilumLang.Runtime;

namespace NyxilumLang.Compiler;

public class Compiler
{
    private Bytecode? _bytecode;
    private Dictionary<string, int> _vars = new();
    private readonly Dictionary<string, int> _functions = new();
    private int _varCounter;

    // Усі структури програми за іменем — потрібно і для проставлення
    // аліасів успадкованих методів (ResolveInheritance), і для резолвингу
    // super.method() (ResolveMethodOwnerInChain), обидва - по AST, не по
    // вже скомпільованому байткоду, тому порядок оголошення структур у
    // файлі не має значення.
    private readonly Dictionary<string, StructDeclaration> _structsByName = new();
    // Батько СТРУКТУРИ, чий МЕТОД компілюється просто зараз (null - як тільки
    // компілюється не метод, або метод структури без extends) - потрібен,
    // щоб super.method() усередині нього резолвився статично, а не через
    // CALL_METHOD (яка завжди диспетчерізує по runtime __type self, тобто
    // знову викликала б НАЙБІЛЬШ похідне перевизначення - нескінченна
    // рекурсія замість виклику батьківської реалізації).
    private string? _currentStructParent;

    // Вкладена func (усередині іншої func) реєструється під УТОЧНЕНИМ
    // іменем "батько::дитина" (не голим "дитина"), інакше однакова назва
    // вкладеної функції у двох різних "батьках" тихо перезаписувала б
    // одна одну в спільному словнику _functions. Стек — ланцюжок "батьків"
    // поточної функції, що компілюється/реєструється просто зараз;
    // порожній на верхньому рівні файлу.
    private readonly List<string> _funcNestingStack = new();

    private string QualifyFunctionName(string name) =>
        _funcNestingStack.Count == 0 ? name : string.Join("::", _funcNestingStack) + "::" + name;

    // Виклик "foo()" усередині функції може означати: вкладену функцію в
    // ПОТОЧНІЙ функції, або в якомусь із "батьків" (лексична видимість —
    // від найближчого до найдальшого), або звичайну функцію верхнього
    // рівня. Повертає перше знайдене уточнене ім'я, або null, якщо це не
    // вкладена/верхньорівнева функція взагалі (виклик — builtin/змінна/
    // помилка вирішується викликачем).
    private string? ResolveFunctionName(string name)
    {
        for (int i = _funcNestingStack.Count; i >= 0; i--)
        {
            string candidate = i == 0 ? name : string.Join("::", _funcNestingStack.Take(i)) + "::" + name;
            if (_functions.ContainsKey(candidate)) return candidate;
        }
        return null;
    }

    // extends на невідому структуру або цикл (A extends B extends A) мають
    // впасти одразу й зрозуміло, а не мовчки зациклити компілятор пізніше
    // в ResolveInheritance/ResolveMethodOwnerInChain.
    private void ValidateInheritanceChains()
    {
        foreach (var s in _structsByName.Values)
        {
            var seen = new HashSet<string> { s.Name };
            var current = s.ParentName;
            while (current != null)
            {
                if (!_structsByName.ContainsKey(current))
                    throw new Exception($"Структура '{s.Name}' успадковує невідому структуру '{current}' (extends {current})");
                if (!seen.Add(current))
                    throw new Exception($"Циклічне успадкування виявлено біля структури '{s.Name}'");
                current = _structsByName[current].ParentName;
            }
        }
    }

    // Для кожної структури з extends: методи, які предок(и) оголошують, а
    // сама структура (і жоден БЛИЖЧИЙ предок) не перевизначає, отримують
    // аліас "Дитина.метод" на ту саму адресу. Завдяки цьому CALL_METHOD
    // (яка завжди резолвить "{runtime __type self}.{метод}") знаходить
    // успадкований метод так само, як власний, - а виклик перевизначеного
    // методу з БАТЬКІВСЬКОГО коду (self.метод() усередині методу предка)
    // автоматично дає поліморфізм: self має __type найбільш ПОХІДНОЇ
    // структури, тому знайде саме її перевизначення, не батьківське.
    private void ResolveInheritance()
    {
        foreach (var s in _structsByName.Values)
        {
            if (s.ParentName == null) continue;

            var ownMethodNames = new HashSet<string>(s.Methods.Select(m => m.Name.Split('.')[^1]));
            var current = s.ParentName;
            while (current != null && _structsByName.TryGetValue(current, out var ancestor))
            {
                foreach (var m in ancestor.Methods)
                {
                    var shortName = m.Name.Split('.')[^1];
                    if (ownMethodNames.Contains(shortName)) continue;
                    string aliasKey = $"{s.Name}.{shortName}";
                    // Уже проставлено ближчим предком у цьому ж проході - той має пріоритет.
                    if (_functions.ContainsKey(aliasKey)) continue;
                    string sourceKey = $"{current}.{shortName}";
                    if (_functions.TryGetValue(sourceKey, out int addr))
                    {
                        _functions[aliasKey] = addr;
                        _bytecode!.FunctionAddresses[aliasKey] = addr;
                    }
                }
                current = ancestor.ParentName;
            }
        }
    }

    // super.method() має викликати САМЕ РЕАЛІЗАЦІЮ ланцюжка extends, що
    // починається з startStructName (батько структури, чий метод зараз
    // компілюється), а не найбільш похідне перевизначення - тому шукаємо
    // по AST (де метод РЕАЛЬНО оголошений), а не через CALL_METHOD.
    private string? ResolveMethodOwnerInChain(string? startStructName, string methodName)
    {
        var current = startStructName;
        var seen = new HashSet<string>();
        while (current != null && seen.Add(current) && _structsByName.TryGetValue(current, out var decl))
        {
            if (decl.Methods.Any(m => m.Name.Split('.')[^1] == methodName))
                return current;
            current = decl.ParentName;
        }
        return null;
    }

    // ВИПРАВЛЕНО: функції компілюються по черзі, в порядку оголошення, тому
    // виклик функції, яка оголошена ПІЗНІШЕ у файлі (напр. main викликає
    // factorial, а factorial оголошена нижче), раніше отримував адресу -1
    // (заглушку з RegisterFunctions), бо реальна адреса ще не була відома
    // на момент компіляції цього виклику. Тепер такі виклики відкладаються
    // і патчаться правильною адресою після того, як усі функції скомпільовано.
    private readonly List<(int Position, string FunctionName)> _pendingCalls = new();
    // Так само, як _pendingCalls, але для посилань на іменовану функцію ЯК
    // ЗНАЧЕННЯ (без виклику) — патчимо NxFunctionRef.Address напряму,
    // оскільки він лежить у таблиці констант як живий об'єкт.
    private readonly List<(NxFunctionRef Ref, string FunctionName)> _pendingFunctionRefs = new();

    // break/continue: на момент компіляції тіла циклу адреса його кінця ще
    // невідома, тому позиції переходів запам'ятовуються і патчаться, коли
    // цикл скомпільовано повністю. Стек — бо цикли бувають вкладені, і
    // break має вести з НАЙБЛИЖЧОГО циклу.
    private class LoopContext
    {
        public List<int> BreakJumps = new();     // патчаться на адресу після циклу
        public List<int> ContinueJumps = new();  // патчаться на адресу наступної ітерації
    }
    private readonly Stack<LoopContext> _loops = new();

    // Глобальні var (верхній рівень файлу). Заповнюється ПЕРЕД компіляцією
    // тіл функцій, щоб функція, оголошена ДЕ ЗАВГОДНО у файлі, бачила
    // глобальну змінну — незалежно від того, до чи після неї в тексті
    // ця змінна написана. Компілюються через GET_GLOBAL/SET_GLOBAL (за
    // іменем, не за номером слота): у функцій свій _varCounter з нуля,
    // тому спільного номера слота для глобальних просто не існує.
    private readonly HashSet<string> _globalNames = new();
    // true лише поки компілюється код верхнього рівня файлу (не всередині
    // жодної func) — саме там var повинен ставати ГЛОБАЛЬНИМ, а не
    // локальною змінною поточної (відсутньої) функції.
    private bool _atTopLevel;

    private static readonly HashSet<string> _builtins = new()
    {
        "print", "printNoNewLine",
        "readLine", "readInt", "readDouble",
        "readFile", "writeFile", "appendFile", "fileExists", "readLines",
        "deleteFile", "makeDir", "dirExists", "deleteDir", "listDir",
        "sqrt", "abs", "pow", "sin", "cos", "tan",
        "round", "floor", "ceil", "max", "min", "clamp",
        "toString", "toInt", "toDouble", "toFixed", "len",
        "substring", "replace", "toUpper", "toLower", "contains", "startsWith", "endsWith", "split", "join",
        "trim", "repeat", "indexOf", "reverse",
        "append", "pop", "removeAt", "insert", "clear", "slice", "unique",
        "randomInt", "randomDouble",
        "now", "today", "timestamp", "formatDate", "parseDate", "sleep",
        "typeOf", "isNumber", "isString", "isArray", "isBool", "isNull",
        "charCode", "fromCharCode",
        "newMap", "mapSet", "mapGet", "mapHas", "mapRemove", "mapKeys", "mapValues",
        "sort", "mapArr", "filter", "reduce", "toJson", "fromJson", "callWithArgs",
        "osPlatform", "osArchitecture", "osMemory", "osCpuCount", "osEnv", "osCwd",
        "httpServer", "httpGet", "urlStatus", "httpPost", "httpRequest",
        "regexTest", "regexMatch", "regexFindAll", "regexReplace",
        "wsConnect", "wsSend", "wsReceive", "wsClose",
        "spawn", "workerJoin", "newChannel", "channelSend", "channelReceive",
        "createCanvas", "clearCanvas", "drawRect", "drawCircle", "drawLine", "drawText",
        "presentCanvas", "canvasShouldClose", "closeCanvas",
        "isKeyDown", "isMouseDown", "getMouseX", "getMouseY", "project3D",
        "guiWindow", "guiButton", "guiLabel", "guiTextBox", "guiAdd",
        "guiOnAction", "guiShow", "guiSetText", "guiGetText",
        "guiCheckbox", "guiDropdown", "guiScrollList", "guiProgressBar", "guiEntry",
        "guiGetChecked", "guiSetChecked", "guiSetOptions", "guiGetSelected", "guiSetSelected",
        "guiSetProgress",
        "gc_stats", "gc_collect", "gc_limit", "exit",
        "dbOpen", "dbClose", "dbSet", "dbGet", "dbHas", "dbDelete", "dbKeys", "dbCount", "dbCheckpoint",
        "procStart", "procRun", "procWait", "procIsRunning", "procKill", "procPid", "procExitCode",
        "procOutput", "procErrorOutput",
        "zipExtract", "zipEntries", "zipExtractEntry", "zipCreate"
    };

    // knownGlobals — імена глобальних змінних, оголошених РАНІШЕ, поза цим
    // program (напр. REPL: кожен рядок компілюється окремим викликом
    // Compile(), тож глобальна var з попереднього рядка інакше виглядала б
    // "не оголошеною" для компілятора, хоч у VM (Globals) її значення вже є.
    public Bytecode Compile(ProgramNode program, IEnumerable<string>? knownGlobals = null)
    {
        _bytecode = new Bytecode();

        // Раніше тут усі імена білтинів заздалегідь клались у _functions
        // з адресою-заглушкою -1. Жоден зі споживачів _functions (виклик,
        // посилання на функцію ЯК значення, авто-виклик main) насправді
        // цього не потребує — обидва місця вже перевіряють _builtins
        // окремо й незалежно, ДО того як впасти до ResolveFunctionName.
        // А шкода була реальна: коли ResolveFunctionName перевіряється
        // РАНІШЕ за _builtins.Contains (щоб власна/імпортована функція з
        // іменем білтина коректно перекривала його — див. коментар нижче),
        // ЦЯ заглушка змушувала виклик БУДЬ-ЯКОГО білтина резолвитись як
        // "нібито відома функція" з адресою, що НІКОЛИ не патчиться (жоден
        // білтин не компілюється в байткод) - CALL стрибав на -1, той самий
        // клас "сміттєва адреса, Run() тихо завершується без помилки", що
        // вже описаний нижче для вкладених функцій.
        foreach (var stmt in program.Statements)
            if (stmt is StructDeclaration sd) _structsByName[sd.Name] = sd;
        ValidateInheritanceChains();

        // Реєструємо всі функції (включаючи вкладені) перед компіляцією
        RegisterFunctions(program.Statements);

        if (knownGlobals != null)
            foreach (var name in knownGlobals) _globalNames.Add(name);

        // Імена глобальних var — ПЕРЕД компіляцією тіл функцій. Інакше
        // функція, оголошена перед глобальною змінною в тексті файлу, не
        // "бачила" би цю змінну взагалі: CompileFunctions компілює ВСІ тіла
        // одним проходом раніше, ніж top-level var (нижче в цьому методі).
        foreach (var stmt in program.Statements)
            if (stmt is VariableDeclaration v) _globalNames.Add(v.Name);

        // Emit JUMP to skip function definitions
        _bytecode.Emit(OpCode.JUMP, 0);
        int skipJump = _bytecode.Code.Count - 2;

        // Компілюємо тіла функцій
        CompileFunctions(program.Statements);

        // Успадкування: структура з extends отримує доступ до методів
        // предка, яких сама не перевизначає, - проставляємо для них
        // аліаси "Дитина.метод" -> та сама адреса, що й "Батько.метод".
        // Робиться ПІСЛЯ CompileFunctions (усі реальні адреси вже відомі),
        // а не разом з RegisterFunctions - інакше алiас міг би вказати на
        // ще не встановлену (-1) адресу предка, оголошеного нижче у файлі.
        ResolveInheritance();

        PatchJump(skipJump);

        // Глобальні інструкції
        _atTopLevel = true;
        foreach (var stmt in program.Statements)
        {
            if (!(stmt is FunctionDeclaration))
                CompileStatement(stmt);
        }
        _atTopLevel = false;

        // Патчимо відкладені виклики ПІСЛЯ компіляції геть усього, а не
        // лише тіл функцій. Раніше виклик іменованої функції ПРЯМО З КОДУ
        // ВЕРХНЬОГО РІВНЯ (напр. просто "main()" унизу файлу) компілювався
        // вже ПІСЛЯ цього патчингу — CALL лишався з адресою-заглушкою 0,
        // тобто стрибав на початок байткоду (сам JUMP, що пропускає тіла
        // функцій) і програма зациклювалась навіки, замовчуючи все.
        foreach (var (pos, name) in _pendingCalls)
        {
            int target = _functions[name];
            _bytecode.Code[pos] = (byte)(target & 0xFF);
            _bytecode.Code[pos + 1] = (byte)((target >> 8) & 0xFF);
        }
        foreach (var (fref, name) in _pendingFunctionRefs)
        {
            fref.Address = _functions[name];
        }

        // Автоматичний виклик main(), якщо він є і немає глобального коду, або просто для зручності
        if (_functions.ContainsKey("main") && !HasGlobalExecution(program))
        {
            _bytecode.Emit(OpCode.CALL, _functions["main"]);
        }

        _bytecode.Emit(OpCode.HALT);
        return _bytecode;
    }

    private void RegisterFunctions(IEnumerable<StatementNode> statements)
    {
        foreach (var stmt in statements)
        {
            if (stmt is FunctionDeclaration func)
            {
                _functions[QualifyFunctionName(func.Name)] = -1;
                _funcNestingStack.Add(func.Name);
                RegisterFunctions(func.Body.Statements);
                _funcNestingStack.RemoveAt(_funcNestingStack.Count - 1);
            }
            else if (stmt is StructDeclaration structDecl)
            {
                RegisterFunctions(structDecl.Methods);
            }
            else if (stmt is IfStatement ifStmt)
            {
                RegisterFunctions(ifStmt.ThenBlock.Statements);
                if (ifStmt.ElseBlock != null) RegisterFunctions(ifStmt.ElseBlock.Statements);
            }
            else if (stmt is WhileStatement whileStmt)
            {
                RegisterFunctions(whileStmt.Body.Statements);
            }
            else if (stmt is ForStatement forStmt)
            {
                RegisterFunctions(forStmt.Body.Statements);
            }
            else if (stmt is BlockStatement block)
            {
                RegisterFunctions(block.Statements);
            }
        }
    }

    private void CompileFunctions(IEnumerable<StatementNode> statements)
    {
        foreach (var stmt in statements)
        {
            if (stmt is FunctionDeclaration func)
            {
                string qualifiedName = QualifyFunctionName(func.Name);
                _functions[qualifiedName] = _bytecode!.Code.Count;
                _bytecode.FunctionAddresses[qualifiedName] = _bytecode.Code.Count;
                // Стек "батьків" має містити ЦЮ функцію вже під час компіляції
                // її власного тіла (виклики всередині мають резолвитись
                // відносно неї) і під час пошуку вкладених у ній функцій
                // нижче — знімається лише в самому кінці цієї гілки.
                _funcNestingStack.Add(func.Name);

                // Метод структури зареєстрований під іменем "Структура.метод"
                // (див. Parser.ParseFunctionDeclaration) - саме з цього дефіса
                // дізнаємось, чий це метод, і чи є в цієї структури extends,
                // щоб super.method() усередині тіла резолвився правильно.
                string? oldCurrentStructParent = _currentStructParent;
                int dotIdx = func.Name.IndexOf('.');
                _currentStructParent = dotIdx >= 0 && _structsByName.TryGetValue(func.Name[..dotIdx], out var ownerStruct)
                    ? ownerStruct.ParentName
                    : null;

                // Зберігаємо поточний стан змінних для ізоляції функцій
                var oldVars = new Dictionary<string, int>(_vars);
                // ВИПРАВЛЕНО: кожна функція починає нумерацію слотів з нуля.
                // Разом зі стеком фреймів у VM (див. VirtualMachine.cs) це дає
                // кожному виклику власний простір локальних змінних — раніше
                // номери слотів призначались наскрізно на всю програму, і
                // рекурсивний/вкладений виклик тієї ж функції міг тихо
                // затерти локальну змінну зовнішнього виклику.
                int oldVarCounter = _varCounter;
                _varCounter = 0;

                // Оскільки аргументи на стеку лежать в порядку (arg1, arg2, ...),
                // а ми їх хочемо дістати і зберегти в змінні, нам треба робити STORE в зворотному порядку
                for (int j = func.Parameters.Count - 1; j >= 0; j--)
                {
                    var p = func.Parameters[j];
                    _vars[p.Name] = _varCounter++;
                    _bytecode.Emit(OpCode.STORE_VAR, _vars[p.Name]);
                }

                CompileStatement(func.Body);
                // Неявний return у кінці тіла: кладемо власний null з тієї ж
                // причини, що й у ReturnStatement. Без цього функція, яка
                // закінчилась без return, знімала зі спільного стеку значення
                // свого викликача — найпомітніше це ламало вкладені try/catch.
                _bytecode.Emit(OpCode.LOAD_CONST, _bytecode.AddConstant(null!));
                _bytecode.Emit(OpCode.RETURN);

                // Відновлюємо змінні та лічильник слотів
                _vars = oldVars;
                _varCounter = oldVarCounter;
                _currentStructParent = oldCurrentStructParent;

                // RegisterFunctions() (вище) рекурсивно реєструє ІМ'Я кожної
                // func-декларації, включно з вкладеними в тіло іншої функції
                // (_functions[name] = -1, щоб виклик компілювався без помилки
                // "не оголошена"). Але CompileFunctions() РАНІШЕ не заходила
                // в func.Body — реальну адресу й байткод вкладена функція так
                // ніколи й не отримувала, лишаючись назавжди на "-1". Виклик
                // такої функції мовчки зупиняв всю програму (CALL стрибав на
                // сміттєву адресу, _ip вилітав за межі коду, Run() тихо
                // завершувався без жодної помилки). Тепер обидва проходи
                // симетричні: вкладена func компілюється одразу після
                // тіла-власника, її байткод лежить ПІСЛЯ RETURN власника,
                // тому нормальне виконання туди "не провалюється" — дістатись
                // можна лише явним викликом за іменем.
                CompileFunctions(func.Body.Statements);
                _funcNestingStack.RemoveAt(_funcNestingStack.Count - 1);
            }

            // Шукаємо вкладені функції в інших інструкціях
            if (stmt is StructDeclaration structDecl) { CompileFunctions(structDecl.Methods); }
            else if (stmt is IfStatement i) { CompileFunctions(i.ThenBlock.Statements); if (i.ElseBlock != null) CompileFunctions(i.ElseBlock.Statements); }
            else if (stmt is WhileStatement w) { CompileFunctions(w.Body.Statements); }
            else if (stmt is ForStatement f) { CompileFunctions(f.Body.Statements); }
            else if (stmt is BlockStatement b) { CompileFunctions(b.Statements); }
        }
    }

    private bool HasGlobalExecution(ProgramNode program)
    {
        foreach (var stmt in program.Statements)
        {
            // VariableDeclaration - це ОГОЛОШЕННЯ глобальної константи, а не
            // виконуваний код: "var MAX = 100" перед func main() не повинен
            // скасовувати автовиклик main(). Без цього винятку будь-яка
            // глобальна змінна робила auto-call неможливим, а явний виклик
            // main() з коду верхнього рівня — єдина альтернатива - раніше
            // зациклював програму (окрема причина, вже виправлена вище).
            if (!(stmt is FunctionDeclaration) && !(stmt is StructDeclaration) && !(stmt is VariableDeclaration))
                return true;
        }
        return false;
    }

    private void CompileStatement(StatementNode stmt)
    {
        // Дозволяє VirtualMachine показати номер рядка джерела при
        // Runtime Error — раніше помилки виконання (на відміну від
        // помилок парсингу) взагалі не казали, ДЕ в скрипті стались.
        if (stmt.Line > 0) _bytecode!.MarkLine(stmt.Line);

        switch (stmt)
        {
            case FunctionDeclaration f:
                break;
            case PrintStatement p:
                CompileExpression(p.Expression);
                _bytecode!.Emit(OpCode.PRINT);
                break;
            case ExpressionStatement e:
                CompileExpression(e.Expression);
                // ВИПРАВЛЕНО: виклик функції як окремий оператор (без
                // використання результату) залишав значення на стеку
                // назавжди - воно накопичувалось і зсувало аргументи
                // НАСТУПНИХ викликів (особливо помітно, коли такий виклик
                // ховався всередині іншої функції, чий результат одразу
                // передається далі як аргумент). Присвоєння (=) саме себе
                // "з'їдає" через STORE_VAR/ARRAY_SET/STRUCT_SET, тому для
                // нього POP не потрібен.
                if (!(e.Expression is BinaryExpression be && be.Operator == "="))
                {
                    _bytecode!.Emit(OpCode.POP);
                }
                break;
            case VariableDeclaration v:
                if (v.Initializer != null) CompileExpression(v.Initializer);
                else _bytecode!.Emit(OpCode.LOAD_CONST, _bytecode.AddConstant(0));
                if (_atTopLevel)
                {
                    // Верхній рівень файлу -> глобальна змінна: за іменем,
                    // не за номером слота (у кожної функції власна нумерація
                    // з нуля, спільного слота для глобальних не існує).
                    _bytecode!.Emit(OpCode.SET_GLOBAL, _bytecode.AddConstant(v.Name));
                }
                else
                {
                    _vars[v.Name] = _varCounter++;
                    _bytecode!.Emit(OpCode.STORE_VAR, _vars[v.Name]);
                }
                break;
            case IfStatement i:
                CompileExpression(i.Condition);
                _bytecode!.Emit(OpCode.JUMP_IF_FALSE, 0);
                int jumpPos = _bytecode.Code.Count - 2;
                CompileStatement(i.ThenBlock);
                _bytecode.Emit(OpCode.JUMP, 0);
                int jumpEndPos = _bytecode.Code.Count - 2;
                PatchJump(jumpPos);
                if (i.ElseBlock != null) CompileStatement(i.ElseBlock);
                PatchJump(jumpEndPos);
                break;
            case WhileStatement w:
                int startPos = _bytecode!.Code.Count;
                CompileExpression(w.Condition);
                _bytecode.Emit(OpCode.JUMP_IF_FALSE, 0);
                int jumpPos2 = _bytecode.Code.Count - 2;

                _loops.Push(new LoopContext());
                CompileStatement(w.Body);
                var whileLoop = _loops.Pop();

                // continue у while веде на перевірку умови
                foreach (var pos in whileLoop.ContinueJumps) PatchJumpTo(pos, startPos);

                _bytecode.Emit(OpCode.JUMP, startPos);
                PatchJump(jumpPos2);

                // break веде сюди — одразу за цикл
                foreach (var pos in whileLoop.BreakJumps) PatchJump(pos);
                break;
            case ForStatement f when f.End == null:
                {
                    // Ітерація по елементах масиву: for x in arrExpr { ... }
                    CompileExpression(f.Start);
                    int arrSlot = _varCounter++;
                    _bytecode!.Emit(OpCode.STORE_VAR, arrSlot);

                    int idxSlot = _varCounter++;
                    _bytecode.Emit(OpCode.LOAD_CONST, _bytecode.AddConstant(0.0));
                    _bytecode.Emit(OpCode.STORE_VAR, idxSlot);

                    _vars[f.VariableName] = _varCounter++;

                    int arrLoopStart = _bytecode.Code.Count;
                    _bytecode.Emit(OpCode.LOAD_VAR, idxSlot);
                    _bytecode.Emit(OpCode.LOAD_VAR, arrSlot);
                    int lenNameConst = _bytecode.AddConstant("len");
                    _bytecode.Emit(OpCode.CALL_NATIVE, lenNameConst, 1);
                    _bytecode.Emit(OpCode.LT);
                    _bytecode.Emit(OpCode.JUMP_IF_FALSE, 0);
                    int arrEndJump = _bytecode.Code.Count - 2;

                    _bytecode.Emit(OpCode.LOAD_VAR, arrSlot);
                    _bytecode.Emit(OpCode.LOAD_VAR, idxSlot);
                    _bytecode.Emit(OpCode.ARRAY_GET);
                    _bytecode.Emit(OpCode.STORE_VAR, _vars[f.VariableName]);

                    _loops.Push(new LoopContext());
                    CompileStatement(f.Body);
                    var forEachLoop = _loops.Pop();

                    // continue має пропустити решту тіла, але ОБОВ'ЯЗКОВО
                    // виконати збільшення індексу — інакше цикл зациклиться.
                    int incrPos = _bytecode.Code.Count;
                    foreach (var pos in forEachLoop.ContinueJumps) PatchJumpTo(pos, incrPos);

                    _bytecode.Emit(OpCode.LOAD_VAR, idxSlot);
                    _bytecode.Emit(OpCode.LOAD_CONST, _bytecode.AddConstant(1.0));
                    _bytecode.Emit(OpCode.ADD);
                    _bytecode.Emit(OpCode.STORE_VAR, idxSlot);
                    _bytecode.Emit(OpCode.JUMP, arrLoopStart);

                    PatchJump(arrEndJump);
                    foreach (var pos in forEachLoop.BreakJumps) PatchJump(pos);
                }
                break;
            case ForStatement f:
                _vars[f.VariableName] = _varCounter++;
                CompileExpression(f.Start);
                _bytecode!.Emit(OpCode.STORE_VAR, _vars[f.VariableName]);

                int forStart = _bytecode.Code.Count;
                _bytecode.Emit(OpCode.LOAD_VAR, _vars[f.VariableName]);
                CompileExpression(f.End!);
                _bytecode.Emit(OpCode.LT);
                _bytecode.Emit(OpCode.JUMP_IF_FALSE, 0);
                int forJump = _bytecode.Code.Count - 2;

                _loops.Push(new LoopContext());
                CompileStatement(f.Body);
                var forLoop = _loops.Pop();

                // Так само, як у for-in: continue стрибає на збільшення лічильника.
                int forIncrPos = _bytecode.Code.Count;
                foreach (var pos in forLoop.ContinueJumps) PatchJumpTo(pos, forIncrPos);

                _bytecode.Emit(OpCode.LOAD_VAR, _vars[f.VariableName]);
                _bytecode.Emit(OpCode.LOAD_CONST, _bytecode.AddConstant(1));
                _bytecode.Emit(OpCode.ADD);
                _bytecode.Emit(OpCode.STORE_VAR, _vars[f.VariableName]);
                _bytecode.Emit(OpCode.JUMP, forStart);

                PatchJump(forJump);
                foreach (var pos in forLoop.BreakJumps) PatchJump(pos);
                break;
            case BlockStatement b:
                foreach (var s in b.Statements) CompileStatement(s);
                break;
            case ReturnStatement r:
                // Стек операндів у VM спільний на всю програму, а RETURN завжди
                // знімає з нього значення. Тому "return" без значення мусить
                // покласти власний null — інакше він зніме чуже значення,
                // що належить тому, хто викликав.
                if (r.Value != null) CompileExpression(r.Value);
                else _bytecode!.Emit(OpCode.LOAD_CONST, _bytecode.AddConstant(null!));
                _bytecode!.Emit(OpCode.RETURN);
                break;
            case StructDeclaration s:
                break;
            case TryStatement t:
                {
                    _vars[t.CatchVariableName] = _varCounter++;
                    int catchVarSlot = _vars[t.CatchVariableName];

                    _bytecode!.Emit(OpCode.TRY_BEGIN, 0, catchVarSlot);
                    int catchAddrPatchPos = _bytecode.Code.Count - 4;

                    CompileStatement(t.TryBlock);
                    _bytecode.Emit(OpCode.TRY_END);
                    _bytecode.Emit(OpCode.JUMP, 0);
                    int jumpOverCatchPos = _bytecode.Code.Count - 2;

                    PatchJump(catchAddrPatchPos);
                    CompileStatement(t.CatchBlock);
                    PatchJump(jumpOverCatchPos);
                }
                break;
            case BreakStatement:
                if (_loops.Count == 0)
                    throw new Exception("'break' можна використовувати лише всередині циклу");
                _bytecode!.Emit(OpCode.JUMP, 0);
                _loops.Peek().BreakJumps.Add(_bytecode.Code.Count - 2);
                break;
            case ContinueStatement:
                if (_loops.Count == 0)
                    throw new Exception("'continue' можна використовувати лише всередині циклу");
                _bytecode!.Emit(OpCode.JUMP, 0);
                _loops.Peek().ContinueJumps.Add(_bytecode.Code.Count - 2);
                break;
            case ThrowStatement th:
                CompileExpression(th.Value);
                _bytecode!.Emit(OpCode.THROW);
                break;
        }
    }

    private void PatchJump(int pos)
    {
        PatchJumpTo(pos, _bytecode!.Code.Count);
    }

    // Патч на КОНКРЕТНУ адресу, а не на поточний кінець коду: потрібно для
    // continue, який стрибає назад — на збільшення лічильника або на умову.
    private void PatchJumpTo(int pos, int target)
    {
        _bytecode!.Code[pos] = (byte)(target & 0xFF);
        _bytecode.Code[pos + 1] = (byte)((target >> 8) & 0xFF);
    }

    private void CompileExpression(ExpressionNode expr)
    {
        switch (expr)
        {
            case LiteralExpression l:
                _bytecode!.Emit(OpCode.LOAD_CONST, _bytecode.AddConstant(l.Value));
                break;
            case VariableExpression v:
                if (_vars.TryGetValue(v.Name, out int varIdx))
                    _bytecode!.Emit(OpCode.LOAD_VAR, varIdx);
                else if (_globalNames.Contains(v.Name))
                    _bytecode!.Emit(OpCode.GET_GLOBAL, _bytecode.AddConstant(v.Name));
                else if (ResolveFunctionName(v.Name) is string resolvedRefName)
                {
                    // Посилання на іменовану функцію ЯК ЗНАЧЕННЯ (без виклику).
                    // Перевіряється ПЕРЕД _builtins - тими ж міркуваннями, що й
                    // для виклику (CallExpression, вище): власна/імпортована
                    // функція з іменем білтина має перекривати його й тут.
                    // Адреса патчиться пізніше, коли всі функції вже скомпільовані.
                    // resolvedRefName — уточнене ім'я з урахуванням вкладеності
                    // (те саме лексичне резолвення, що й для звичайного виклику).
                    var funcRef = new NxFunctionRef { Name = v.Name };
                    int constIdx = _bytecode!.AddConstant(funcRef);
                    _pendingFunctionRefs.Add((funcRef, resolvedRefName));
                    _bytecode.Emit(OpCode.LOAD_CONST, constIdx);
                }
                else if (_builtins.Contains(v.Name))
                {
                    // Посилання на НАТИВНУ функцію як значення (напр. var f = sqrt).
                    // У нативних функцій немає адреси в байткоді - викликаються
                    // напряму через _nativeFunctions за іменем.
                    var nativeRef = new NxFunctionRef { NativeName = v.Name };
                    int nativeConstIdx = _bytecode!.AddConstant(nativeRef);
                    _bytecode.Emit(OpCode.LOAD_CONST, nativeConstIdx);
                }
                else
                    throw new Exception($"Змінна '{v.Name}' не оголошена");
                break;
            case BinaryExpression b:
                if (b.Operator == "=")
                {
                    if (b.Left is VariableExpression varExpr)
                    {
                        CompileExpression(b.Right);
                        if (_vars.TryGetValue(varExpr.Name, out int varIdx2))
                            _bytecode!.Emit(OpCode.STORE_VAR, varIdx2);
                        else if (_globalNames.Contains(varExpr.Name))
                            _bytecode!.Emit(OpCode.SET_GLOBAL, _bytecode.AddConstant(varExpr.Name));
                        else
                            throw new Exception($"Змінна '{varExpr.Name}' не оголошена");
                    }
                    else if (b.Left is IndexExpression idxExpr)
                    {
                        CompileExpression(idxExpr.Array);
                        CompileExpression(idxExpr.Index);
                        CompileExpression(b.Right);
                        _bytecode!.Emit(OpCode.ARRAY_SET);
                    }
                    else if (b.Left is MemberAccessExpression memberExpr)
                    {
                        CompileExpression(memberExpr.Object);
                        _bytecode!.Emit(OpCode.LOAD_CONST, _bytecode.AddConstant(memberExpr.Member));
                        CompileExpression(b.Right);
                        _bytecode!.Emit(OpCode.STRUCT_SET);
                    }
                    else
                        throw new Exception("Ліва частина присвоєння повинна бути змінною, елементом масиву або полем структури");
                }
                else if (b.Operator == "&&")
                {
                    // Коротке замикання: якщо ліва частина хибна, права взагалі не обчислюється
                    CompileExpression(b.Left);
                    _bytecode!.Emit(OpCode.JUMP_IF_FALSE, 0);
                    int falseJump = _bytecode.Code.Count - 2;
                    CompileExpression(b.Right);
                    _bytecode.Emit(OpCode.NOT);
                    _bytecode.Emit(OpCode.NOT);
                    _bytecode.Emit(OpCode.JUMP, 0);
                    int endJump = _bytecode.Code.Count - 2;
                    PatchJump(falseJump);
                    _bytecode.Emit(OpCode.LOAD_CONST, _bytecode.AddConstant(false));
                    PatchJump(endJump);
                }
                else if (b.Operator == "||")
                {
                    // Коротке замикання: якщо ліва частина істинна, права взагалі не обчислюється
                    CompileExpression(b.Left);
                    _bytecode!.Emit(OpCode.JUMP_IF_FALSE, 0);
                    int falseJump = _bytecode.Code.Count - 2;
                    _bytecode.Emit(OpCode.LOAD_CONST, _bytecode.AddConstant(true));
                    _bytecode.Emit(OpCode.JUMP, 0);
                    int endJump = _bytecode.Code.Count - 2;
                    PatchJump(falseJump);
                    CompileExpression(b.Right);
                    _bytecode.Emit(OpCode.NOT);
                    _bytecode.Emit(OpCode.NOT);
                    PatchJump(endJump);
                }
                else
                {
                    CompileExpression(b.Left);
                    CompileExpression(b.Right);
                    _bytecode!.Emit(b.Operator switch
                    {
                        "+" => OpCode.ADD, "-" => OpCode.SUB, "*" => OpCode.MUL,
                        "/" => OpCode.DIV, "%" => OpCode.MOD,
                        "==" => OpCode.EQ, "!=" => OpCode.NEQ,
                        "<" => OpCode.LT, "<=" => OpCode.LTE,
                        ">" => OpCode.GT, ">=" => OpCode.GTE,
                        _ => throw new Exception($"Невідомий оператор: {b.Operator}")
                    });
                }
                break;
            case UnaryExpression u:
                if (u.Operator == "!")
                {
                    CompileExpression(u.Operand);
                    _bytecode!.Emit(OpCode.NOT);
                }
                else if (u.Operator == "-")
                {
                    _bytecode!.Emit(OpCode.LOAD_CONST, _bytecode.AddConstant(0.0));
                    CompileExpression(u.Operand);
                    _bytecode!.Emit(OpCode.SUB);
                }
                break;
            case CallExpression c:
                switch (c.FunctionName)
                {
                    case "print":
                        foreach (var arg in c.Arguments) CompileExpression(arg);
                        _bytecode!.Emit(OpCode.PRINT);
                        break;
                    case "readLine":
                        _bytecode!.Emit(OpCode.READ_LINE);
                        break;
                    case "readInt":
                        _bytecode!.Emit(OpCode.READ_INT);
                        break;
                    case "readDouble":
                        _bytecode!.Emit(OpCode.READ_DOUBLE);
                        break;
                    default:
                        // Вкладена функція резолвиться від найближчого "батька"
                        // до найдальшого (лексична видимість), перш ніж шукати
                        // серед функцій верхнього рівня — інакше однойменна
                        // вкладена в іншому місці програми могла б випадково
                        // "перебити" ту, що справді видна звідси.
                        //
                        // resolvedName перевіряється ПЕРШИМ, перед _builtins:
                        // раніше було навпаки, тож власна/імпортована функція з
                        // іменем, що збігається з нативним білтином (напр.
                        // lib/datetime.nx визначає свій formatDate/parseDate),
                        // тихо й без жодної діагностики компілятора програвала
                        // білтину — виклик у файлі, що явно імпортував саме
                        // ЛОКАЛЬНУ функцію, насправді викликав щось зовсім інше.
                        string? resolvedName = ResolveFunctionName(c.FunctionName);
                        if (resolvedName != null)
                        {
                            foreach (var arg in c.Arguments) CompileExpression(arg);
                            _bytecode!.Emit(OpCode.CALL, 0);
                            _pendingCalls.Add((_bytecode.Code.Count - 2, resolvedName));
                        }
                        else if (_builtins.Contains(c.FunctionName))
                        {
                            foreach (var arg in c.Arguments) CompileExpression(arg);
                            int nameConst = _bytecode!.AddConstant(c.FunctionName);
                            _bytecode.Emit(OpCode.CALL_NATIVE, nameConst, c.Arguments.Count);
                        }
                        else if (_vars.ContainsKey(c.FunctionName))
                        {
                            // Виклик функції-значення, що зберігається у змінній
                            // (параметр вищого порядку, лямбда тощо)
                            _bytecode!.Emit(OpCode.LOAD_VAR, _vars[c.FunctionName]);
                            foreach (var arg in c.Arguments) CompileExpression(arg);
                            _bytecode.Emit(OpCode.CALL_VALUE, c.Arguments.Count);
                        }
                        else
                            throw new Exception($"Функція '{c.FunctionName}' не оголошена");
                        break;
                }
                break;
            case CallValueExpression cv:
                // Callee - довільний вираз (попередній виклик, індексація,
                // мапа тощо), а не ім'я змінної: CompileExpression сам кладе
                // на стек значення-функцію, CALL_VALUE далі бере його разом
                // з аргументами - той самий опкод, що й для "var f = ...; f()".
                CompileExpression(cv.Callee);
                foreach (var arg in cv.Arguments) CompileExpression(arg);
                _bytecode!.Emit(OpCode.CALL_VALUE, cv.Arguments.Count);
                break;
            case ArrayLiteralExpression a:
                foreach (var elem in a.Elements)
                    CompileExpression(elem);
                _bytecode!.Emit(OpCode.ARRAY_NEW, a.Elements.Count);
                break;
            case IndexExpression idx:
                CompileExpression(idx.Array);
                CompileExpression(idx.Index);
                _bytecode!.Emit(OpCode.ARRAY_GET);
                break;
            case StructInitExpression s:
                foreach (var field in s.Fields)
                {
                    _bytecode!.Emit(OpCode.LOAD_CONST, _bytecode.AddConstant(field.Name));
                    CompileExpression(field.Value);
                }
                _bytecode!.Emit(OpCode.LOAD_CONST, _bytecode.AddConstant("__type"));
                _bytecode!.Emit(OpCode.LOAD_CONST, _bytecode.AddConstant(s.StructName));
                _bytecode!.Emit(OpCode.STRUCT_NEW, s.Fields.Count + 1);
                break;
            case MemberAccessExpression m:
                CompileExpression(m.Object);
                _bytecode!.Emit(OpCode.LOAD_CONST, _bytecode.AddConstant(m.Member));
                _bytecode!.Emit(OpCode.STRUCT_GET);
                break;
            case FunctionExpression fn:
                {
                    // Захоплюємо (копіюємо значення) усіх змінних, видимих у
                    // поточній області видимості на момент СТВОРЕННЯ лямбди —
                    // просте замикання копіюванням, без справжніх upvalue.
                    var capturedSlots = _vars.Values.Distinct().Select(s => (object)(double)s).ToList();

                    _bytecode!.Emit(OpCode.JUMP, 0);
                    int skipPos = _bytecode.Code.Count - 2;

                    int lambdaAddr = _bytecode.Code.Count;

                    var oldVars = new Dictionary<string, int>(_vars); // успадковуємо, НЕ обнуляємо
                    int oldVarCounter = _varCounter; // продовжуємо нумерацію, щоб не зіткнутися з видимими слотами
                    // Тіло лямбди — власна локальна область, навіть якщо саму
                    // лямбду створено на верхньому рівні файлу: var усередині
                    // fn.Body не повинен ставати глобальним.
                    bool oldAtTopLevel = _atTopLevel;
                    _atTopLevel = false;

                    for (int j = fn.Parameters.Count - 1; j >= 0; j--)
                    {
                        var p = fn.Parameters[j];
                        _vars[p.Name] = _varCounter++;
                        _bytecode.Emit(OpCode.STORE_VAR, _vars[p.Name]);
                    }

                    CompileStatement(fn.Body);
                    // Той самий неявний return, що й у іменованих функціях.
                    _bytecode.Emit(OpCode.LOAD_CONST, _bytecode.AddConstant(null!));
                    _bytecode.Emit(OpCode.RETURN);

                    _vars = oldVars;
                    _varCounter = oldVarCounter;
                    _atTopLevel = oldAtTopLevel;

                    PatchJump(skipPos);

                    int slotsConstIdx = _bytecode.AddConstant(capturedSlots);
                    _bytecode.Emit(OpCode.MAKE_CLOSURE, lambdaAddr, slotsConstIdx);
                }
                break;
            case MethodCallExpression m:
                if (m.Object is VariableExpression superVe && superVe.Name == "super")
                {
                    // super.method() навмисно НЕ йде через CALL_METHOD: та
                    // диспетчерізує по runtime __type self, тобто знайшла б
                    // знову НАЙБІЛЬШ похідне перевизначення - виклик самого
                    // себе замість батьківської реалізації. Тому резолвимо
                    // статично (як звичайний CALL) через ланцюжок extends.
                    if (_currentStructParent == null)
                        throw new Exception("'super' можна використовувати лише в методі структури, яка оголошена з 'extends'");
                    if (!_vars.TryGetValue("self", out int selfSlot))
                        throw new Exception("'super' можна використовувати лише всередині методу структури");
                    string? owner = ResolveMethodOwnerInChain(_currentStructParent, m.MethodName);
                    if (owner == null)
                        throw new Exception($"Метод '{m.MethodName}' не знайдено в батьківських структурах ('{_currentStructParent}' і вище)");

                    _bytecode!.Emit(OpCode.LOAD_VAR, selfSlot);
                    foreach (var arg in m.Arguments) CompileExpression(arg);
                    _bytecode.Emit(OpCode.CALL, 0);
                    _pendingCalls.Add((_bytecode.Code.Count - 2, $"{owner}.{m.MethodName}"));
                }
                else
                {
                    CompileExpression(m.Object);
                    foreach (var arg in m.Arguments)
                        CompileExpression(arg);
                    int methodConst = _bytecode!.AddConstant(m.MethodName);
                    _bytecode.Emit(OpCode.CALL_METHOD, methodConst, m.Arguments.Count);
                }
                break;
            default:
                throw new Exception($"Невідомий вираз: {expr.GetType()}");
        }
    }
}