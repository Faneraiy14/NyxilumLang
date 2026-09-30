using System.Reflection;
using System.Text;
using NyxilumLang.AST;
using NyxilumLang.Core;

namespace NyxilumLang.Native;

// NativeCodegenArm64 — Фаза A2 (30.09.2026): нативна компіляція
// NyxilumLang у машинний код ARM64 (AArch64) - процесор телефонів.
// Статичний ELF без libc і без VM, сирі syscall'и Linux, тож той самий
// бінарник запускається і на Android (Termux / adb shell / всередині APK).
//
// На відміну від x86-бекенда (статичні типи, Фаза N3), тут значення
// ДИНАМІЧНІ, як у VM: будь-яка змінна може тримати число, рядок, масив,
// мапу, функцію, структуру, bool чи null. Кожне значення - 64 біти
// (NaN-boxing, прийом JS-рушіїв):
//   - звичайний double - це число;
//   - "неможливий" NaN з верхніми 16 бітами 0xFFF9..0xFFFE - мітка типу,
//     а нижні 48 біт - вказівник (або null/false/true для 0xFFF9).
// Мітки: 0xFFF9 спец (null=0, false=1, true=2), 0xFFFA рядок,
// 0xFFFB масив, 0xFFFC мапа, 0xFFFD функція, 0xFFFE структура.
//
// Об'єкти в купі (bump-розподілювач через brk, без збирання сміття):
//   рядок     [довжина в байтах:8][UTF-8 байти][0]
//   масив     [довжина:8][місткість:8][вказівник на дані:8]
//   мапа      [масив ключів:8][масив значень:8]
//   функція   [код:8][оточення:8][кількість параметрів:8]
//   структура [id типу:8][кількість полів:8][поля по 8]
//
// Виклики (свої, не C ABI): аргументи - у зарезервованій ділянці стека
// слотами по 16 байт (sp на AArch64 кратний 16), слот 0 - оточення
// замикання. Callee бачить оточення за [x29+16], параметр i - за
// [x29+32+i*16]. Результат будь-якого виразу - у x0.
//
// Більшість бібліотеки (len, split, join, мапи, друк дробових чисел)
// написана на самій NyxilumLang - Arm64Prelude.nx; на асемблері
// (NativeCodegenArm64.Runtime.cs) лише "двері": пам'ять, байти, біти
// double, syscall'и.
public partial class NativeCodegenArm64
{
    private const ulong NullBits = 0xFFF9_0000_0000_0000UL;
    private const ulong FalseBits = NullBits | 1;
    private const ulong TrueBits = NullBits | 2;

    private readonly StringBuilder _asm = new();
    private readonly StringBuilder _rodata = new();
    private int _labelCounter;

    private readonly Dictionary<string, FunctionDeclaration> _funcs = new();
    private readonly HashSet<string> _preludeNames = new();
    private readonly Dictionary<string, string> _funcLabels = new();
    private readonly Dictionary<string, string> _globals = new();
    private readonly List<StructInfo> _structs = new();
    private readonly Dictionary<string, StructInfo> _structByName = new();
    private readonly Dictionary<string, string> _stringLits = new();
    private readonly HashSet<string> _fnObjects = new();
    private readonly List<(string Label, FunctionExpression Fn, List<string> Captured)> _pendingLambdas = new();
    private int _lambdaCounter;
    private readonly HashSet<string> _reachable = new();

    // стан поточної функції
    private Dictionary<string, int> _locals = new();
    private Dictionary<object, int> _hidden = new(ReferenceEqualityComparer.Instance);
    private int _nextLocal;
    private string _epilogue = "";
    private readonly List<int> _activeTries = new();
    private readonly Stack<(string Continue, string Break, int TryDepth)> _loops = new();

    private sealed class StructInfo
    {
        public string Name = "";
        public int Id;
        public List<string> Fields = new();
        public Dictionary<string, (FunctionDeclaration Decl, string Label)> Methods = new();
    }

    // Вбудовані функції, реалізовані прямо на асемблері: назва ->
    // (підпрограма, мін. і макс. кількість аргументів). Відсутні
    // необов'язкові аргументи передаються як null.
    private static readonly Dictionary<string, (string Rt, int Min, int Max)> Builtins = new()
    {
        ["toString"] = ("rt_to_str", 1, 1),
        ["readLine"] = ("rt_read_line", 0, 0),
        ["timestamp"] = ("rt_timestamp", 0, 0),
        ["exit"] = ("rt_exit", 0, 1),
        ["sleep"] = ("rt_sleep", 1, 1),
        ["floor"] = ("rt_floor", 1, 1),
        ["ceil"] = ("rt_ceil", 1, 1),
        ["round"] = ("rt_round", 1, 1),
        ["sqrt"] = ("rt_sqrt", 1, 1),
        ["abs"] = ("rt_abs", 1, 1),
        ["printNoNewLine"] = ("rt_print_nonl", 1, 1),
        ["isNull"] = ("rt_is_null", 1, 1),
        ["append"] = ("rt_arr_push", 2, 2),
        ["__tag"] = ("rt_i_tag", 1, 1),
        ["__trunc"] = ("rt_trunc", 1, 1),
        ["__strByteLen"] = ("rt_i_str_len", 1, 1),
        ["__strByte"] = ("rt_i_str_byte", 2, 2),
        ["__strAlloc"] = ("rt_i_str_alloc", 1, 1),
        ["__strSetByte"] = ("rt_i_str_set_byte", 3, 3),
        ["__strCopy"] = ("rt_i_str_copy", 5, 5),
        ["__strSliceBytes"] = ("rt_i_str_slice", 3, 3),
        ["__strFindBytes"] = ("rt_i_str_find", 3, 3),
        ["__arrNew"] = ("rt_i_arr_new", 1, 1),
        ["__arrLen"] = ("rt_i_arr_len", 1, 1),
        ["__arrGet"] = ("rt_index_get", 2, 2),
        ["__arrSet"] = ("rt_index_set", 3, 3),
        ["__arrPush"] = ("rt_arr_push", 2, 2),
        ["__arrPop"] = ("rt_i_arr_pop", 1, 1),
        ["__arrSetLen"] = ("rt_i_arr_set_len", 2, 2),
        ["__mapNew"] = ("rt_i_map_new", 0, 0),
        ["__mapKeys"] = ("rt_i_map_keys", 1, 1),
        ["__mapVals"] = ("rt_i_map_vals", 1, 1),
        ["__dblExp"] = ("rt_i_dbl_exp", 1, 1),
        ["__dblHi"] = ("rt_i_dbl_hi", 1, 1),
        ["__dblLo"] = ("rt_i_dbl_lo", 1, 1),
        ["__dblSign"] = ("rt_i_dbl_sign", 1, 1),
        ["__write"] = ("rt_i_write", 2, 2),
        ["__stdinEof"] = ("rt_i_stdin_eof", 0, 0),
        ["__random32"] = ("rt_i_random32", 0, 0),
        ["__structName"] = ("rt_i_struct_name", 1, 1),
    };

    private static readonly Dictionary<string, string> BinaryOps = new()
    {
        ["+"] = "rt_add", ["-"] = "rt_sub", ["*"] = "rt_mul", ["/"] = "rt_div", ["%"] = "rt_mod",
        ["=="] = "rt_eq", ["!="] = "rt_ne", ["<"] = "rt_lt", ["<="] = "rt_le", [">"] = "rt_gt", [">="] = "rt_ge",
        ["&"] = "rt_band", ["|"] = "rt_bor", ["^"] = "rt_bxor", ["<<"] = "rt_shl", [">>"] = "rt_shr",
    };

    // Назви, які лексер вважає ключовими словами, - у прелюдії з префіксом __
    private static readonly Dictionary<string, string> PreludeAliases = new()
    {
        ["min"] = "__min", ["max"] = "__max", ["pow"] = "__pow",
    };

    private string ResolveFuncName(string name) =>
        !_funcs.ContainsKey(name) && PreludeAliases.TryGetValue(name, out var alias) ? alias : name;

    // Функції прелюдії, які кличе сам рантайм (друк чисел/масивів/мап)
    private static readonly string[] RuntimeRoots = { "__numToStrSlow", "__arrToStr", "__mapToStr", "__structToStr" };

    public string Compile(ProgramNode program)
    {
        CollectDeclarations(LoadPrelude(), isPrelude: true);
        var topLevel = CollectDeclarations(program, isPrelude: false);

        bool autoMain = _funcs.ContainsKey("main") && !_preludeNames.Contains("main")
            && !topLevel.Any(s => s is not VariableDeclaration);
        ComputeReachable(topLevel, autoMain);

        _asm.AppendLine("// Згенеровано NativeCodegenArm64.cs (NyxilumLang, Фаза A2) - НЕ редагувати вручну.");
        _asm.AppendLine(AsmMacros);
        _asm.AppendLine(".text");
        _asm.AppendLine(".global _start");

        CompileStart(topLevel, autoMain);
        foreach (var (name, decl) in _funcs)
            if (_reachable.Contains(name))
                CompileFunction(_funcLabels[name], decl.Parameters.Select(p => p.Name).ToList(), decl.Body, null);
        foreach (var s in _structs)
            foreach (var (mname, (decl, label)) in s.Methods)
                if (_reachable.Contains("." + mname))
                    CompileFunction(label, decl.Parameters.Select(p => p.Name).ToList(), decl.Body, null);
        while (_pendingLambdas.Count > 0)
        {
            var (label, fn, captured) = _pendingLambdas[0];
            _pendingLambdas.RemoveAt(0);
            CompileFunction(label, fn.Parameters.Select(p => p.Name).ToList(), fn.Body, captured);
        }

        _asm.AppendLine(Runtime.Replace("{NUMSLOW}", _funcLabels["__numToStrSlow"])
            .Replace("{ARRTOSTR}", _funcLabels["__arrToStr"])
            .Replace("{MAPTOSTR}", _funcLabels["__mapToStr"])
            .Replace("{STRUCTTOSTR}", _funcLabels["__structToStr"]));

        _asm.AppendLine(".section .rodata");
        _asm.AppendLine("print_newline: .byte 10");
        foreach (var (label, text) in RuntimeStrings)
            EmitStringObject(label, text);
        foreach (var name in _fnObjects)
        {
            _rodata.AppendLine(".balign 8");
            _rodata.AppendLine($".Lfo_{_funcLabels[name]}: .quad {_funcLabels[name]}, 0, {_funcs[name].Parameters.Count}");
        }
        var structNameLabels = _structs.Select(s => StringLabel(s.Name)).ToList();
        _rodata.AppendLine(".balign 8");
        _rodata.AppendLine(".Lstructnames:");
        foreach (var l in structNameLabels)
            _rodata.AppendLine($"    .quad {l}");
        _asm.Append(_rodata);

        _asm.AppendLine(".bss");
        _asm.AppendLine(".balign 8");
        _asm.AppendLine("heap_ptr: .skip 8");   // bump-покажчик купи (0 = ще не ініціалізовано)
        _asm.AppendLine("heap_end: .skip 8");   // поточна межа brk
        _asm.AppendLine("exc_top: .skip 8");    // найближчий активний try-обробник (0 = немає)
        _asm.AppendLine("exc_value: .skip 8");  // значення останнього throw
        _asm.AppendLine("inpos: .skip 8");      // stdin: позиція в буфері,
        _asm.AppendLine("inlen: .skip 8");      //        скільки байтів у ньому,
        _asm.AppendLine("ineof: .skip 8");      //        чи скінчився ввід
        _asm.AppendLine("inbuf: .skip 4096");
        foreach (var label in _globals.Values)
            _asm.AppendLine($"{label}: .skip 8");

        return _asm.ToString();
    }

    // ---------------------------------------------------------------- збір оголошень

    private static ProgramNode LoadPrelude()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("NyxilumLang.Native.Arm64Prelude.nx")
            ?? throw new Exception("native codegen (arm64): не знайдено вбудовану прелюдію Arm64Prelude.nx");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return new Parser(new Lexer(reader.ReadToEnd()).Tokenize()).ParseProgram();
    }

    private string MakeLabel(string name)
    {
        bool ascii = name.All(c => c < 128 && (char.IsLetterOrDigit(c) || c == '_'));
        return ascii ? "nx_" + name : $"nx_u{_labelCounter++}";
    }

    // Повертає виконувані оператори верхнього рівня (разом з глобальними
    // var - вони виконуються в тому ж порядку, що й у VM)
    private List<StatementNode> CollectDeclarations(ProgramNode program, bool isPrelude)
    {
        var exec = new List<StatementNode>();
        var methodFuncs = new List<FunctionDeclaration>();
        foreach (var stmt in program.Statements)
        {
            switch (stmt)
            {
                case FunctionDeclaration f when f.Name.Contains('.'):
                    methodFuncs.Add(f);
                    break;
                case FunctionDeclaration f:
                    if (!isPrelude) _preludeNames.Remove(f.Name); // програма перевизначає прелюдію
                    _funcs[f.Name] = f;
                    _funcLabels[f.Name] = MakeLabel(f.Name);
                    if (isPrelude) _preludeNames.Add(f.Name);
                    break;
                case StructDeclaration s:
                    RegisterStruct(s);
                    break;
                case ImportStatement:
                    break;
                case VariableDeclaration v:
                    if (!_globals.ContainsKey(v.Name)) _globals[v.Name] = $".Lg{_globals.Count}";
                    exec.Add(v);
                    break;
                default:
                    exec.Add(stmt);
                    break;
            }
        }
        foreach (var m in methodFuncs)
        {
            string owner = m.Name[..m.Name.LastIndexOf('.')];
            if (!_structByName.TryGetValue(owner, out var info))
                throw new Exception($"native codegen (arm64): метод '{m.Name}' для невідомої структури '{owner}'");
            string bare = m.Name[(m.Name.LastIndexOf('.') + 1)..];
            info.Methods[bare] = (m, $"nx_m{info.Id}_{_labelCounter++}");
        }
        return exec;
    }

    private void RegisterStruct(StructDeclaration s)
    {
        var info = new StructInfo { Name = s.Name, Id = _structs.Count };
        if (s.ParentName != null)
        {
            if (!_structByName.TryGetValue(s.ParentName, out var parent))
                throw new Exception($"native codegen (arm64): батьківська структура '{s.ParentName}' має бути оголошена раніше за '{s.Name}'");
            info.Fields.AddRange(parent.Fields);
            foreach (var (k, v) in parent.Methods) info.Methods[k] = v;
        }
        foreach (var f in s.Fields)
            if (!info.Fields.Contains(f.Name)) info.Fields.Add(f.Name);
        foreach (var m in s.Methods)
        {
            string bare = m.Name.Contains('.') ? m.Name[(m.Name.LastIndexOf('.') + 1)..] : m.Name;
            info.Methods[bare] = (m, $"nx_m{info.Id}_{_labelCounter++}");
        }
        _structs.Add(info);
        _structByName[s.Name] = info;
    }

    // ---------------------------------------------------------------- досяжність

    // У бінарник потрапляє лише те, що справді викликається: від main,
    // коду верхнього рівня й функцій прелюдії, потрібних рантайму.
    // Методи позначаються як ".назва" - кличуться динамічно, тож беремо
    // всі методи з такою назвою.
    private void ComputeReachable(List<StatementNode> topLevel, bool autoMain)
    {
        var work = new Stack<BlockStatement>();
        void Mark(string key, BlockStatement body)
        {
            if (_reachable.Add(key)) work.Push(body);
        }
        void Visit(object node)
        {
            switch (node)
            {
                case CallExpression c:
                    if (_funcs.TryGetValue(ResolveFuncName(c.FunctionName), out var fd)) Mark(ResolveFuncName(c.FunctionName), fd.Body);
                    foreach (var a in c.Arguments) Visit(a);
                    break;
                case VariableExpression v:
                    if (_funcs.TryGetValue(v.Name, out var vd)) Mark(v.Name, vd.Body);
                    break;
                case MethodCallExpression mc:
                    if (_reachable.Add("." + mc.MethodName))
                        foreach (var s in _structs)
                            if (s.Methods.TryGetValue(mc.MethodName, out var m)) work.Push(m.Decl.Body);
                    Visit(mc.Object);
                    foreach (var a in mc.Arguments) Visit(a);
                    break;
                case FunctionExpression fe: work.Push(fe.Body); break;
                case BinaryExpression b: Visit(b.Left); Visit(b.Right); break;
                case UnaryExpression u: Visit(u.Operand); break;
                case CallValueExpression cv: Visit(cv.Callee); foreach (var a in cv.Arguments) Visit(a); break;
                case IndexExpression ix: Visit(ix.Array); Visit(ix.Index); break;
                case MemberAccessExpression ma: Visit(ma.Object); break;
                case ArrayLiteralExpression al: foreach (var e in al.Elements) Visit(e); break;
                case StructInitExpression si: foreach (var f in si.Fields) Visit(f.Value); break;
                case VariableDeclaration vd2: if (vd2.Initializer != null) Visit(vd2.Initializer); break;
                case PrintStatement p: Visit(p.Expression); break;
                case ReturnStatement r: if (r.Value != null) Visit(r.Value); break;
                case ExpressionStatement es: Visit(es.Expression); break;
                case IfStatement ifs: Visit(ifs.Condition); work.Push(ifs.ThenBlock); if (ifs.ElseBlock != null) work.Push(ifs.ElseBlock); break;
                case WhileStatement ws: Visit(ws.Condition); work.Push(ws.Body); break;
                case ForStatement fs: Visit(fs.Start); if (fs.End != null) Visit(fs.End); work.Push(fs.Body); break;
                case TryStatement ts: work.Push(ts.TryBlock); work.Push(ts.CatchBlock); break;
                case ThrowStatement th: Visit(th.Value); break;
                case BlockStatement bs: work.Push(bs); break;
            }
        }

        foreach (var s in topLevel) Visit(s);
        if (autoMain) Mark("main", _funcs["main"].Body);
        foreach (var r in RuntimeRoots) Mark(r, _funcs[r].Body);
        while (work.Count > 0)
            foreach (var s in work.Pop().Statements) Visit(s);
    }

    // ---------------------------------------------------------------- помічники емісії

    private void Emit(string line) => _asm.AppendLine("    " + line);
    private void Label(string label) => _asm.AppendLine(label + ":");
    private string NewLabel(string prefix) => $".L{prefix}{_labelCounter++}";

    private void LoadImm(string reg, ulong v)
    {
        bool first = true;
        for (int sh = 0; sh < 64; sh += 16)
        {
            ushort part = (ushort)(v >> sh);
            if (part == 0) continue;
            Emit(first ? $"movz {reg}, #{part}, lsl #{sh}" : $"movk {reg}, #{part}, lsl #{sh}");
            first = false;
        }
        if (first) Emit($"mov {reg}, #0");
    }

    // Операнд пам'яті для слота кадру [x29 + off]; поза -256..255 адреса
    // рахується в x16 (ldur/stur приймають лише такий діапазон)
    private string Slot(int off)
    {
        if (off >= -256 && off <= 255) return $"[x29, #{off}]";
        Emit($"mov x16, #{off}");
        Emit("add x16, x29, x16");
        return "[x16]";
    }

    private void PushX0() => Emit("str x0, [sp, #-16]!");
    private void PopX(string reg) => Emit($"ldr {reg}, [sp], #16");

    private void SubSp(int bytes)
    {
        if (bytes == 0) return;
        if (bytes <= 4095) Emit($"sub sp, sp, #{bytes}");
        else { Emit($"mov x16, #{bytes}"); Emit("sub sp, sp, x16"); }
    }

    private void AddSp(int bytes)
    {
        if (bytes == 0) return;
        if (bytes <= 4095) Emit($"add sp, sp, #{bytes}");
        else { Emit($"mov x16, #{bytes}"); Emit("add sp, sp, x16"); }
    }

    private string StringLabel(string text)
    {
        if (_stringLits.TryGetValue(text, out var label)) return label;
        label = $".Lstr{_stringLits.Count}";
        _stringLits[text] = label;
        EmitStringObject(label, text);
        return label;
    }

    private void EmitStringObject(string label, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        _rodata.AppendLine(".balign 8");
        _rodata.AppendLine($"{label}:");
        _rodata.AppendLine($"    .quad {bytes.Length}");
        _rodata.AppendLine("    .byte " + string.Join(", ", bytes.Select(b => b.ToString()).Append("0")));
    }

    // ---------------------------------------------------------------- кадри й змінні

    private int NewSlot(int bytes = 8)
    {
        _nextLocal -= bytes;
        return _nextLocal;
    }

    // Слоти для всіх змінних функції (без заходу у вкладені лямбди).
    // topLevel: пряме "var" на верхньому рівні - глобальна, не локальна.
    private void CollectLocals(IEnumerable<StatementNode> stmts, bool topLevel)
    {
        foreach (var stmt in stmts)
        {
            switch (stmt)
            {
                case VariableDeclaration v:
                    if (!topLevel && !_locals.ContainsKey(v.Name)) _locals[v.Name] = NewSlot();
                    break;
                case IfStatement ifs:
                    CollectLocals(ifs.ThenBlock.Statements, false);
                    if (ifs.ElseBlock != null) CollectLocals(ifs.ElseBlock.Statements, false);
                    break;
                case WhileStatement ws:
                    CollectLocals(ws.Body.Statements, false);
                    break;
                case ForStatement fs:
                    if (!_locals.ContainsKey(fs.VariableName)) _locals[fs.VariableName] = NewSlot();
                    _hidden[fs] = NewSlot(16); // [кінець діапазону або масив][індекс]
                    CollectLocals(fs.Body.Statements, false);
                    break;
                case TryStatement ts:
                    _hidden[ts] = NewSlot(32);
                    if (!_locals.ContainsKey(ts.CatchVariableName)) _locals[ts.CatchVariableName] = NewSlot();
                    CollectLocals(ts.TryBlock.Statements, false);
                    CollectLocals(ts.CatchBlock.Statements, false);
                    break;
                case BlockStatement b:
                    CollectLocals(b.Statements, false);
                    break;
            }
        }
    }

    private void ResetFunctionState()
    {
        _locals = new Dictionary<string, int>();
        _hidden = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        _nextLocal = 0;
        _activeTries.Clear();
        _loops.Clear();
    }

    private void EmitPrologue(string label)
    {
        Label(label);
        Emit("stp x29, x30, [sp, #-16]!");
        Emit("mov x29, sp");
        SubSp((-_nextLocal + 15) / 16 * 16);
    }

    private void CompileFunction(string label, List<string> parameters, BlockStatement body, List<string>? captured)
    {
        ResetFunctionState();
        for (int i = 0; i < parameters.Count; i++)
            _locals[parameters[i]] = 32 + i * 16;
        if (captured != null)
            foreach (var c in captured)
                _locals[c] = NewSlot();
        CollectLocals(body.Statements, false);

        _epilogue = NewLabel("ret");
        EmitPrologue(label);
        if (captured != null && captured.Count > 0)
        {
            Emit("ldr x9, [x29, #16]"); // оточення: захоплені значення по 8 байт
            for (int i = 0; i < captured.Count; i++)
            {
                Emit($"ldr x0, [x9, #{i * 8}]");
                Emit($"str x0, {Slot(_locals[captured[i]])}");
            }
        }
        foreach (var stmt in body.Statements)
            CompileStatement(stmt);
        LoadImm("x0", NullBits);
        Label(_epilogue);
        Emit("mov sp, x29");
        Emit("ldp x29, x30, [sp], #16");
        Emit("ret");
    }

    // _start: глобальні змінні -> null, код верхнього рівня по порядку,
    // потім main() (якщо VM теж викликала б її автоматично)
    private void CompileStart(List<StatementNode> topLevel, bool autoMain)
    {
        ResetFunctionState();
        CollectLocals(topLevel, true);
        _epilogue = NewLabel("startret");
        EmitPrologue("_start");
        LoadImm("x0", NullBits);
        foreach (var label in _globals.Values)
        {
            Emit($"adrp x16, {label}");
            Emit($"str x0, [x16, :lo12:{label}]");
        }
        foreach (var stmt in topLevel)
        {
            if (stmt is VariableDeclaration v)
            {
                if (v.Initializer != null) CompileExpression(v.Initializer);
                else LoadImm("x0", NullBits);
                StoreVar(v.Name);
            }
            else CompileStatement(stmt);
        }
        if (autoMain)
        {
            SubSp(16);
            LoadImm("x9", NullBits);
            Emit("str x9, [sp]");
            Emit($"bl {_funcLabels["main"]}");
            AddSp(16);
            Emit("b rt_exit_code");
        }
        LoadImm("x0", NullBits);
        Label(_epilogue);
        Emit("mov x0, #0");
        Emit("mov x8, #93");
        Emit("svc #0");
    }

    private void LoadVar(string name)
    {
        if (_locals.TryGetValue(name, out int off))
            Emit($"ldr x0, {Slot(off)}");
        else if (_globals.TryGetValue(name, out var g))
        {
            Emit($"adrp x16, {g}");
            Emit($"ldr x0, [x16, :lo12:{g}]");
        }
        else if (_funcs.ContainsKey(name))
        {
            // назва функції як значення - статичний об'єкт-функція без оточення
            _fnObjects.Add(name);
            Emit($"adrp x0, .Lfo_{_funcLabels[name]}");
            Emit($"add x0, x0, :lo12:.Lfo_{_funcLabels[name]}");
            Emit("movk x0, #0xFFFD, lsl #48");
        }
        else throw new Exception($"native codegen (arm64): змінна '{name}' не оголошена");
    }

    private void StoreVar(string name)
    {
        if (_locals.TryGetValue(name, out int off))
            Emit($"str x0, {Slot(off)}");
        else if (_globals.TryGetValue(name, out var g))
        {
            Emit($"adrp x16, {g}");
            Emit($"str x0, [x16, :lo12:{g}]");
        }
        else throw new Exception($"native codegen (arm64): змінна '{name}' не оголошена");
    }

    // ---------------------------------------------------------------- оператори

    private void CompileBlock(BlockStatement block)
    {
        foreach (var stmt in block.Statements)
            CompileStatement(stmt);
    }

    private void Truthy(string falseLabel)
    {
        Emit("bl rt_truthy");
        Emit($"cbz x0, {falseLabel}");
    }

    // Вихід з блоків try (return/break/continue): відновити exc_top на
    // обробник, що був активний ДО найзовнішнього try, який ми покидаємо
    private void RestoreTries(int keepDepth)
    {
        if (_activeTries.Count <= keepDepth) return;
        Emit($"ldr x9, {Slot(_activeTries[keepDepth] + 24)}");
        Emit("adrp x10, exc_top");
        Emit("str x9, [x10, :lo12:exc_top]");
    }

    private void CompileStatement(StatementNode stmt)
    {
        switch (stmt)
        {
            case VariableDeclaration v:
                if (v.Initializer != null) CompileExpression(v.Initializer);
                else LoadImm("x0", NullBits);
                StoreVar(v.Name);
                break;

            case PrintStatement p:
                CompileExpression(p.Expression);
                Emit("bl rt_print");
                break;

            case ExpressionStatement e:
                CompileExpression(e.Expression);
                break;

            case ReturnStatement r:
                if (r.Value != null) CompileExpression(r.Value);
                else LoadImm("x0", NullBits);
                RestoreTries(0);
                Emit($"b {_epilogue}");
                break;

            case IfStatement ifs:
                {
                    string elseL = NewLabel("else"), endL = NewLabel("endif");
                    CompileExpression(ifs.Condition);
                    Truthy(elseL);
                    CompileBlock(ifs.ThenBlock);
                    Emit($"b {endL}");
                    Label(elseL);
                    if (ifs.ElseBlock != null) CompileBlock(ifs.ElseBlock);
                    Label(endL);
                    break;
                }

            case WhileStatement ws:
                {
                    string startL = NewLabel("while"), endL = NewLabel("wend");
                    Label(startL);
                    CompileExpression(ws.Condition);
                    Truthy(endL);
                    _loops.Push((startL, endL, _activeTries.Count));
                    CompileBlock(ws.Body);
                    _loops.Pop();
                    Emit($"b {startL}");
                    Label(endL);
                    break;
                }

            case ForStatement fs:
                CompileFor(fs);
                break;

            case BreakStatement:
                if (_loops.Count == 0) throw new Exception("native codegen (arm64): break поза циклом");
                RestoreTries(_loops.Peek().TryDepth);
                Emit($"b {_loops.Peek().Break}");
                break;

            case ContinueStatement:
                if (_loops.Count == 0) throw new Exception("native codegen (arm64): continue поза циклом");
                RestoreTries(_loops.Peek().TryDepth);
                Emit($"b {_loops.Peek().Continue}");
                break;

            case TryStatement ts:
                {
                    // Кадр обробника [sp, x29, мітка catch, попередній]; throw
                    // відновлює sp/x29 з нього і стрибає на catch (setjmp/longjmp)
                    int frame = _hidden[ts];
                    string catchL = NewLabel("catch"), endL = NewLabel("tryend");
                    Emit("mov x9, sp");
                    Emit($"str x9, {Slot(frame)}");
                    Emit($"str x29, {Slot(frame + 8)}");
                    Emit($"adrp x9, {catchL}");
                    Emit($"add x9, x9, :lo12:{catchL}");
                    Emit($"str x9, {Slot(frame + 16)}");
                    Emit("adrp x10, exc_top");
                    Emit("ldr x9, [x10, :lo12:exc_top]");
                    Emit($"str x9, {Slot(frame + 24)}");
                    Emit($"mov x9, #{frame}");
                    Emit("add x9, x29, x9");
                    Emit("adrp x10, exc_top");
                    Emit("str x9, [x10, :lo12:exc_top]");

                    _activeTries.Add(frame);
                    CompileBlock(ts.TryBlock);
                    _activeTries.RemoveAt(_activeTries.Count - 1);

                    Emit($"ldr x9, {Slot(frame + 24)}");
                    Emit("adrp x10, exc_top");
                    Emit("str x9, [x10, :lo12:exc_top]");
                    Emit($"b {endL}");

                    Label(catchL);
                    Emit($"ldr x9, {Slot(frame + 24)}");
                    Emit("adrp x10, exc_top");
                    Emit("str x9, [x10, :lo12:exc_top]");
                    Emit("adrp x10, exc_value");
                    Emit("ldr x0, [x10, :lo12:exc_value]");
                    StoreVar(ts.CatchVariableName);
                    CompileBlock(ts.CatchBlock);
                    Label(endL);
                    break;
                }

            case ThrowStatement th:
                CompileExpression(th.Value);
                Emit("b rt_throw");
                break;

            case BlockStatement b:
                CompileBlock(b);
                break;

            case FunctionDeclaration or StructDeclaration or ImportStatement:
                throw new Exception("native codegen (arm64): функції, структури та import оголошуються лише на верхньому рівні файлу");

            default:
                throw new Exception($"native codegen (arm64): непідтримуваний оператор - {stmt.GetType().Name}");
        }
    }

    private void CompileFor(ForStatement fs)
    {
        int hidden = _hidden[fs];
        string startL = NewLabel("for"), contL = NewLabel("fcont"), endL = NewLabel("fend");
        if (fs.End != null)
        {
            // for i in a..b - від a до b НЕ включно, крок 1 (як у VM)
            CompileExpression(fs.Start);
            StoreVar(fs.VariableName);
            CompileExpression(fs.End);
            Emit($"str x0, {Slot(hidden)}");
            Label(startL);
            LoadVar(fs.VariableName);
            Emit($"ldr x1, {Slot(hidden)}");
            Emit("bl rt_lt");
            Truthy(endL);
            _loops.Push((contL, endL, _activeTries.Count));
            CompileBlock(fs.Body);
            _loops.Pop();
            Label(contL);
            LoadVar(fs.VariableName);
            LoadImm("x1", (ulong)BitConverter.DoubleToInt64Bits(1.0));
            Emit("bl rt_add");
            StoreVar(fs.VariableName);
            Emit($"b {startL}");
            Label(endL);
        }
        else
        {
            // for x in масив - елементи по черзі (довжина перечитується щоразу)
            CompileExpression(fs.Start);
            Emit($"str x0, {Slot(hidden)}");
            Emit($"str xzr, {Slot(hidden + 8)}");
            Label(startL);
            Emit($"ldr x0, {Slot(hidden)}");
            Emit("bl rt_arr_len_raw");
            Emit($"ldr x1, {Slot(hidden + 8)}");
            Emit("cmp x1, x0");
            Emit($"b.hs {endL}");
            Emit($"ldr x0, {Slot(hidden)}");
            Emit("UNBOX x9, x0");
            Emit("ldr x9, [x9, #16]");
            Emit("ldr x0, [x9, x1, lsl #3]");
            StoreVar(fs.VariableName);
            _loops.Push((contL, endL, _activeTries.Count));
            CompileBlock(fs.Body);
            _loops.Pop();
            Label(contL);
            Emit($"ldr x9, {Slot(hidden + 8)}");
            Emit("add x9, x9, #1");
            Emit($"str x9, {Slot(hidden + 8)}");
            Emit($"b {startL}");
            Label(endL);
        }
    }

    // ---------------------------------------------------------------- вирази

    private void CompileExpression(ExpressionNode expr)
    {
        switch (expr)
        {
            case LiteralExpression { Value: double d }:
                LoadImm("x0", (ulong)BitConverter.DoubleToInt64Bits(d));
                break;
            case LiteralExpression { Value: bool b }:
                LoadImm("x0", b ? TrueBits : FalseBits);
                break;
            case LiteralExpression { Value: string s }:
                Emit($"LSTR x0, {StringLabel(s)}");
                break;
            case LiteralExpression { Value: null }:
                LoadImm("x0", NullBits);
                break;

            case VariableExpression v:
                LoadVar(v.Name);
                break;

            case ArrayLiteralExpression al:
                Emit($"mov x0, #{Math.Max(al.Elements.Count, 4)}");
                Emit("bl rt_arr_new_raw");
                foreach (var el in al.Elements)
                {
                    PushX0();
                    CompileExpression(el);
                    Emit("mov x1, x0");
                    Emit("ldr x0, [sp]");
                    Emit("bl rt_arr_push");
                    PopX("x0");
                }
                break;

            case IndexExpression ix:
                CompileExpression(ix.Array);
                PushX0();
                CompileExpression(ix.Index);
                Emit("mov x1, x0");
                PopX("x0");
                Emit("bl rt_index_get");
                break;

            case MemberAccessExpression ma:
                CompileExpression(ma.Object);
                EmitFieldDispatch(ma.Member, store: false);
                break;

            case StructInitExpression si:
                CompileStructInit(si);
                break;

            case MethodCallExpression mc:
                CompileMethodCall(mc);
                break;

            case FunctionExpression fe:
                CompileLambdaValue(fe);
                break;

            case CallExpression call:
                CompileCall(call);
                break;

            case CallValueExpression cv:
                CompileExpression(cv.Callee);
                CompileValueCall(cv.Arguments);
                break;

            case UnaryExpression { Operator: "-" } u:
                CompileExpression(u.Operand);
                Emit("bl rt_neg");
                break;
            case UnaryExpression { Operator: "!" } u:
                CompileExpression(u.Operand);
                Emit("bl rt_truthy");
                Emit("eor x0, x0, #1");
                Emit("TOBOOL x0");
                break;
            case UnaryExpression { Operator: "~" } u:
                CompileExpression(u.Operand);
                Emit("bl rt_bnot");
                break;

            case BinaryExpression { Operator: "=" } assign:
                CompileAssign(assign);
                break;

            case BinaryExpression { Operator: "&&" or "||" } logic:
                {
                    bool isAnd = logic.Operator == "&&";
                    string shortL = NewLabel("sc"), endL = NewLabel("scend");
                    CompileExpression(logic.Left);
                    Emit("bl rt_truthy");
                    Emit(isAnd ? $"cbz x0, {shortL}" : $"cbnz x0, {shortL}");
                    CompileExpression(logic.Right);
                    Emit("bl rt_truthy");
                    Emit(isAnd ? $"cbz x0, {shortL}" : $"cbnz x0, {shortL}");
                    LoadImm("x0", isAnd ? TrueBits : FalseBits);
                    Emit($"b {endL}");
                    Label(shortL);
                    LoadImm("x0", isAnd ? FalseBits : TrueBits);
                    Label(endL);
                    break;
                }

            case BinaryExpression bin:
                if (!BinaryOps.TryGetValue(bin.Operator, out var rt))
                    throw new Exception($"native codegen (arm64): оператор '{bin.Operator}' не підтримується");
                CompileExpression(bin.Left);
                PushX0();
                CompileExpression(bin.Right);
                Emit("mov x1, x0");
                PopX("x0");
                Emit($"bl {rt}");
                break;

            default:
                throw new Exception($"native codegen (arm64): непідтримуваний вираз - {expr.GetType().Name}");
        }
    }

    private void CompileAssign(BinaryExpression assign)
    {
        switch (assign.Left)
        {
            case VariableExpression v:
                CompileExpression(assign.Right);
                StoreVar(v.Name);
                break;
            case IndexExpression ix:
                CompileExpression(ix.Array);
                PushX0();
                CompileExpression(ix.Index);
                PushX0();
                CompileExpression(assign.Right);
                Emit("mov x2, x0");
                PopX("x1");
                PopX("x0");
                Emit("bl rt_index_set");
                break;
            case MemberAccessExpression ma:
                CompileExpression(ma.Object);
                PushX0();
                CompileExpression(assign.Right);
                Emit("mov x1, x0");
                PopX("x0");
                EmitFieldDispatch(ma.Member, store: true);
                break;
            default:
                throw new Exception("native codegen (arm64): присвоєння можливе лише у змінну, a[i] чи obj.field");
        }
    }

    // Поле структури: тип відомий лише під час виконання, тож перевіряємо
    // id типу серед усіх структур, що мають таке поле.
    // x0 = структура; для store x1 = нове значення (воно ж і результат).
    private void EmitFieldDispatch(string field, bool store)
    {
        var byOffset = _structs.Where(s => s.Fields.Contains(field))
            .GroupBy(s => 16 + s.Fields.IndexOf(field) * 8).ToList();
        if (byOffset.Count == 0)
            throw new Exception($"native codegen (arm64): жодна структура не має поля '{field}'");
        string doneL = NewLabel("fdone");
        Emit("TAGOF x16, x0");
        Emit("cmp x16, #5");
        Emit("b.ne rt_err_notstruct");
        Emit("UNBOX x9, x0");
        Emit("ldr x10, [x9]");
        var hits = new List<(string Label, int Offset)>();
        foreach (var group in byOffset)
        {
            string hitL = NewLabel("fhit");
            foreach (var s in group)
            {
                Emit($"cmp x10, #{s.Id}");
                Emit($"b.eq {hitL}");
            }
            hits.Add((hitL, group.Key));
        }
        Emit("b rt_err_nofield");
        foreach (var (hitL, off) in hits)
        {
            Label(hitL);
            if (store)
            {
                Emit($"str x1, [x9, #{off}]");
                Emit("mov x0, x1");
            }
            else Emit($"ldr x0, [x9, #{off}]");
            Emit($"b {doneL}");
        }
        Label(doneL);
    }

    private void CompileStructInit(StructInitExpression si)
    {
        if (!_structByName.TryGetValue(si.StructName, out var info))
            throw new Exception($"native codegen (arm64): невідома структура '{si.StructName}'");
        int n = info.Fields.Count;
        Emit($"mov x0, #{16 + n * 8}");
        Emit("bl rt_alloc");
        Emit($"mov x9, #{info.Id}");
        Emit("str x9, [x0]");
        Emit($"mov x9, #{n}");
        Emit("str x9, [x0, #8]");
        LoadImm("x9", NullBits);
        for (int i = 0; i < n; i++)
            Emit($"str x9, [x0, #{16 + i * 8}]");
        Emit("movk x0, #0xFFFE, lsl #48");
        foreach (var f in si.Fields)
        {
            int k = info.Fields.IndexOf(f.Name);
            if (k < 0) throw new Exception($"native codegen (arm64): структура '{si.StructName}' не має поля '{f.Name}'");
            PushX0();
            CompileExpression(f.Value);
            Emit("ldr x9, [sp]");
            Emit("UNBOX x9, x9");
            Emit($"str x0, [x9, #{16 + k * 8}]");
            PopX("x0");
        }
    }

    // Виклик за назвою: змінна-функція, своя/прелюдійна функція чи вбудована
    private void CompileCall(CallExpression call)
    {
        string name = call.FunctionName;
        if (_locals.ContainsKey(name) || _globals.ContainsKey(name))
        {
            LoadVar(name);
            CompileValueCall(call.Arguments);
            return;
        }
        name = ResolveFuncName(name);
        if (_funcs.TryGetValue(name, out var decl))
        {
            int n = decl.Parameters.Count;
            bool prelude = _preludeNames.Contains(name);
            if (call.Arguments.Count > n || (!prelude && call.Arguments.Count != n))
                throw new Exception($"native codegen (arm64): функція '{name}' очікує {n} аргумент(и/ів), передано {call.Arguments.Count}");
            int bytes = (n + 1) * 16;
            SubSp(bytes);
            for (int i = 0; i < n; i++)
            {
                if (i < call.Arguments.Count) CompileExpression(call.Arguments[i]);
                else LoadImm("x0", NullBits);
                Emit($"str x0, [sp, #{16 + i * 16}]");
            }
            LoadImm("x9", NullBits);
            Emit("str x9, [sp]");
            Emit($"bl {_funcLabels[name]}");
            AddSp(bytes);
            return;
        }
        if (Builtins.TryGetValue(name, out var b))
        {
            if (call.Arguments.Count < b.Min || call.Arguments.Count > b.Max)
                throw new Exception($"native codegen (arm64): {name}() очікує {b.Min}..{b.Max} аргумент(и/ів), передано {call.Arguments.Count}");
            foreach (var a in call.Arguments)
            {
                CompileExpression(a);
                PushX0();
            }
            for (int i = call.Arguments.Count - 1; i >= 0; i--)
                PopX($"x{i}");
            for (int i = call.Arguments.Count; i < b.Max; i++)
                LoadImm($"x{i}", NullBits);
            Emit($"bl {b.Rt}");
            return;
        }
        throw new Exception($"native codegen (arm64): невідома функція '{name}' (нативно ще не підтримується або не оголошена)");
    }

    // x0 = значення-функція; аргументи обчислюються зліва направо
    private void CompileValueCall(List<ExpressionNode> args)
    {
        int n = args.Count;
        int bytes = (n + 1) * 16;
        PushX0();
        SubSp(bytes);
        for (int i = 0; i < n; i++)
        {
            CompileExpression(args[i]);
            Emit($"str x0, [sp, #{16 + i * 16}]");
        }
        Emit($"ldr x0, [sp, #{bytes}]");
        Emit($"mov x1, #{n}");
        Emit("bl rt_fn_prep"); // x0 = код, x1 = оточення
        Emit("str x1, [sp]");
        Emit("blr x0");
        AddSp(bytes + 16);
    }

    private void CompileMethodCall(MethodCallExpression mc)
    {
        int n = mc.Arguments.Count;
        var targets = _structs.Where(s => s.Methods.TryGetValue(mc.MethodName, out var m) && m.Decl.Parameters.Count == n + 1).ToList();
        if (targets.Count == 0)
            throw new Exception($"native codegen (arm64): жодна структура не має методу '{mc.MethodName}' з {n} аргумент(ом/ами)");
        int bytes = (n + 2) * 16; // оточення, self, аргументи
        CompileExpression(mc.Object);
        PushX0();
        SubSp(bytes);
        Emit($"ldr x0, [sp, #{bytes}]");
        Emit("str x0, [sp, #16]");
        for (int i = 0; i < n; i++)
        {
            CompileExpression(mc.Arguments[i]);
            Emit($"str x0, [sp, #{32 + i * 16}]");
        }
        LoadImm("x9", NullBits);
        Emit("str x9, [sp]");
        Emit("ldr x0, [sp, #16]");
        Emit("TAGOF x16, x0");
        Emit("cmp x16, #5");
        Emit("b.ne rt_err_notstruct");
        Emit("UNBOX x9, x0");
        Emit("ldr x10, [x9]");
        string doneL = NewLabel("mdone");
        var hits = new List<(string Label, string Target)>();
        foreach (var s in targets)
        {
            string hitL = NewLabel("mhit");
            Emit($"cmp x10, #{s.Id}");
            Emit($"b.eq {hitL}");
            hits.Add((hitL, s.Methods[mc.MethodName].Label));
        }
        Emit("b rt_err_nomethod");
        foreach (var (hitL, target) in hits)
        {
            Label(hitL);
            Emit($"bl {target}");
            Emit($"b {doneL}");
        }
        Label(doneL);
        AddSp(bytes + 16);
    }

    // Замикання: захоплені змінні КОПІЮЮТЬСЯ в оточення (як у VM)
    private void CompileLambdaValue(FunctionExpression fe)
    {
        var captured = FreeVars(fe).Where(_locals.ContainsKey).ToList();
        string label = $".Llam{_lambdaCounter++}";
        _pendingLambdas.Add((label, fe, captured));
        if (captured.Count > 0)
        {
            Emit($"mov x0, #{captured.Count * 8}");
            Emit("bl rt_alloc");
            PushX0();
            for (int i = 0; i < captured.Count; i++)
            {
                LoadVar(captured[i]);
                Emit("ldr x9, [sp]");
                Emit($"str x0, [x9, #{i * 8}]");
            }
        }
        else
        {
            Emit("str xzr, [sp, #-16]!");
        }
        Emit("mov x0, #24");
        Emit("bl rt_alloc");
        PopX("x10");
        Emit($"adrp x9, {label}");
        Emit($"add x9, x9, :lo12:{label}");
        Emit("str x9, [x0]");
        Emit("str x10, [x0, #8]");
        Emit($"mov x9, #{fe.Parameters.Count}");
        Emit("str x9, [x0, #16]");
        Emit("movk x0, #0xFFFD, lsl #48");
    }

    // ---------------------------------------------------------------- вільні змінні лямбди

    private static List<string> FreeVars(FunctionExpression fn)
    {
        var bound = new HashSet<string>(fn.Parameters.Select(p => p.Name));
        var free = new List<string>();
        FreeInBlock(fn.Body.Statements, bound, free);
        return free;
    }

    private static void FreeInBlock(IEnumerable<StatementNode> stmts, HashSet<string> bound, List<string> free)
    {
        foreach (var s in stmts) FreeInStmt(s, bound, free);
    }

    private static void FreeInStmt(StatementNode stmt, HashSet<string> bound, List<string> free)
    {
        switch (stmt)
        {
            case VariableDeclaration v:
                if (v.Initializer != null) FreeInExpr(v.Initializer, bound, free);
                bound.Add(v.Name);
                break;
            case PrintStatement p: FreeInExpr(p.Expression, bound, free); break;
            case ReturnStatement r: if (r.Value != null) FreeInExpr(r.Value, bound, free); break;
            case ExpressionStatement e: FreeInExpr(e.Expression, bound, free); break;
            case ThrowStatement t: FreeInExpr(t.Value, bound, free); break;
            case IfStatement ifs:
                FreeInExpr(ifs.Condition, bound, free);
                FreeInBlock(ifs.ThenBlock.Statements, bound, free);
                if (ifs.ElseBlock != null) FreeInBlock(ifs.ElseBlock.Statements, bound, free);
                break;
            case WhileStatement ws:
                FreeInExpr(ws.Condition, bound, free);
                FreeInBlock(ws.Body.Statements, bound, free);
                break;
            case ForStatement fs:
                FreeInExpr(fs.Start, bound, free);
                if (fs.End != null) FreeInExpr(fs.End, bound, free);
                bound.Add(fs.VariableName);
                FreeInBlock(fs.Body.Statements, bound, free);
                break;
            case TryStatement ts:
                FreeInBlock(ts.TryBlock.Statements, bound, free);
                bound.Add(ts.CatchVariableName);
                FreeInBlock(ts.CatchBlock.Statements, bound, free);
                break;
            case BlockStatement b: FreeInBlock(b.Statements, bound, free); break;
        }
    }

    private static void FreeInExpr(ExpressionNode expr, HashSet<string> bound, List<string> free)
    {
        switch (expr)
        {
            case VariableExpression v:
                if (!bound.Contains(v.Name) && !free.Contains(v.Name)) free.Add(v.Name);
                break;
            case CallExpression c:
                if (!bound.Contains(c.FunctionName) && !free.Contains(c.FunctionName)) free.Add(c.FunctionName);
                foreach (var a in c.Arguments) FreeInExpr(a, bound, free);
                break;
            case BinaryExpression b: FreeInExpr(b.Left, bound, free); FreeInExpr(b.Right, bound, free); break;
            case UnaryExpression u: FreeInExpr(u.Operand, bound, free); break;
            case CallValueExpression cv:
                FreeInExpr(cv.Callee, bound, free);
                foreach (var a in cv.Arguments) FreeInExpr(a, bound, free);
                break;
            case IndexExpression ix: FreeInExpr(ix.Array, bound, free); FreeInExpr(ix.Index, bound, free); break;
            case MemberAccessExpression m: FreeInExpr(m.Object, bound, free); break;
            case MethodCallExpression mc:
                FreeInExpr(mc.Object, bound, free);
                foreach (var a in mc.Arguments) FreeInExpr(a, bound, free);
                break;
            case ArrayLiteralExpression al: foreach (var e in al.Elements) FreeInExpr(e, bound, free); break;
            case StructInitExpression si: foreach (var f in si.Fields) FreeInExpr(f.Value, bound, free); break;
            case FunctionExpression nested:
                foreach (var fv in FreeVars(nested))
                    if (!bound.Contains(fv) && !free.Contains(fv)) free.Add(fv);
                break;
        }
    }
}
