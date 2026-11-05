using System.Globalization;
using System.Reflection;
using System.Text;
using NyxilumLang.AST;
using NyxilumLang.Core;

namespace NyxilumLang.Native;

// Системний бекенд x86-64 (SYSTEMS_ROADMAP.md, етап S1) - NyxilumLang «як C».
//
// На відміну від x86-32 (NativeCodegen, числа - double) і ARM64 (динамічні
// значення з NaN-boxing), тут СТАТИЧНІ типи без боксування - те, на чому
// можна писати JVM, аллокатор, ядро:
//   int8 int16 int32 int64, uint8 uint16 uint32 uint64, float64, bool,
//   ptr<T> (ptr<void> - будь-який вказівник), структури з розміткою полів як у C.
// Ціле значення в регістрі завжди «канонічне»: розширене до 64 біт за
// знаком (signed) чи нулями (unsigned) - після кожної операції.
// Виклики - System V AMD64 ABI (rdi rsi rdx rcx r8 r9 / xmm0-7, результат
// rax / xmm0), тож функції можна викликати з C і навпаки (--obj).
// Цілочисельні літерали без типу підлаштовуються під тип другого операнда
// або змінної; дробові - float64. Змішувати різні цілі типи не можна -
// лише явне перетворення: int32(x), uint8(x), float64(x)...
//
// Вбудовані: print(x) / put(x) (без нового рядка), syscall(n, ...),
// alloc(n) -> ptr<void> (нулі), free(p), realloc(p, n), memcpy/memmove/memset, addr(x), offsetof(S, поле), new(S) -> ptr<S> (нулі), S { поле: ... },
// sizeof(T), stackbuf(N) -> ptr<uint8> (N байтів у кадрі функції),
// asptr(ціле чи вказівник) -> ptr<void>, funcaddr(f) -> ptr<void>, callptr(f, ...) -> int64,
// float64Bits(x) -> int64, bitsToFloat64(n) -> float64, asm("...") -> int64 (rax).
// Ще немає (наступні етапи): винятки, замикання, рядки як значення, масиви,
// int64 MIN / -1 і насичення float->int як у Java (S2), free (S3).
public sealed class NativeCodegenX64
{
    // ---------- типи ----------
    enum K { Int, Float, Bool, Ptr, Void, Struct, Array }

    sealed class T
    {
        public K Kind; public int Size, Count, Align = 1; public bool Signed; public T? Elem; public string? Struct;
        public static readonly T I8 = new() { Kind = K.Int, Size = 1, Signed = true }, U8 = new() { Kind = K.Int, Size = 1 },
            I16 = new() { Kind = K.Int, Size = 2, Signed = true }, U16 = new() { Kind = K.Int, Size = 2 },
            I32 = new() { Kind = K.Int, Size = 4, Signed = true }, U32 = new() { Kind = K.Int, Size = 4 },
            I64 = new() { Kind = K.Int, Size = 8, Signed = true }, U64 = new() { Kind = K.Int, Size = 8 },
            F64 = new() { Kind = K.Float, Size = 8 }, F32 = new() { Kind = K.Float, Size = 4 }, Bool = new() { Kind = K.Bool, Size = 1 }, Void = new() { Kind = K.Void, Size = 0 };
        public static readonly T VoidPtr = Ptr(Void);
        public static T Ptr(T e) => new() { Kind = K.Ptr, Size = 8, Elem = e };
        public bool IsInt => Kind == K.Int;
        public bool IsPtr => Kind == K.Ptr;
        public bool IsFloat => Kind == K.Float;
        public bool IsAggregate => Kind is K.Struct or K.Array; // значення-«блоки» пам'яті: у виразах дають вказівник на себе
        public bool InReg => Kind is K.Int or K.Bool or K.Ptr;
        public override string ToString() => Kind switch
        {
            K.Int => (Signed ? "int" : "uint") + Size * 8,
            K.Float => "float" + Size * 8, K.Bool => "bool", K.Void => "void",
            K.Ptr => $"ptr<{Elem}>", K.Array => $"[{Elem}; {Count}]", _ => Struct!,
        };
        public bool Same(T o) => Kind == K.Struct ? o.Kind == K.Struct && Struct == o.Struct : Kind == K.Array ? o.Kind == K.Array && Count == o.Count && Elem!.Same(o.Elem!) : Kind == o.Kind && Size == o.Size && Signed == o.Signed && Struct == o.Struct
            && (Kind != K.Ptr || Elem!.Same(o.Elem!));
    }

    sealed class StructInfo { public string Name = ""; public List<(string Name, T Type, int Offset)> Fields = new(); public int Size, Align = 1; }
    sealed class Func { public string Name = "", Label = ""; public List<(string Name, T Type)> Params = new(); public T Ret = T.Void; public FunctionDeclaration Decl = null!; public string? Owner; }

    readonly Dictionary<string, StructInfo> _structs = new();
    readonly Dictionary<string, Func> _funcs = new();
    readonly Dictionary<string, (T Type, string Label)> _globals = new();
    readonly StringBuilder _text = new(), _data = new(), _rodata = new();
    readonly Dictionary<string, string> _strings = new();
    int _label;

    // стан поточної функції
    StringBuilder _b = new();
    // області видимості як у C/Java: змінна живе до кінця свого блоку { } (слоти не перевикористовуються)
    List<Dictionary<string, (T Type, int Off)>> _scopes = new();
    bool TryLocal(string name, out (T Type, int Off) v)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--) if (_scopes[i].TryGetValue(name, out v)) return true;
        v = default;
        return false;
    }
    int _frame;           // байтів під локальні (від rbp вниз)
    int _depth;           // байтів, покладених push-ами поверх кадру (для вирівнювання на 16 перед call)
    Func _cur = null!;
    readonly Stack<(string Cont, string Brk)> _loops = new();
    int _line;

    Exception Err(string msg) => new($"рядок {_line}: {msg}");

    public string Compile(ProgramNode program, bool objectOnly = false)
    {
        var prelude = LoadPrelude();
        var all = new List<StatementNode>(prelude.Statements);
        all.AddRange(program.Statements);

        foreach (var s in all.OfType<StructDeclaration>()) _structs[s.Name] = new StructInfo { Name = s.Name };
        foreach (var s in all.OfType<StructDeclaration>()) _structDecls[s.Name] = s;
        foreach (var s in all.OfType<StructDeclaration>()) LayoutByName(s.Name);

        foreach (var st in all)
        {
            _line = st.Line;
            switch (st)
            {
                case StructDeclaration sd:
                    foreach (var m in sd.Methods) Declare(m, sd.Name);
                    break;
                case FunctionDeclaration fd:
                    if (fd.Name.Contains('.')) Declare(fd, fd.Name[..fd.Name.LastIndexOf('.')]);
                    else Declare(fd, null);
                    break;
                case VariableDeclaration vd: DeclareGlobal(vd); break;
                case ImportStatement: throw Err("import у системному режимі ще не підтримується");
                default: throw Err("на верхньому рівні дозволені лише func, struct і var (код - у func main)");
            }
        }

        _text.AppendLine("# Згенеровано NativeCodegenX64 (NyxilumLang, системний режим x86-64) - НЕ редагувати вручну.");
        _text.AppendLine(".section .text");
        if (!objectOnly)
        {
            if (!_funcs.TryGetValue("main", out var main)) throw new Exception("системний бекенд: у файлі немає func main() (для бібліотеки - --obj)");
            if (main.Params.Count > 0) throw new Exception("системний бекенд: main() поки без параметрів");
            _text.AppendLine(".globl _start");
            _text.AppendLine("_start:");
            _text.AppendLine("    xor %ebp, %ebp");
            _text.AppendLine("    and $-16, %rsp");
            _text.AppendLine($"    call {main.Label}");
            _text.AppendLine(main.Ret.InReg ? "    mov %rax, %rdi" : "    xor %edi, %edi");
            _text.AppendLine("    mov $60, %eax");
            _text.AppendLine("    syscall");
        }
        foreach (var f in _funcs.Values) CompileFunc(f);

        var o = new StringBuilder(_text.ToString());
        if (_rodata.Length > 0) { o.AppendLine(".section .rodata"); o.Append(_rodata); }
        if (_data.Length > 0) { o.AppendLine(".section .data"); o.Append(_data); }
        o.AppendLine(".section .note.GNU-stack,\"\",@progbits");
        return o.ToString();
    }

    static ProgramNode LoadPrelude()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("NyxilumLang.Native.X64Prelude.nx")
            ?? throw new Exception("системний бекенд: не знайдено вбудовану прелюдію X64Prelude.nx");
        using var r = new StreamReader(stream, Encoding.UTF8);
        return new Parser(new Lexer(r.ReadToEnd()).Tokenize()).ParseProgram();
    }

    // ---------- оголошення ----------
    T ParseType(string? s, bool param = false)
    {
        switch (s)
        {
            case null or "any" when param: return T.I64;
            case "int8": return T.I8; case "uint8": return T.U8; case "int16": return T.I16; case "uint16": return T.U16;
            case "int32": return T.I32; case "uint32": return T.U32; case "int64": return T.I64; case "uint64": return T.U64;
            case "float64" or "number": return T.F64;
            case "float32": return T.F32;
            case "bool": return T.Bool; case "void": return T.Void;
        }
        if (s != null && s.StartsWith("ptr<") && s.EndsWith(">")) return T.Ptr(ParseType(s[4..^1]));
        if (s != null && s.StartsWith("[") && s.EndsWith("]") && s.Contains(';'))
        {
            var el = ParseType(s[1..s.LastIndexOf(';')]);
            if (!int.TryParse(s[(s.LastIndexOf(';') + 1)..^1], out int cnt) || cnt <= 0) throw Err($"довжина масиву в {s} має бути додатним цілим");
            if (el.Kind == K.Void) throw Err("масив void не має сенсу");
            if (el.Kind == K.Struct) LayoutByName(el.Struct!);
            int es = el.Kind == K.Struct ? _structs[el.Struct!].Size : el.Size;
            return new T { Kind = K.Array, Elem = el, Count = cnt, Size = es * cnt, Align = AlignOf(el) };
        }
        if (s != null && _structs.ContainsKey(s)) return new T { Kind = K.Struct, Size = _structs[s].Size, Struct = s };
        throw Err($"невідомий тип «{s ?? "(не вказано)"}» (є int8..int64, uint8..uint64, float32, float64, bool, ptr<T>, структури)");
    }

    readonly Dictionary<string, StructDeclaration> _structDecls = new();
    readonly HashSet<string> _laidOut = new(), _layingOut = new();

    void LayoutByName(string name)
    {
        if (_laidOut.Contains(name)) return;
        if (!_layingOut.Add(name)) throw Err($"структура {name} містить сама себе за значенням - тримай ptr<{name}>");
        Layout(_structDecls[name]);
        _layingOut.Remove(name);
        _laidOut.Add(name);
    }

    int AlignOf(T t) => t.Kind switch
    {
        K.Struct => _structs[t.Struct!].Align,
        K.Array => t.Align,
        _ => Math.Max(1, t.Size),
    };

    // розмітка як у C: кожне поле вирівняне на свій розмір (структура/масив - на найбільше вирівнювання всередині)
    void Layout(StructDeclaration s)
    {
        int saved = _line;
        _line = s.Line;
        if (s.ParentName != null) throw Err("extends у системному режимі ще немає");
        var si = _structs[s.Name];
        int off = 0;
        foreach (var f in s.Fields)
        {
            var t = ParseType(f.Type);
            if (t.Kind == K.Void) throw Err($"поле {s.Name}.{f.Name}: void не може бути полем");
            if (t.Kind == K.Struct) { LayoutByName(t.Struct!); t = new T { Kind = K.Struct, Struct = t.Struct, Size = _structs[t.Struct!].Size }; }
            int a = AlignOf(t);
            off = (off + a - 1) / a * a;
            si.Fields.Add((f.Name, t, off));
            off += t.Size;
            si.Align = Math.Max(si.Align, a);
        }
        si.Size = Math.Max(1, (off + si.Align - 1) / si.Align * si.Align);
        _line = saved;
    }

    void Declare(FunctionDeclaration fd, string? owner)
    {
        _line = fd.Line;
        string bare = owner != null && fd.Name.Contains('.') ? fd.Name[(fd.Name.LastIndexOf('.') + 1)..] : fd.Name;
        string key = owner != null ? owner + "." + bare : bare;
        if (_funcs.ContainsKey(key)) throw Err($"функцію «{key}» оголошено двічі");
        if (fd.IsNaked) throw Err("naked-функції в x86-64 ще немає");
        var f = new Func { Name = key, Decl = fd, Owner = owner, Label = owner != null ? Mangle(owner) + "__" + Mangle(bare) : Mangle(bare) };
        foreach (var p in fd.Parameters)
            f.Params.Add((p.Name, owner != null && p.Name == "self" ? T.Ptr(ParseType(owner)) : ParseType(p.Type, param: true)));
        f.Ret = fd.ReturnType == null ? T.Void : ParseType(fd.ReturnType);
        if (f.Ret.Kind == K.Struct) throw Err("повертати структуру за значенням ще не можна - поверни ptr<…>");
        _funcs[key] = f;
    }

    void DeclareGlobal(VariableDeclaration vd)
    {
        _line = vd.Line;
        T t = vd.TypeAnnotation != null ? ParseType(vd.TypeAnnotation) : vd.Initializer != null ? LiteralType(vd.Initializer) : throw Err($"глобальна {vd.Name}: вкажи тип");
        if (t.Kind is K.Struct or K.Void) throw Err($"глобальна {vd.Name}: тип {t} не підходить");
        string label = "__g_" + Mangle(vd.Name);
        _globals[vd.Name] = (t, label);
        string dir = t.Size switch { 1 => ".byte", 2 => ".short", 4 => ".long", _ => ".quad" };
        string val;
        if (vd.Initializer == null) val = "0";
        else if (t.IsFloat) val = t.Size == 4 ? BitConverter.SingleToInt32Bits((float)ConstFloat(vd.Initializer)).ToString(CultureInfo.InvariantCulture) : BitConverter.DoubleToInt64Bits(ConstFloat(vd.Initializer)).ToString(CultureInfo.InvariantCulture);
        else if (t.Kind == K.Bool) val = vd.Initializer is LiteralExpression { Value: true } ? "1" : vd.Initializer is LiteralExpression { Value: false } ? "0" : throw Err("глобальна bool: лише true/false");
        else if (t.IsPtr && vd.Initializer is LiteralExpression { Value: string str }) val = StringLabel(str);
        else if (t.IsPtr && vd.Initializer is LiteralExpression { Value: null }) val = "0";
        else if (t.IsInt) val = ConstInt(vd.Initializer, t).ToString(CultureInfo.InvariantCulture);
        else throw Err($"глобальна {vd.Name}: початкове значення має бути сталою");
        _data.AppendLine($"    .balign {Math.Max(1, t.Size)}");
        _data.AppendLine($"{label}: {dir} {val}");
    }

    T LiteralType(ExpressionNode e) => e switch
    {
        LiteralExpression { Value: double } l when IsIntText(l) => T.I64,
        LiteralExpression { Value: double } => T.F64,
        LiteralExpression { Value: bool } => T.Bool,
        LiteralExpression { Value: string } => T.Ptr(T.U8),
        UnaryExpression { Operator: "-", Operand: var o } => LiteralType(o),
        _ => throw Err("глобальна змінна: початкове значення має бути сталою"),
    };

    static bool IsIntText(LiteralExpression l) => l.Value is double && (l.Raw == null ? Math.Floor((double)l.Value) == (double)l.Value && Math.Abs((double)l.Value) < 9e15 : !l.Raw.Contains('.'));

    long ConstInt(ExpressionNode e, T t)
    {
        bool neg = false;
        while (e is UnaryExpression { Operator: "-" } u) { neg = !neg; e = u.Operand; }
        if (e is not LiteralExpression l || l.Value is not double || !IsIntText(l)) throw Err("очікується ціла стала");
        string raw = l.Raw ?? ((double)l.Value).ToString("R", CultureInfo.InvariantCulture);
        long v;
        if (long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var sv)) v = sv;
        else if (ulong.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var uv)) v = unchecked((long)uv);
        else throw Err($"число {raw} не вміщається в 64 біти");
        if (neg) v = unchecked(-v);
        if (!Fits(v, t, neg)) throw Err($"стала {(neg ? "-" : "")}{raw} не вміщається в {t}");
        return Canon(v, t);
    }

    static bool Fits(long v, T t, bool negative)
    {
        if (t.Size == 8) return !(negative && !t.Signed && v != 0);
        int bits = t.Size * 8;
        if (t.Signed) return v >= -(1L << (bits - 1)) && v < (1L << (bits - 1));
        return v >= 0 && v < (1L << bits);
    }

    static long Canon(long v, T t) => t.Size switch
    {
        1 => t.Signed ? (sbyte)v : (byte)v,
        2 => t.Signed ? (short)v : (ushort)v,
        4 => t.Signed ? (int)v : (uint)v,
        _ => v,
    };

    double ConstFloat(ExpressionNode e)
    {
        bool neg = false;
        while (e is UnaryExpression { Operator: "-" } u) { neg = !neg; e = u.Operand; }
        if (e is not LiteralExpression { Value: double d } lit) throw Err("очікується дробова стала");
        if (lit.Raw != null) d = double.Parse(lit.Raw, CultureInfo.InvariantCulture);
        return neg ? -d : d;
    }

    static string Mangle(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name)
        {
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_') sb.Append(c);
            else sb.Append("_U").Append(((int)c).ToString("x4"));
        }
        return sb.ToString();
    }

    string StringLabel(string s)
    {
        if (_strings.TryGetValue(s, out var l)) return l;
        l = $".Lstr{_strings.Count}";
        _strings[s] = l;
        var bytes = Encoding.UTF8.GetBytes(s);
        _rodata.AppendLine($"{l}: .byte {string.Join(",", bytes.Append((byte)0))}");
        return l;
    }

    string NewLabel(string what) => $".L{what}{_label++}";
    void E(string line) => _b.AppendLine("    " + line);
    void L(string label) => _b.AppendLine(label + ":");

    // ---------- функції ----------
    static readonly string[] IntArgRegs = { "%rdi", "%rsi", "%rdx", "%rcx", "%r8", "%r9" };

    void CompileFunc(Func f)
    {
        _cur = f;
        _b = new StringBuilder();
        _scopes = new() { new() };
        _frame = 0;
        _depth = 0;
        _loops.Clear();
        _line = f.Decl.Line;
        int ni = 0, nf = 0;
        foreach (var (name, type) in f.Params)
        {
            if (type.Kind == K.Struct) throw Err($"параметр {name}: структуру передавай як ptr<{type}>");
            int off = Slot(name, type);
            if (type.IsFloat)
            {
                if (nf >= 8) throw Err("більше 8 дробових параметрів ще не підтримується");
                E($"movsd %xmm{nf++}, {off}(%rbp)");
            }
            else
            {
                if (ni >= 6) throw Err("більше 6 цілих параметрів ще не підтримується");
                // за System V старші біти вузьких цілих (і bool) у регістрі не визначені - канонізуємо
                E($"mov {IntArgRegs[ni++]}, %rax");
                if (type.Kind == K.Bool) E("movzbq %al, %rax"); else CanonReg(type);
                E($"mov %rax, {off}(%rbp)");
            }
        }
        foreach (var st in f.Decl.Body.Statements) Stmt(st);
        // кінець без return: 0
        E("xor %eax, %eax");
        E("pxor %xmm0, %xmm0");
        L($".Lret_{f.Label}");
        E("leave");
        E("ret");

        int frame = (_frame + 15) / 16 * 16;
        _text.AppendLine($".globl {f.Label}");
        _text.AppendLine($".type {f.Label}, @function");
        _text.AppendLine($"{f.Label}:");
        _text.AppendLine("    push %rbp");
        _text.AppendLine("    mov %rsp, %rbp");
        if (frame > 0) _text.AppendLine($"    sub ${frame}, %rsp");
        _text.Append(_b);
    }

    int Slot(string name, T type)
    {
        if (_scopes[^1].TryGetValue(name, out var ex))
        {
            if (!ex.Type.Same(type)) throw Err($"змінна «{name}» уже є з типом {ex.Type}, а тут {type}");
            return ex.Off;
        }
        _frame += 8;
        _scopes[^1][name] = (type, -_frame);
        return -_frame;
    }

    // ---------- інструкції ----------
    void Stmt(StatementNode st)
    {
        _line = st.Line;
        switch (st)
        {
            case ExpressionStatement es: Gen(es.Expression, null); break;
            case VariableDeclaration vd:
            {
                T t;
                if (vd.TypeAnnotation != null)
                {
                    t = ParseType(vd.TypeAnnotation);
                    if (t.Kind == K.Void) throw Err($"змінна {vd.Name}: тип void");
                    if (t.IsAggregate)
                    {
                        // масив чи структура на стеку (як у C): місце в кадрі, нулі; змінна - вказівник на нього
                        if (vd.Initializer != null) throw Err($"змінна {vd.Name}: {t} на стеку без початкового значення - заповни після");
                        int size = t.Kind == K.Struct ? _structs[t.Struct!].Size : t.Size;
                        _frame += (size + 15) / 16 * 16;
                        _frame = (_frame + 15) / 16 * 16;
                        E($"lea {-_frame}(%rbp), %rdi");
                        E($"mov ${size}, %ecx");
                        E("xor %eax, %eax");
                        E("rep stosb");
                        E($"lea {-_frame}(%rbp), %rax");
                        var pt = t.Kind == K.Array ? T.Ptr(t.Elem!) : T.Ptr(t);
                        StoreLocal(Slot(vd.Name, pt), pt);
                        break;
                    }
                    if (vd.Initializer != null) Convert(Gen(vd.Initializer, t), t, $"змінна {vd.Name}");
                    else { E("xor %eax, %eax"); E("pxor %xmm0, %xmm0"); }
                }
                else
                {
                    if (vd.Initializer == null) throw Err($"змінна {vd.Name}: вкажи тип або початкове значення");
                    t = Gen(vd.Initializer, null);
                    if (t.Kind == K.Void) throw Err($"змінна {vd.Name}: вираз нічого не повертає");
                }
                StoreLocal(Slot(vd.Name, t), t);
                break;
            }
            case IfStatement ifs:
            {
                if (TryJumpTable(ifs)) break;
                string elseL = NewLabel("else"), endL = NewLabel("endif");
                Cond(ifs.Condition, elseL);
                Block(ifs.ThenBlock);
                if (ifs.ElseBlock != null) { E($"jmp {endL}"); L(elseL); Block(ifs.ElseBlock); L(endL); }
                else L(elseL);
                break;
            }
            case WhileStatement ws:
            {
                string top = NewLabel("while"), end = NewLabel("wend");
                L(top);
                Cond(ws.Condition, end);
                _loops.Push((top, end));
                Block(ws.Body);
                _loops.Pop();
                E($"jmp {top}");
                L(end);
                break;
            }
            case ForStatement fs:
            {
                if (fs.End == null) throw Err("for по елементах масиву в системному режимі ще немає - лише for i in a..b");
                T t = IsIntLit(fs.Start) ? (IsIntLit(fs.End) ? T.I64 : TypeOf(fs.End)) : TypeOf(fs.Start);
                if (!t.IsInt) throw Err("for: межі мають бути цілими");
                Convert(Gen(fs.Start, t), t, "for");
                _scopes.Add(new()); // змінна циклу - лише всередині for
                int iv = Slot(fs.VariableName, t);
                StoreLocal(iv, t);
                int endSlot = Slot($"$for_end{_label++}", t);
                Convert(Gen(fs.End, t), t, "for");
                StoreLocal(endSlot, t);
                string top = NewLabel("for"), next = NewLabel("fnext"), end = NewLabel("fend");
                L(top);
                E($"mov {iv}(%rbp), %rax");
                E($"cmp {endSlot}(%rbp), %rax");
                E($"{(t.Signed ? "jge" : "jae")} {end}");
                _loops.Push((next, end));
                Block(fs.Body);
                _loops.Pop();
                L(next);
                E($"mov {iv}(%rbp), %rax");
                E("add $1, %rax");
                CanonReg(t);
                E($"mov %rax, {iv}(%rbp)");
                E($"jmp {top}");
                L(end);
                _scopes.RemoveAt(_scopes.Count - 1);
                break;
            }
            case BlockStatement bs: Block(bs); break;
            case BreakStatement: if (_loops.Count == 0) throw Err("break поза циклом"); E($"jmp {_loops.Peek().Brk}"); break;
            case ContinueStatement: if (_loops.Count == 0) throw Err("continue поза циклом"); E($"jmp {_loops.Peek().Cont}"); break;
            case ReturnStatement rs:
                if (rs.Value == null)
                {
                    if (_cur.Ret.Kind != K.Void) throw Err($"{_cur.Name} має повернути {_cur.Ret}");
                }
                else
                {
                    if (_cur.Ret.Kind == K.Void) throw Err($"{_cur.Name} нічого не повертає (додай -> тип)");
                    Convert(Gen(rs.Value, _cur.Ret), _cur.Ret, "return");
                }
                E($"jmp .Lret_{_cur.Label}");
                break;
            case PrintStatement ps: Print(ps.Expression, true); break;
            case TryStatement or ThrowStatement: throw Err("винятків (try/throw) у системному режимі ще немає");
            case FunctionDeclaration: throw Err("вкладені функції в системному режимі ще не підтримуються");
            default: throw Err($"інструкція {st.GetType().Name} ще не підтримується в системному режимі");
        }
    }

    // S4: ланцюжок «if x == 1 {..} else if x == 2 {..} ... else {..}» по одній цілій змінній і сталих -
    // таблиця переходів (один непрямий стрибок замість N порівнянь), як switch у C. Синтаксис мови той самий.
    bool TryJumpTable(IfStatement first)
    {
        string? var = null;
        T? vt = null;
        var cases = new List<(long Val, BlockStatement Body)>();
        BlockStatement? dflt = null;
        IfStatement? cur = first;
        while (cur != null)
        {
            if (cur.Condition is not BinaryExpression { Operator: "==" } eq) return false;
            ExpressionNode? lhs = null, rhs = null;
            if (eq.Left is VariableExpression && IsIntLit(eq.Right)) { lhs = eq.Left; rhs = eq.Right; }
            else if (eq.Right is VariableExpression && IsIntLit(eq.Left)) { lhs = eq.Right; rhs = eq.Left; }
            else return false;
            string name = ((VariableExpression)lhs).Name;
            if (var == null)
            {
                var = name;
                if (TryLocal(name, out var lv)) vt = lv.Type;
                else if (_globals.TryGetValue(name, out var gv)) vt = gv.Type;
                else return false;
                if (!vt.IsInt) return false;
            }
            else if (name != var) return false;
            long c;
            try { c = ConstInt(rhs, vt!); } catch (Exception) { return false; }
            if (cases.Any(x => x.Val == c)) return false;
            cases.Add((c, cur.ThenBlock));
            if (cur.ElseBlock is { Statements: [IfStatement next] }) cur = next;
            else { dflt = cur.ElseBlock; cur = null; }
        }
        if (cases.Count < 4) return false;
        long min = cases.Min(x => x.Val), max = cases.Max(x => x.Val);
        long range = vt!.Signed ? max - min : (long)((ulong)max - (ulong)min);
        if (range < 0 || range > 4096 || range > cases.Count * 4 + 16) return false;

        string tbl = NewLabel("jt"), def = NewLabel("jtdef"), end = NewLabel("jtend");
        Gen(new VariableExpression(var!), null);
        if (min != 0) { E($"movabs ${min}, %rcx"); E("sub %rcx, %rax"); }
        E($"cmp ${range}, %rax");
        E($"ja {def}");
        E($"lea {tbl}(%rip), %rcx");
        E("movslq (%rcx,%rax,4), %rdx");
        E("add %rcx, %rdx");
        E("jmp *%rdx");
        E(".balign 4");
        L(tbl);
        var labels = cases.ToDictionary(x => x.Val, _ => NewLabel("case"));
        for (long v = 0; v <= range; v++)
            E($".long {(labels.TryGetValue(min + v, out var lb) ? lb : def)} - {tbl}");
        foreach (var (val, body) in cases)
        {
            L(labels[val]);
            Block(body);
            E($"jmp {end}");
        }
        L(def);
        if (dflt != null) Block(dflt);
        L(end);
        return true;
    }

    void Block(BlockStatement b)
    {
        _scopes.Add(new());
        foreach (var s in b.Statements) Stmt(s);
        _scopes.RemoveAt(_scopes.Count - 1);
    }

    void Cond(ExpressionNode c, string falseLabel)
    {
        var t = Gen(c, null);
        if (t.Kind != K.Bool) throw Err($"умова має бути bool, а тут {t} (порівняй явно: x != 0)");
        E("test %al, %al");
        E($"jz {falseLabel}");
    }

    void Print(ExpressionNode e, bool newline)
    {
        var t = Gen(e, null);
        string fn = t.Kind switch
        {
            K.Int => t.Signed ? "__nx_print_i64" : "__nx_print_u64",
            K.Bool => "__nx_print_bool",
            K.Float => t.Size == 4 ? "__nx_print_f32" : "__nx_print_f64",
            K.Ptr when t.Elem!.Kind == K.Int && t.Elem.Size == 1 => "__nx_print_str",
            K.Ptr => "__nx_print_u64",
            _ => throw Err($"print не вміє друкувати {t}"),
        };
        if (!t.IsFloat) E("mov %rax, %rdi");
        CallLabel(_funcs[fn].Label);
        if (newline) CallLabel(_funcs["__nx_print_nl"].Label);
    }

    void CallLabel(string label)
    {
        bool pad = _depth % 16 != 0;
        if (pad) E("sub $8, %rsp");
        E("xor %eax, %eax");
        E($"call {label}");
        if (pad) E("add $8, %rsp");
    }

    void Push(T t)
    {
        if (t.IsFloat) { E("sub $8, %rsp"); E("movsd %xmm0, (%rsp)"); }
        else E("push %rax");
        _depth += 8;
    }

    void PopTo(string reg) { E($"pop {reg}"); _depth -= 8; }
    void PopXmm(int n) { E($"movsd (%rsp), %xmm{n}"); E("add $8, %rsp"); _depth -= 8; }

    void StoreLocal(int off, T t)
    {
        if (t.IsFloat) E($"movsd %xmm0, {off}(%rbp)");
        else E($"mov %rax, {off}(%rbp)");
    }

    void LoadLocal(int off, T t)
    {
        if (t.IsFloat) E($"movsd {off}(%rbp), %xmm0");
        else E($"mov {off}(%rbp), %rax");
    }

    // завантажити значення типу t з пам'яті addr (рядок адресації) у rax/xmm0
    void LoadMem(string addr, T t)
    {
        switch (t.Kind)
        {
            case K.Float: E(t.Size == 4 ? $"movss {addr}, %xmm0" : $"movsd {addr}, %xmm0"); return;
            case K.Bool: E($"movzbq {addr}, %rax"); return;
            case K.Ptr: E($"mov {addr}, %rax"); return;
            case K.Int:
                switch (t.Size)
                {
                    case 1: E(t.Signed ? $"movsbq {addr}, %rax" : $"movzbq {addr}, %rax"); return;
                    case 2: E(t.Signed ? $"movswq {addr}, %rax" : $"movzwq {addr}, %rax"); return;
                    case 4: E(t.Signed ? $"movslq {addr}, %rax" : $"movl {addr}, %eax"); return;
                    default: E($"mov {addr}, %rax"); return;
                }
            default: throw Err($"не можна прочитати значення типу {t}");
        }
    }

    void StoreMem(string addr, T t)
    {
        switch (t.Kind)
        {
            case K.Float: E(t.Size == 4 ? $"movss %xmm0, {addr}" : $"movsd %xmm0, {addr}"); return;
            case K.Bool: E($"movb %al, {addr}"); return;
            case K.Ptr: E($"mov %rax, {addr}"); return;
            case K.Int:
                E(t.Size switch { 1 => $"movb %al, {addr}", 2 => $"movw %ax, {addr}", 4 => $"movl %eax, {addr}", _ => $"mov %rax, {addr}" });
                return;
            default: throw Err($"не можна записати значення типу {t}");
        }
    }

    // привести ціле в rax до канонічного вигляду типу t
    void CanonReg(T t)
    {
        if (!t.IsInt) return;
        switch (t.Size)
        {
            case 1: E(t.Signed ? "movsbq %al, %rax" : "movzbq %al, %rax"); break;
            case 2: E(t.Signed ? "movswq %ax, %rax" : "movzwq %ax, %rax"); break;
            case 4: E(t.Signed ? "movslq %eax, %rax" : "mov %eax, %eax"); break;
        }
    }

    // перевірити, що значення типу from можна покласти туди, де чекають to (без неявних перетворень, крім ptr<void>)
    void Convert(T from, T to, string where)
    {
        if (from.Same(to)) return;
        if (from.IsPtr && to.IsPtr && (from.Elem!.Kind == K.Void || to.Elem!.Kind == K.Void)) return;
        throw Err($"{where}: очікується {to}, а тут {from} (перетвори явно: {(to.IsPtr ? "asptr(...)" : to + "(...)")})");
    }

    static bool IsIntLit(ExpressionNode e) => e switch
    {
        LiteralExpression { Value: double } l => IsIntText(l),
        UnaryExpression { Operator: "-" or "~", Operand: var o } => IsIntLit(o),
        _ => false,
    };

    static bool IsFloatLit(ExpressionNode e) => e switch
    {
        LiteralExpression { Value: double } l => !IsIntText(l),
        UnaryExpression { Operator: "-", Operand: var o } => IsFloatLit(o),
        _ => false,
    };

    // тип виразу без генерації коду (для підлаштування літералів)
    T TypeOf(ExpressionNode e)
    {
        var saved = _b; int sd = _depth, sl = _label; var sf = _frame;
        var savedScopes = _scopes.Select(d => new Dictionary<string, (T Type, int Off)>(d)).ToList();
        var savedRo = _rodata.Length; var savedStr = new Dictionary<string, string>(_strings);
        _b = new StringBuilder();
        try { return Gen(e, null); }
        finally { _b = saved; _depth = sd; _label = sl; _frame = sf; _scopes = savedScopes; _rodata.Length = savedRo; _strings.Clear(); foreach (var kv in savedStr) _strings[kv.Key] = kv.Value; }
    }

    // ---------- вирази: результат у rax (цілі, bool, ptr) або xmm0 (float64) ----------
    T Gen(ExpressionNode e, T? want)
    {
        switch (e)
        {
            case LiteralExpression { Value: double } lit:
            {
                if (want != null && !want.IsInt && !want.IsFloat) want = null;
                if (IsIntText(lit) && (want == null || want.IsInt))
                {
                    var t = want ?? T.I64;
                    E($"movabs ${ConstInt(lit, t)}, %rax");
                    return t;
                }
                double d = (double)lit.Value;
                if (lit.Raw != null) d = double.Parse(lit.Raw, CultureInfo.InvariantCulture);
                if (want != null && want.IsFloat && want.Size == 4)
                {
                    E($"mov ${BitConverter.SingleToInt32Bits((float)d)}, %eax");
                    E("movd %eax, %xmm0");
                    return T.F32;
                }
                E($"movabs ${BitConverter.DoubleToInt64Bits(d)}, %rax");
                E("movq %rax, %xmm0");
                return T.F64;
            }
            case LiteralExpression { Value: bool bv }: E($"mov ${(bv ? 1 : 0)}, %eax"); return T.Bool;
            case LiteralExpression { Value: string s }: E($"lea {StringLabel(s)}(%rip), %rax"); return T.Ptr(T.U8);
            case LiteralExpression { Value: null }: E("xor %eax, %eax"); return T.VoidPtr;
            case VariableExpression v:
            {
                if (TryLocal(v.Name, out var loc)) { LoadLocal(loc.Off, loc.Type); return loc.Type; }
                if (_globals.TryGetValue(v.Name, out var g)) { LoadMem($"{g.Label}(%rip)", g.Type); return g.Type; }
                if (_funcs.ContainsKey(v.Name)) throw Err($"«{v.Name}» - функція; адресу дає funcaddr({v.Name})");
                throw Err($"невідома змінна «{v.Name}»");
            }
            case UnaryExpression u: return Unary(u, want);
            case BinaryExpression { Operator: "=" } asg: return Assign(asg);
            case BinaryExpression { Operator: "&&" or "||" } lg:
            {
                string end = NewLabel("sc");
                var lt = Gen(lg.Left, null);
                if (lt.Kind != K.Bool) throw Err($"{lg.Operator}: потрібен bool, а тут {lt}");
                E("test %al, %al");
                E(lg.Operator == "&&" ? $"jz {end}" : $"jnz {end}");
                var rt = Gen(lg.Right, null);
                if (rt.Kind != K.Bool) throw Err($"{lg.Operator}: потрібен bool, а тут {rt}");
                L(end);
                return T.Bool;
            }
            case BinaryExpression bin: return Binary(bin, want);
            case CallExpression call: return Call(call, want);
            case MethodCallExpression mc: return Method(mc);
            case IndexExpression or MemberAccessExpression:
            {
                var et = Address(e);
                if (et.IsAggregate) return et.Kind == K.Array ? T.Ptr(et.Elem!) : T.Ptr(et); // вкладена структура / масив - вказівник на нього
                LoadMem("(%rax)", et);
                return et;
            }
            case StructInitExpression si:
            {
                if (!_structs.TryGetValue(si.StructName, out var info)) throw Err($"невідома структура {si.StructName}");
                E($"mov ${info.Size}, %edi");
                CallLabel(_funcs["__nx_alloc"].Label);
                Push(T.VoidPtr);
                foreach (var fi in si.Fields)
                {
                    var f = info.Fields.FirstOrDefault(x => x.Name == fi.Name);
                    if (f.Name == null) throw Err($"у {info.Name} немає поля {fi.Name}");
                    if (f.Type.IsAggregate) throw Err($"{info.Name}.{f.Name}: поле-{(f.Type.Kind == K.Array ? "масив" : "структуру")} заповнюй після створення");
                    Convert(Gen(fi.Value, f.Type), f.Type, $"{info.Name}.{f.Name}");
                    E("mov (%rsp), %rcx");
                    StoreMem($"{f.Offset}(%rcx)", f.Type);
                }
                PopTo("%rax");
                return T.Ptr(ParseType(info.Name));
            }
            default: throw Err($"вираз {e.GetType().Name} ще не підтримується в системному режимі");
        }
    }

    // адреса lvalue (p[i], p.поле) у rax; повертає тип значення за адресою
    T Address(ExpressionNode e)
    {
        switch (e)
        {
            case IndexExpression ix:
            {
                var pt = Gen(ix.Array, null);
                if (!pt.IsPtr) throw Err($"індексувати можна лише ptr<T>, а тут {pt}");
                if (pt.Elem!.Kind is K.Void) throw Err("ptr<void> не можна індексувати - спершу присвой у ptr<T>");
                Push(pt);
                var it = Gen(ix.Index, IsIntLit(ix.Index) ? T.I64 : null);
                if (!it.IsInt) throw Err($"індекс має бути цілим, а тут {it}");
                PopTo("%rcx");
                E($"lea (%rcx,%rax,{Scale(ElemSize(pt), out var extra)}), %rax");
                if (extra != null) throw Err("внутрішня помилка масштабу");
                return pt.Elem;
            }
            case MemberAccessExpression ma:
            {
                var ot = Gen(ma.Object, null);
                if (!(ot.IsPtr && ot.Elem!.Kind == K.Struct)) throw Err($".{ma.Member}: потрібен вказівник на структуру, а тут {ot}");
                var info = _structs[ot.Elem.Struct!];
                var f = info.Fields.FirstOrDefault(x => x.Name == ma.Member);
                if (f.Name == null) throw Err($"у {info.Name} немає поля {ma.Member}");
                if (f.Offset != 0) E($"add ${f.Offset}, %rax");
                return f.Type;
            }
            default: throw Err("ліворуч від = має бути змінна, p[i] або p.поле");
        }
    }

    string Scale(int size, out string? extra)
    {
        extra = null;
        if (size is 1 or 2 or 4 or 8) return size.ToString();
        // нестандартний розмір: rax *= size, далі масштаб 1
        E($"imul ${size}, %rax, %rax");
        return "1";
    }

    T Assign(BinaryExpression asg)
    {
        if (asg.Left is VariableExpression v)
        {
            if (TryLocal(v.Name, out var loc))
            {
                Convert(Gen(asg.Right, loc.Type), loc.Type, $"присвоєння {v.Name}");
                StoreLocal(loc.Off, loc.Type);
                return loc.Type;
            }
            if (_globals.TryGetValue(v.Name, out var g))
            {
                Convert(Gen(asg.Right, g.Type), g.Type, $"присвоєння {v.Name}");
                StoreMem($"{g.Label}(%rip)", g.Type);
                return g.Type;
            }
            throw Err($"невідома змінна «{v.Name}» (оголоси: var {v.Name} = ...)");
        }
        var t = Address(asg.Left);
        if (t.IsAggregate) throw Err($"присвоїти {t} цілком не можна - копіюй memcpy(куди, звідки, sizeof(...)) або по полях");
        Push(T.VoidPtr);
        Convert(Gen(asg.Right, t), t, "присвоєння");
        PopTo("%rcx");
        StoreMem("(%rcx)", t);
        return t;
    }

    T Unary(UnaryExpression u, T? want)
    {
        switch (u.Operator)
        {
            case "!":
            {
                var t = Gen(u.Operand, null);
                if (t.Kind != K.Bool) throw Err($"! потребує bool, а тут {t}");
                E("xor $1, %eax");
                return T.Bool;
            }
            case "-":
            {
                if (IsIntLit(u) && (want == null || want.IsInt)) { var lt = want ?? T.I64; E($"movabs ${ConstInt(u, lt)}, %rax"); return lt; }
                var t = Gen(u.Operand, want);
                if (t.IsFloat && t.Size == 4) { E("movd %xmm0, %eax"); E("btc $31, %eax"); E("movd %eax, %xmm0"); return t; }
                if (t.IsFloat) { E("movq %xmm0, %rax"); E("btc $63, %rax"); E("movq %rax, %xmm0"); return t; }
                if (!t.IsInt) throw Err($"унарний мінус для {t} не має сенсу");
                E("neg %rax");
                CanonReg(t);
                return t;
            }
            case "~":
            {
                var t = Gen(u.Operand, want != null && want.IsInt ? want : null);
                if (!t.IsInt) throw Err($"~ лише для цілих, а тут {t}");
                E("not %rax");
                CanonReg(t);
                return t;
            }
        }
        throw Err($"невідомий оператор {u.Operator}");
    }

    T Binary(BinaryExpression bin, T? want)
    {
        string op = bin.Operator;
        bool cmp = op is "==" or "!=" or "<" or "<=" or ">" or ">=";
        // типи операндів: літерал підлаштовується під інший бік (або під want)
        T? lh = null, rh = null;
        bool ll = IsIntLit(bin.Left) || IsFloatLit(bin.Left), rl = IsIntLit(bin.Right) || IsFloatLit(bin.Right);
        if (want != null && !want.IsInt && !want.IsFloat) want = null;
        if (ll && rl) { lh = rh = cmp ? null : want; if (IsFloatLit(bin.Left) || IsFloatLit(bin.Right)) lh = rh = want != null && want.IsFloat ? want : T.F64; }
        else if (ll) { var rt0 = TypeOf(bin.Right); lh = rt0.IsPtr && op is "+" or "-" ? T.I64 : rt0.IsPtr ? T.VoidPtr : rt0; }
        else if (rl) { var lt0 = TypeOf(bin.Left); rh = lt0.IsPtr && op is "+" or "-" ? T.I64 : lt0; }
        if (op is "<<" or ">>") rh = IsIntLit(bin.Right) ? (lh ?? TypeOf(bin.Left)) : null;
        if (lh != null && lh.IsPtr) lh = null;
        if (rh != null && rh.IsPtr) rh = null;

        var lt = Gen(bin.Left, lh);
        Push(lt);
        var rt = Gen(bin.Right, rh);

        // float64
        if (lt.IsFloat || rt.IsFloat)
        {
            if (!(lt.IsFloat && rt.IsFloat) || lt.Size != rt.Size) throw Err($"{op}: {lt} і {rt} - перетвори явно {(lt.IsFloat ? lt : rt)}(...)");
            E("movapd %xmm0, %xmm1");
            PopXmm(0);
            string x = lt.Size == 4 ? "ss" : "sd";
            switch (op)
            {
                case "+": E($"add{x} %xmm1, %xmm0"); return lt;
                case "-": E($"sub{x} %xmm1, %xmm0"); return lt;
                case "*": E($"mul{x} %xmm1, %xmm0"); return lt;
                case "/": E($"div{x} %xmm1, %xmm0"); return lt;
                case "%":
                {
                    // залишок як у Java/C fmod: знак діленого, через x87 fprem (точний)
                    string ld = lt.Size == 4 ? "flds" : "fldl", st = lt.Size == 4 ? "fstps" : "fstpl", again = NewLabel("fprem");
                    E("sub $16, %rsp");
                    E($"movs{x[1]} %xmm1, 8(%rsp)");
                    E($"movs{x[1]} %xmm0, (%rsp)");
                    E($"{ld} 8(%rsp)");
                    E($"{ld} (%rsp)");
                    L(again);
                    E("fprem");
                    E("fnstsw %ax");
                    E("test $0x400, %ax");
                    E($"jnz {again}");
                    E("fstp %st(1)");
                    E($"{st} (%rsp)");
                    E($"movs{x[1]} (%rsp), %xmm0");
                    E("add $16, %rsp");
                    return lt;
                }
                case "<": E($"ucomi{x} %xmm0, %xmm1"); E("seta %al"); break;
                case "<=": E($"ucomi{x} %xmm0, %xmm1"); E("setae %al"); break;
                case ">": E($"ucomi{x} %xmm1, %xmm0"); E("seta %al"); break;
                case ">=": E($"ucomi{x} %xmm1, %xmm0"); E("setae %al"); break;
                case "==": E($"ucomi{x} %xmm1, %xmm0"); E("sete %al"); E("setnp %cl"); E("and %cl, %al"); break;
                case "!=": E($"ucomi{x} %xmm1, %xmm0"); E("setne %al"); E("setp %cl"); E("or %cl, %al"); break;
                default: throw Err($"оператор {op} для {lt} немає");
            }
            E("movzbq %al, %rax");
            return T.Bool;
        }

        E("mov %rax, %rcx");
        PopTo("%rax");
        // тепер: ліве в rax, праве в rcx

        if (lt.Kind == K.Bool || rt.Kind == K.Bool)
        {
            if (!(lt.Kind == K.Bool && rt.Kind == K.Bool) || op is not ("==" or "!=")) throw Err($"{op}: {lt} і {rt} не поєднуються");
            E("cmp %rcx, %rax"); E(op == "==" ? "sete %al" : "setne %al"); E("movzbq %al, %rax");
            return T.Bool;
        }

        // вказівники
        if (lt.IsPtr || rt.IsPtr)
        {
            if (op == "+" && lt.IsPtr && rt.IsInt) { PtrAdd(lt, "%rcx"); return lt; }
            if (op == "+" && lt.IsInt && rt.IsPtr) { E("xchg %rax, %rcx"); PtrAdd(rt, "%rcx"); return rt; }
            if (op == "-" && lt.IsPtr && rt.IsInt) { E("neg %rcx"); PtrAdd(lt, "%rcx"); return lt; }
            if (op == "-" && lt.IsPtr && rt.IsPtr)
            {
                if (!lt.Same(rt)) throw Err($"різниця вказівників різних типів: {lt} і {rt}");
                E("sub %rcx, %rax");
                int sz = ElemSize(lt);
                if (sz > 1) { E($"mov ${sz}, %rcx"); E("cqo"); E("idiv %rcx"); }
                return T.I64;
            }
            if (cmp && lt.IsPtr && rt.IsPtr)
            {
                if (!(lt.Same(rt) || lt.Elem!.Kind == K.Void || rt.Elem!.Kind == K.Void)) throw Err($"порівняння вказівників різних типів: {lt} і {rt}");
                return Compare(op, signed: false);
            }
            throw Err($"{op}: {lt} і {rt} не поєднуються");
        }

        if (!lt.IsInt || !rt.IsInt) throw Err($"{op}: {lt} і {rt} не поєднуються");
        bool shift = op is "<<" or ">>";
        if (!shift && !lt.Same(rt)) throw Err($"{op}: різні типи {lt} і {rt} - перетвори один явно, напр. {lt}(...)");
        if (cmp) return Compare(op, lt.Signed);
        bool w32 = lt.Size <= 4;
        switch (op)
        {
            case "+": E("add %rcx, %rax"); break;
            case "-": E("sub %rcx, %rax"); break;
            case "*": E("imul %rcx, %rax"); break;
            case "/" or "%":
                if (lt.Signed && lt.Size == 8)
                {
                    // як у Java: MIN / -1 = MIN, MIN % -1 = 0 (idiv на цьому падає); x / -1 = -x
                    string norm = NewLabel("div"), done = NewLabel("divd");
                    E("cmp $-1, %rcx");
                    E($"jne {norm}");
                    E(op == "/" ? "neg %rax" : "xor %eax, %eax");
                    E($"jmp {done}");
                    L(norm);
                    E("cqo"); E("idiv %rcx");
                    if (op == "%") E("mov %rdx, %rax");
                    L(done);
                    break;
                }
                if (lt.Signed) { E("cqo"); E("idiv %rcx"); } else { E("xor %edx, %edx"); E("div %rcx"); }
                if (op == "%") E("mov %rdx, %rax");
                break;
            case "&": E("and %rcx, %rax"); break;
            case "|": E("or %rcx, %rax"); break;
            case "^": E("xor %rcx, %rax"); break;
            case "<<": E(w32 ? "shl %cl, %eax" : "shl %cl, %rax"); break;
            case ">>":
                if (w32) E(lt.Signed ? "sar %cl, %eax" : "shr %cl, %eax");
                else E(lt.Signed ? "sar %cl, %rax" : "shr %cl, %rax");
                break;
            default: throw Err($"невідомий оператор {op}");
        }
        CanonReg(lt);
        return lt;
    }

    int ElemSize(T p) => p.Elem!.Kind == K.Void ? 1 : p.Elem.Kind == K.Struct ? _structs[p.Elem.Struct!].Size : p.Elem.Size;

    void PtrAdd(T p, string idxReg)
    {
        int sz = ElemSize(p);
        if (sz is 1 or 2 or 4 or 8) E($"lea (%rax,{idxReg},{sz}), %rax");
        else { E($"imul ${sz}, {idxReg}, {idxReg}"); E($"add {idxReg}, %rax"); }
    }

    T Compare(string op, bool signed)
    {
        E("cmp %rcx, %rax");
        string cc = op switch
        {
            "==" => "e", "!=" => "ne",
            "<" => signed ? "l" : "b", "<=" => signed ? "le" : "be",
            ">" => signed ? "g" : "a", _ => signed ? "ge" : "ae",
        };
        E($"set{cc} %al");
        E("movzbq %al, %rax");
        return T.Bool;
    }

    // ---------- виклики ----------
    T Call(CallExpression call, T? want)
    {
        string n = call.FunctionName;
        var a = call.Arguments;
        switch (n)
        {
            case "int8" or "uint8" or "int16" or "uint16" or "int32" or "uint32" or "int64" or "uint64" or "float64" or "float32" when a.Count == 1:
                return Cast(a[0], ParseType(n));
            case "sizeof":
                if (a.Count != 1 || a[0] is not VariableExpression tv) throw Err("sizeof(тип)");
                var st = ParseType(tv.Name);
                E($"mov ${(st.Kind == K.Struct ? _structs[st.Struct!].Size : st.Size)}, %eax");
                return T.I64;
            case "stackbuf":
            {
                if (a.Count != 1) throw Err("stackbuf(N)");
                long size = ConstInt(a[0], T.I64);
                if (size <= 0 || size > 1 << 20) throw Err("stackbuf: від 1 байта до 1 МБ");
                _frame += (int)((size + 15) / 16 * 16);
                _frame = (_frame + 15) / 16 * 16;
                E($"lea {-_frame}(%rbp), %rax");
                return T.Ptr(T.U8);
            }
            case "new":
            {
                if (a.Count != 1 || a[0] is not VariableExpression sv || !_structs.TryGetValue(sv.Name, out var info)) throw Err("new(Структура)");
                E($"mov ${info.Size}, %edi");
                CallLabel(_funcs["__nx_alloc"].Label);
                return T.Ptr(ParseType(sv.Name));
            }
            case "asptr":
            {
                if (a.Count != 1) throw Err("asptr(ціле або вказівник)");
                var t = Gen(a[0], IsIntLit(a[0]) ? T.I64 : null);
                if (!t.IsInt && !t.IsPtr) throw Err($"asptr: потрібне ціле або вказівник, а тут {t}");
                return T.VoidPtr;
            }
            case "funcaddr":
            {
                if (a.Count != 1 || a[0] is not VariableExpression fv || !_funcs.TryGetValue(fv.Name, out var ff)) throw Err("funcaddr(ім'я функції)");
                E($"lea {ff.Label}(%rip), %rax");
                return T.VoidPtr;
            }
            case "float64Bits":
            {
                if (a.Count != 1) throw Err("float64Bits(x)");
                var t = Gen(a[0], T.F64);
                if (!t.IsFloat || t.Size != 8) throw Err($"float64Bits: потрібен float64, а тут {t}");
                E("movq %xmm0, %rax");
                return T.I64;
            }
            case "float32Bits":
            {
                if (a.Count != 1) throw Err("float32Bits(x)");
                var t = Gen(a[0], T.F32);
                if (!t.IsFloat || t.Size != 4) throw Err($"float32Bits: потрібен float32, а тут {t}");
                E("movd %xmm0, %eax");
                E("movslq %eax, %rax");
                return T.I32;
            }
            case "bitsToFloat32":
            {
                if (a.Count != 1) throw Err("bitsToFloat32(n)");
                var t = Gen(a[0], T.I32);
                if (!t.IsInt || t.Size != 4) throw Err($"bitsToFloat32: потрібне int32/uint32, а тут {t}");
                E("movd %eax, %xmm0");
                return T.F32;
            }
            case "bitsToFloat64":
            {
                if (a.Count != 1) throw Err("bitsToFloat64(n)");
                var t = Gen(a[0], T.I64);
                if (!t.IsInt || t.Size != 8) throw Err($"bitsToFloat64: потрібне int64/uint64, а тут {t}");
                E("movq %rax, %xmm0");
                return T.F64;
            }
            case "syscall":
            {
                if (a.Count is < 1 or > 7) throw Err("syscall(номер, до 6 аргументів)");
                foreach (var arg in a)
                {
                    var t = Gen(arg, IsIntLit(arg) ? T.I64 : null);
                    if (!t.InReg) throw Err($"syscall: аргумент {t} - лише цілі, bool і вказівники");
                    Push(t);
                }
                string[] regs = { "%rax", "%rdi", "%rsi", "%rdx", "%r10", "%r8", "%r9" };
                for (int i = a.Count - 1; i >= 0; i--) PopTo(regs[i]);
                E("syscall");
                return T.I64;
            }
            case "callptr":
            {
                if (a.Count < 1) throw Err("callptr(адреса, аргументи...)");
                var ft = Gen(a[0], null);
                if (!ft.IsPtr) throw Err("callptr: першим має бути адреса функції (funcaddr)");
                Push(ft);
                return EmitCall(a.Skip(1).ToList(), null, "*%r11", T.I64, popCalleeToR11: true);
            }
            case "alloc":
            {
                if (a.Count != 1) throw Err("alloc(байтів)");
                return EmitCall(a, _funcs["__nx_alloc"].Params.Select(p => p.Type).ToList(), _funcs["__nx_alloc"].Label, T.VoidPtr);
            }
            case "addr":
            {
                // адреса змінної, елемента p[i] чи поля p.x
                if (a.Count != 1) throw Err("addr(змінна, p[i] або p.поле)");
                if (a[0] is VariableExpression av)
                {
                    if (TryLocal(av.Name, out var lv)) { E($"lea {lv.Off}(%rbp), %rax"); return T.Ptr(lv.Type); }
                    if (_globals.TryGetValue(av.Name, out var gv)) { E($"lea {gv.Label}(%rip), %rax"); return T.Ptr(gv.Type); }
                    throw Err($"невідома змінна «{av.Name}»");
                }
                var at = Address(a[0]);
                return at.Kind == K.Array ? T.Ptr(at.Elem!) : T.Ptr(at);
            }
            case "offsetof":
            {
                if (a.Count != 2 || a[0] is not VariableExpression os || a[1] is not VariableExpression of || !_structs.TryGetValue(os.Name, out var oi)) throw Err("offsetof(Структура, поле)");
                var fo = oi.Fields.FirstOrDefault(x => x.Name == of.Name);
                if (fo.Name == null) throw Err($"у {oi.Name} немає поля {of.Name}");
                E($"mov ${fo.Offset}, %eax");
                return T.I64;
            }
            case "memcpy" or "memmove" or "memset":
            {
                // memcpy/memmove(куди, звідки, байтів) - перекриття дозволене; memset(куди, байт, байтів)
                if (a.Count != 3) throw Err($"{n}(куди, {(n == "memset" ? "байт" : "звідки")}, байтів)");
                for (int i = 0; i < 3; i++)
                {
                    var t = Gen(a[i], IsIntLit(a[i]) ? T.I64 : null);
                    if (n == "memset" && i == 1 ? !t.IsInt : i == 2 ? !t.IsInt : !t.IsPtr) throw Err($"{n}: аргумент {i + 1} - {t} не підходить");
                    Push(t);
                }
                PopTo("%rcx");
                PopTo(n == "memset" ? "%rax" : "%rsi");
                PopTo("%rdi");
                if (n == "memset") { E("mov %rdi, %rdx"); E("rep stosb"); E("mov %rdx, %rax"); return T.VoidPtr; }
                string back = NewLabel("mmback"), done = NewLabel("mmdone");
                E("mov %rdi, %rdx");
                E("cmp %rsi, %rdi");
                E($"jbe {done}0");
                E("lea (%rsi,%rcx), %rax");
                E("cmp %rax, %rdi");
                E($"jb {back}");
                L($"{done}0");
                E("rep movsb");
                E($"jmp {done}");
                L(back);
                E("lea -1(%rsi,%rcx), %rsi");
                E("lea -1(%rdi,%rcx), %rdi");
                E("std");
                E("rep movsb");
                E("cld");
                L(done);
                E("mov %rdx, %rax");
                return T.VoidPtr;
            }
            case "free":
            {
                if (a.Count != 1) throw Err("free(вказівник з alloc/new)");
                return EmitCall(a, new List<T> { T.VoidPtr }, _funcs["__nx_release"].Label, T.Void);
            }
            case "realloc":
            {
                if (a.Count != 2) throw Err("realloc(вказівник, байтів)");
                return EmitCall(a, new List<T> { T.VoidPtr, T.I64 }, _funcs["__nx_realloc"].Label, T.VoidPtr);
            }
            case "asm":
                // вставка асемблера (AT&T) як є; значення - rax після вставки
                if (a.Count != 1 || a[0] is not LiteralExpression { Value: string code }) throw Err("asm(\"інструкції\") - лише рядковий літерал");
                foreach (var line in code.Split('\n')) E(line.Trim());
                return T.I64;
            case "put":
                if (a.Count != 1) throw Err("put(значення)");
                Print(a[0], false);
                return T.Void;
            case "print":
                if (a.Count != 1) throw Err("print(значення)");
                Print(a[0], true);
                return T.Void;
        }
        if (!_funcs.TryGetValue(n, out var f) || f.Owner != null) throw Err($"невідома функція «{n}»");
        if (a.Count != f.Params.Count) throw Err($"{n} чекає {f.Params.Count} аргумент(и), а передано {a.Count}");
        return EmitCall(a, f.Params.Select(p => p.Type).ToList(), f.Label, f.Ret);
    }

    T Method(MethodCallExpression mc)
    {
        var ot = TypeOf(mc.Object);
        if (!(ot.IsPtr && ot.Elem!.Kind == K.Struct)) throw Err($".{mc.MethodName}(): потрібен вказівник на структуру, а тут {ot}");
        string key = ot.Elem.Struct + "." + mc.MethodName;
        if (!_funcs.TryGetValue(key, out var f)) throw Err($"у {ot.Elem.Struct} немає методу {mc.MethodName}");
        var args = new List<ExpressionNode> { mc.Object };
        args.AddRange(mc.Arguments);
        if (args.Count != f.Params.Count) throw Err($"{key} чекає {f.Params.Count - 1} аргумент(и), а передано {mc.Arguments.Count}");
        return EmitCall(args, f.Params.Select(p => p.Type).ToList(), f.Label, f.Ret);
    }

    // обчислити аргументи (зліва направо, на стек), розкласти по регістрах System V і викликати
    T EmitCall(List<ExpressionNode> args, List<T>? types, string target, T ret, bool popCalleeToR11 = false)
    {
        var got = new List<T>();
        for (int i = 0; i < args.Count; i++)
        {
            T? hint = types?[i];
            if (hint == null && IsIntLit(args[i])) hint = T.I64;
            if (hint != null && hint.IsPtr) hint = null;
            var t = Gen(args[i], hint);
            if (types != null) Convert(t, types[i], $"аргумент {i + 1}");
            if (t.Kind is K.Void or K.Struct) throw Err($"аргумент {i + 1}: {t} не можна передати");
            got.Add(t);
            Push(t);
        }
        int ni = got.Count(t => !t.IsFloat), nf = got.Count(t => t.IsFloat);
        if (ni > 6 || nf > 8) throw Err("більше 6 цілих / 8 дробових аргументів ще не підтримується");
        int ii = ni, fi = nf;
        for (int i = got.Count - 1; i >= 0; i--)
        {
            if (got[i].IsFloat) PopXmm(--fi);
            else PopTo(IntArgRegs[--ii]);
        }
        if (popCalleeToR11) PopTo("%r11");
        bool pad = _depth % 16 != 0;
        if (pad) E("sub $8, %rsp");
        E($"mov ${nf}, %eax");
        E($"call {target}");
        if (pad) E("add $8, %rsp");
        if (ret.IsInt) CanonReg(ret);
        else if (ret.Kind == K.Bool) E("movzbq %al, %rax");
        return ret;
    }

    T Cast(ExpressionNode arg, T to)
    {
        var from = Gen(arg, IsIntLit(arg) ? T.I64 : IsFloatLit(arg) ? (to.IsFloat ? to : T.F64) : null);
        if (to.IsInt)
        {
            if (from.IsInt) { CanonReg(to); return to; }
            if (from.IsPtr && to.Size == 8) return to;
            if (from.Kind == K.Bool) return to;
            if (from.IsFloat)
            {
                if (from.Size == 4) E("cvtss2sd %xmm0, %xmm0"); // float32 -> float64 точно
                FloatToInt(to);
                return to;
            }
        }
        if (to.IsFloat)
        {
            string x = to.Size == 4 ? "ss" : "sd";
            if (from.IsFloat)
            {
                if (from.Size == 8 && to.Size == 4) E("cvtsd2ss %xmm0, %xmm0");
                else if (from.Size == 4 && to.Size == 8) E("cvtss2sd %xmm0, %xmm0");
                return to;
            }
            if (from.IsInt)
            {
                if (from.Size == 8 && !from.Signed)
                {
                    // uint64 -> дробове: старший біт - через ділення навпіл зі збереженням молодшого (правильне округлення)
                    string neg = NewLabel("u2f"), done = NewLabel("u2fdone");
                    E("test %rax, %rax");
                    E($"js {neg}");
                    E($"cvtsi2{x} %rax, %xmm0");
                    E($"jmp {done}");
                    L(neg);
                    E("mov %rax, %rcx");
                    E("shr $1, %rcx");
                    E("and $1, %eax");
                    E("or %rax, %rcx");
                    E($"cvtsi2{x} %rcx, %xmm0");
                    E($"add{x} %xmm0, %xmm0");
                    L(done);
                    return to;
                }
                E($"cvtsi2{x}q %rax, %xmm0");
                return to;
            }
        }
        throw Err($"перетворення {from} -> {to} не підтримується");
    }

    // float64 у xmm0 -> ціле типу to у rax, як у Java (d2i/d2l): NaN -> 0, за межами - найближча межа типу
    void FloatToInt(T to)
    {
        string zero = NewLabel("f2i0"), max = NewLabel("f2imax"), clamp = NewLabel("f2iclamp"), done = NewLabel("f2idone");
        E("ucomisd %xmm0, %xmm0");
        E($"jp {zero}");                                   // NaN
        if (to.Size == 8 && !to.Signed)
        {
            string big = NewLabel("f2ubig");
            E("pxor %xmm1, %xmm1");
            E("ucomisd %xmm1, %xmm0");
            E($"jbe {zero}");                              // <= 0 (і -0.0)
            E("movabs $0x43f0000000000000, %rax");         // 2^64
            E("movq %rax, %xmm1");
            E("ucomisd %xmm1, %xmm0");
            E($"jae {max}");
            E("movabs $0x43e0000000000000, %rax");         // 2^63
            E("movq %rax, %xmm1");
            E("ucomisd %xmm1, %xmm0");
            E($"jae {big}");
            E("cvttsd2si %xmm0, %rax");
            E($"jmp {done}");
            L(big);
            E("subsd %xmm1, %xmm0");
            E("cvttsd2si %xmm0, %rax");
            E("btc $63, %rax");
            E($"jmp {done}");
            L(max);
            E("mov $-1, %rax");
            E($"jmp {done}");
            L(zero);
            E("xor %eax, %eax");
            L(done);
            return;
        }
        E("movabs $0x43e0000000000000, %rax");             // 2^63
        E("movq %rax, %xmm1");
        E("ucomisd %xmm1, %xmm0");
        E($"jae {max}");
        E("cvttsd2si %xmm0, %rax");                        // < -2^63 дає MIN - це й є насичення
        E($"jmp {clamp}");
        L(max);
        E("movabs $0x7fffffffffffffff, %rax");
        E($"jmp {clamp}");
        L(zero);
        E("xor %eax, %eax");
        E($"jmp {done}");
        L(clamp);
        if (to.Size < 8)
        {
            long lo = to.Signed ? -(1L << (to.Size * 8 - 1)) : 0, hi = to.Signed ? (1L << (to.Size * 8 - 1)) - 1 : (1L << (to.Size * 8)) - 1;
            string okLo = NewLabel("f2ilo"), okHi = NewLabel("f2ihi");
            E($"movabs ${lo}, %rcx");
            E("cmp %rcx, %rax");
            E($"jge {okLo}");
            E("mov %rcx, %rax");
            L(okLo);
            E($"movabs ${hi}, %rcx");
            E("cmp %rcx, %rax");
            E($"jle {okHi}");
            E("mov %rcx, %rax");
            L(okHi);
        }
        L(done);
    }
}
