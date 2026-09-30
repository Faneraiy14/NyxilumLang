using System.Text;
using NyxilumLang.AST;

namespace NyxilumLang.Native;

// NativeCodegenArm64 — Фаза A1 (30.09.2026): справжня компіляція
// NyxilumLang у машинний код ARM64 (AArch64) - процесор телефонів. Той
// самий статичний ELF без libc і без VM, що й x86-бекенд (NativeCodegen),
// з сирими syscall'ами Linux - а Android усередині і є Linux, тож цей
// бінарник запускається й на телефоні (Termux / adb shell).
//
// Підмножина мови - та сама, що в x86-бекенда для --target linux: числа
// (double), bool, рядки, масиви, структури з методами, замикання,
// функції з рекурсією, if/while/break/continue, try/catch/throw, print.
// Аналіз (типи, слоти змінних, вільні змінні) - спільний, NativeFrontend.
//
// Конвенції (не C ABI - з C ми не взаємодіємо, тож обрано найпростішу
// "дзеркальну" x86-бекенду, щоб логіка переносилась один в один):
//   - результат виразу: Number -> d0 (рідний 64-бітний double ARM64),
//     Bool/рядок/масив/структура/замикання -> x0 (64-бітний вказівник);
//   - тимчасові значення - на стеку СЛОТАМИ ПО 16 БАЙТІВ (sp на AArch64
//     мусить лишатись кратним 16 - апаратна вимога);
//   - аргументи функцій - справа наліво на стек по 16 байтів; callee
//     бачить i-й параметр за [x29 + 16 + i*16] (над збереженою парою
//     x29/x30);
//   - локальні змінні - 8-байтові слоти нижче x29 (ті самі зсуви, що
//     рахує CollectVarsAndTypes для x86).
// Syscall'и Linux AArch64: номер у x8, аргументи x0..x2, `svc #0`
// (write=64, exit=93, brk=214).
public class NativeCodegenArm64 : NativeFrontend
{
    protected override int TryFrameBytes => 32; // sp, x29, мітка catch, попередній обробник - по 8 байтів

    public string Compile(ProgramNode program)
    {
        _target = NativeTarget.LinuxArm64;
        var allFuncs = program.Statements.OfType<FunctionDeclaration>().ToList();
        _knownFunctions = allFuncs.Select(f => f.Name).ToHashSet();

        var mainFunc = allFuncs.FirstOrDefault(f => f.Name == "main")
            ?? throw new Exception("native codegen (arm64): у файлі немає func main()");

        var topLevelMethodFuncs = allFuncs.Where(f => f.Name.Contains('.')).ToList();
        foreach (var s in program.Statements.OfType<StructDeclaration>())
        {
            var offsets = new Dictionary<string, int>();
            for (int i = 0; i < s.Fields.Count; i++)
                offsets[s.Fields[i].Name] = i * 8;
            _structFieldOffsets[s.Name] = offsets;

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
                methodMap[m.Name[(m.Name.LastIndexOf('.') + 1)..]] = m;
            }
            _structMethods[s.Name] = methodMap;
        }

        _asm.AppendLine("// Згенеровано NativeCodegenArm64.cs (NyxilumLang, Фаза A1) - НЕ редагувати вручну.");
        _asm.AppendLine(".text");
        _asm.AppendLine(".global _start");

        CompileFunction(mainFunc, isMain: true);
        foreach (var func in allFuncs.Where(f => f.Name != "main" && !f.Name.Contains('.')))
            CompileFunction(func, isMain: false);
        foreach (var s in program.Statements.OfType<StructDeclaration>())
            foreach (var (bareName, m) in _structMethods[s.Name])
                CompileFunction(m, isMain: false, labelOverride: $"{s.Name}__{bareName}");
        while (_pendingLambdas.Count > 0)
        {
            var (label, fnExpr, freeVars) = _pendingLambdas[0];
            _pendingLambdas.RemoveAt(0);
            CompileLambda(label, fnExpr, freeVars);
        }

        EmitHelpers();

        _asm.AppendLine(".section .rodata");
        _asm.AppendLine(".balign 8");
        _asm.AppendLine("print_newline: .byte 10");
        _asm.Append(_rodata);

        _asm.AppendLine(".bss");
        _asm.AppendLine(".balign 8");
        _asm.AppendLine("heap_ptr: .skip 8");   // поточний bump-покажчик heap_alloc (0 = ще не ініціалізовано)
        _asm.AppendLine("heap_end: .skip 8");   // поточна межа (brk)
        _asm.AppendLine("exc_top: .skip 8");    // найближчий активний try-обробник (0 = немає)
        _asm.AppendLine("exc_value: .skip 8");  // значення останнього throw (double)
        _asm.AppendLine("print_buf: .skip 24"); // цифри цілої частини (до 20 для uint64)
        _asm.AppendLine("char_buf: .skip 8");   // скретч-байт для print_char

        return _asm.ToString();
    }

    // ---------------------------------------------------------------- дрібні помічники

    private void Emit(string line) => _asm.AppendLine("    " + line);

    // Операнд пам'яті для слота кадру [x29 + off]. ldur/stur приймають
    // лише зсув -256..255 - далі адресу рахуємо в x16 окремою інструкцією.
    private string Slot(int off)
    {
        if (off >= -256 && off <= 255)
            return $"[x29, #{off}]";
        Emit($"mov x16, #{off}");
        Emit("add x16, x29, x16");
        return "[x16]";
    }

    private void PushD(string reg = "d0") => Emit($"str {reg}, [sp, #-16]!");
    private void PopD(string reg) => Emit($"ldr {reg}, [sp], #16");
    private void PushX(string reg = "x0") => Emit($"str {reg}, [sp, #-16]!");
    private void PopX(string reg) => Emit($"ldr {reg}, [sp], #16");

    private void Adr(string reg, string label)
    {
        Emit($"adrp {reg}, {label}");
        Emit($"add {reg}, {reg}, :lo12:{label}");
    }

    private void LoadDoubleLiteral(double value, string reg = "d0")
    {
        string label = EmitDoubleLiteral(value);
        Emit($"adrp x16, {label}");
        Emit($"ldr {reg}, [x16, :lo12:{label}]");
    }

    private static bool IsPointerType(ValType t) => t is ValType.String or ValType.Array or ValType.Struct or ValType.Closure or ValType.Bool;

    // ---------------------------------------------------------------- функції

    private void ResetFunctionState()
    {
        _varOffsets = new Dictionary<string, int>();
        _varTypes = new Dictionary<string, ValType>();
        _varStructName = new Dictionary<string, string>();
        _ptrElementType = new Dictionary<string, ValType>();
        _tryFrameOffsets = new Dictionary<TryStatement, int>();
        _tryDepth = 0;
        _nextLocalOffset = 0;
    }

    private void EmitPrologue(string label)
    {
        _asm.AppendLine($"{label}:");
        Emit("stp x29, x30, [sp, #-16]!");
        Emit("mov x29, sp");
        int localBytes = (-_nextLocalOffset + 15) / 16 * 16;
        if (localBytes > 0)
        {
            if (localBytes <= 4095)
                Emit($"sub sp, sp, #{localBytes}");
            else
            {
                Emit($"mov x16, #{localBytes}");
                Emit("sub sp, sp, x16");
            }
        }
    }

    private void EmitReturnEpilogue()
    {
        Emit("mov sp, x29");
        Emit("ldp x29, x30, [sp], #16");
        Emit("ret");
    }

    private void CompileFunction(FunctionDeclaration func, bool isMain, string? labelOverride = null)
    {
        ResetFunctionState();
        _isMain = isMain;

        for (int i = 0; i < func.Parameters.Count; i++)
        {
            var param = func.Parameters[i];
            _varOffsets[param.Name] = 16 + i * 16;
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
        EmitPrologue(isMain ? "_start" : entryLabel);

        foreach (var stmt in func.Body.Statements)
            CompileStatement(stmt);

        // "провалились" за кінець без return: main -> код 0, функція -> 0.0
        Emit("mov x0, #0");
        if (!isMain) Emit("fmov d0, xzr");
        _asm.AppendLine($"{_epilogueLabel}:");
        if (isMain)
        {
            Emit("mov x8, #93"); // exit(x0)
            Emit("svc #0");
        }
        else
        {
            EmitReturnEpilogue();
        }
    }

    // Лямбда: неявний env - перший параметр ([x29+16]), явні - далі.
    private void CompileLambda(string label, FunctionExpression fnExpr, List<string> freeVars)
    {
        ResetFunctionState();
        _isMain = false;
        for (int i = 0; i < fnExpr.Parameters.Count; i++)
        {
            _varOffsets[fnExpr.Parameters[i].Name] = 32 + i * 16;
            _varTypes[fnExpr.Parameters[i].Name] = ValType.Number;
        }
        var capturedOffsets = new Dictionary<string, int>();
        foreach (var fv in freeVars)
        {
            _nextLocalOffset -= 8;
            _varOffsets[fv] = _nextLocalOffset;
            _varTypes[fv] = ValType.Number;
            capturedOffsets[fv] = _nextLocalOffset;
        }
        CollectVarsAndTypes(fnExpr.Body);

        _epilogueLabel = $".L{label.TrimStart('.')}_epilogue";
        EmitPrologue(label);
        if (freeVars.Count > 0)
        {
            Emit("ldr x9, [x29, #16]"); // env
            for (int i = 0; i < freeVars.Count; i++)
            {
                Emit($"ldr d0, [x9, #{i * 8}]");
                Emit($"str d0, {Slot(capturedOffsets[freeVars[i]])}");
            }
        }
        foreach (var stmt in fnExpr.Body.Statements)
            CompileStatement(stmt);
        Emit("fmov d0, xzr");
        _asm.AppendLine($"{_epilogueLabel}:");
        EmitReturnEpilogue();
    }

    // ---------------------------------------------------------------- оператори

    private void CompileBlock(BlockStatement block)
    {
        foreach (var stmt in block.Statements)
            CompileStatement(stmt);
    }

    private void StoreToVar(string name)
    {
        int offset = _varOffsets[name];
        if (_varTypes[name] == ValType.Number)
            Emit($"str d0, {Slot(offset)}");
        else
            Emit($"str x0, {Slot(offset)}");
    }

    private void CompileStatement(StatementNode stmt)
    {
        switch (stmt)
        {
            case VariableDeclaration varDecl:
                if (varDecl.Initializer != null)
                {
                    CompileExpression(varDecl.Initializer);
                    StoreToVar(varDecl.Name);
                }
                break;

            case PrintStatement printStmt:
                {
                    var t = InferExprType(printStmt.Expression);
                    CompileExpression(printStmt.Expression);
                    switch (t)
                    {
                        case ValType.Number:
                            Emit("bl print_double");
                            break;
                        case ValType.String:
                            Emit("bl print_string_value");
                            break;
                        case ValType.Bool:
                            {
                                int id = _labelCounter++;
                                Emit($"cbz x0, .Lpfalse{id}");
                                EmitPrintStringLiteral("True\n");
                                Emit($"b .Lpdone{id}");
                                _asm.AppendLine($".Lpfalse{id}:");
                                EmitPrintStringLiteral("False\n");
                                _asm.AppendLine($".Lpdone{id}:");
                                break;
                            }
                        case ValType.Array:
                            throw new Exception("native codegen (arm64): print() масиву напряму ще не підтримується - друкуйте елементи через arr[i] у циклі");
                        case ValType.Struct:
                            throw new Exception("native codegen (arm64): print() структури напряму ще не підтримується - друкуйте поля через obj.field");
                        default:
                            throw new Exception($"native codegen (arm64): print() для {t} не підтримується");
                    }
                    break;
                }

            case ReturnStatement returnStmt:
                {
                    if (_tryDepth > 0)
                        throw new Exception("native codegen (arm64): return усередині try/catch ще не підтримується - винесіть return за межі блоку");
                    if (returnStmt.Value != null)
                    {
                        var t = InferExprType(returnStmt.Value);
                        CompileExpression(returnStmt.Value);
                        if (_isMain)
                        {
                            if (t == ValType.Number)
                                Emit("fcvtzs x0, d0"); // код виходу - ціле
                        }
                        else if (t != ValType.Number)
                        {
                            throw new Exception("native codegen (arm64): звичайні функції можуть повертати лише число - bool поки підтримано тільки для return у main() (код виходу 0/1)");
                        }
                    }
                    else
                    {
                        Emit("mov x0, #0");
                        if (!_isMain) Emit("fmov d0, xzr");
                    }
                    Emit($"b {_epilogueLabel}");
                    break;
                }

            case ExpressionStatement exprStmt:
                CompileExpression(exprStmt.Expression);
                break;

            case IfStatement ifStmt:
                {
                    if (InferExprType(ifStmt.Condition) != ValType.Bool)
                        throw new Exception("native codegen (arm64): умова if має бути булевою (порівняння/&&/||/!)");
                    int id = _labelCounter++;
                    CompileExpression(ifStmt.Condition);
                    if (ifStmt.ElseBlock != null)
                    {
                        Emit($"cbz x0, .Lelse{id}");
                        CompileBlock(ifStmt.ThenBlock);
                        Emit($"b .Lendif{id}");
                        _asm.AppendLine($".Lelse{id}:");
                        CompileBlock(ifStmt.ElseBlock);
                    }
                    else
                    {
                        Emit($"cbz x0, .Lendif{id}");
                        CompileBlock(ifStmt.ThenBlock);
                    }
                    _asm.AppendLine($".Lendif{id}:");
                    break;
                }

            case WhileStatement whileStmt:
                {
                    if (InferExprType(whileStmt.Condition) != ValType.Bool)
                        throw new Exception("native codegen (arm64): умова while має бути булевою (порівняння/&&/||/!)");
                    int id = _labelCounter++;
                    string start = $".Lloopstart{id}";
                    string end = $".Lloopend{id}";
                    _loopLabels.Push((start, end));
                    _asm.AppendLine($"{start}:");
                    CompileExpression(whileStmt.Condition);
                    Emit($"cbz x0, {end}");
                    CompileBlock(whileStmt.Body);
                    Emit($"b {start}");
                    _asm.AppendLine($"{end}:");
                    _loopLabels.Pop();
                    break;
                }

            case BreakStatement:
                if (_loopLabels.Count == 0)
                    throw new Exception("native codegen (arm64): break поза циклом");
                if (_tryDepth > 0)
                    throw new Exception("native codegen (arm64): break усередині try/catch ще не підтримується");
                Emit($"b {_loopLabels.Peek().End}");
                break;

            case ContinueStatement:
                if (_loopLabels.Count == 0)
                    throw new Exception("native codegen (arm64): continue поза циклом");
                if (_tryDepth > 0)
                    throw new Exception("native codegen (arm64): continue усередині try/catch ще не підтримується");
                Emit($"b {_loopLabels.Peek().Start}");
                break;

            case TryStatement ts:
                {
                    // Кадр обробника [sp, x29, catch_label, prev] (по 8 байтів) у
                    // слотах ЦІЄЇ функції; exc_top - найближчий активний. throw
                    // відновлює sp/x29 з кадру й стрибає на catch_label - той
                    // самий setjmp/longjmp-підхід, що x86-бекенд.
                    int id = _labelCounter++;
                    int frame = _tryFrameOffsets[ts];
                    string catchLabel = $".Ltry{id}_catch";
                    string endLabel = $".Ltry{id}_end";

                    Emit("mov x9, sp");
                    Emit($"str x9, {Slot(frame)}");
                    Emit($"str x29, {Slot(frame + 8)}");
                    Adr("x9", catchLabel);
                    Emit($"str x9, {Slot(frame + 16)}");
                    Adr("x10", "exc_top");
                    Emit("ldr x9, [x10]");
                    Emit($"str x9, {Slot(frame + 24)}");     // prev = старий top
                    Emit($"mov x9, #{frame}");
                    Emit("add x9, x29, x9");
                    Emit("str x9, [x10]");                   // активуємо ЦЕЙ обробник

                    _tryDepth++;
                    CompileBlock(ts.TryBlock);
                    _tryDepth--;

                    Emit($"ldr x9, {Slot(frame + 24)}");     // try без throw - деактивуємо
                    Adr("x10", "exc_top");
                    Emit("str x9, [x10]");
                    Emit($"b {endLabel}");

                    _asm.AppendLine($"{catchLabel}:");
                    Emit($"ldr x9, {Slot(frame + 24)}");
                    Adr("x10", "exc_top");
                    Emit("str x9, [x10]");
                    Adr("x10", "exc_value");
                    Emit("ldr d0, [x10]");
                    Emit($"str d0, {Slot(_varOffsets[ts.CatchVariableName])}");

                    _tryDepth++;
                    CompileBlock(ts.CatchBlock);
                    _tryDepth--;
                    _asm.AppendLine($"{endLabel}:");
                    break;
                }

            case ThrowStatement throwStmt:
                {
                    if (InferExprType(throwStmt.Value) != ValType.Number)
                        throw new Exception("native codegen (arm64): throw підтримує лише числові (Number) значення");
                    CompileExpression(throwStmt.Value);
                    Adr("x10", "exc_value");
                    Emit("str d0, [x10]");
                    Adr("x10", "exc_top");
                    Emit("ldr x9, [x10]");
                    Emit("cbz x9, .Lunhandled_throw");
                    Emit("ldr x10, [x9]");       // saved sp
                    Emit("ldr x11, [x9, #8]");   // saved x29
                    Emit("ldr x12, [x9, #16]");  // catch label
                    Emit("mov sp, x10");
                    Emit("mov x29, x11");
                    Emit("br x12");
                    break;
                }

            case BlockStatement block:
                CompileBlock(block);
                break;

            default:
                throw new Exception($"native codegen (arm64): непідтримуваний оператор - {stmt.GetType().Name}");
        }
    }

    // ---------------------------------------------------------------- вирази

    private void CompileExpression(ExpressionNode expr)
    {
        switch (expr)
        {
            case LiteralExpression { Value: double d }:
                if (d == 0 && !double.IsNegative(d))
                    Emit("fmov d0, xzr");
                else
                    LoadDoubleLiteral(d);
                break;

            case LiteralExpression { Value: bool b }:
                Emit($"mov x0, #{(b ? 1 : 0)}");
                break;

            case LiteralExpression { Value: string s }:
                Adr("x0", EmitStringLiteral(s, nullTerminate: true));
                break;

            case ArrayLiteralExpression arrLit:
                {
                    foreach (var el in arrLit.Elements)
                        if (InferExprType(el) != ValType.Number)
                            throw new Exception("native codegen (arm64): елементи масиву підтримуються лише числові (Number)");
                    int count = arrLit.Elements.Count;
                    Emit($"mov x0, #{8 + count * 8}");
                    Emit("bl heap_alloc");
                    Emit($"mov x9, #{count}");
                    Emit("str x9, [x0]");          // довжина - перші 8 байтів
                    PushX();
                    for (int i = 0; i < count; i++)
                    {
                        CompileExpression(arrLit.Elements[i]);
                        Emit("ldr x9, [sp]");      // вказівник - підглядаємо, не знімаючи
                        Emit($"str d0, [x9, #{8 + i * 8}]");
                    }
                    PopX("x0");
                    break;
                }

            case IndexExpression idx:
                {
                    var containerType = InferExprType(idx.Array);
                    if (containerType != ValType.Array && containerType != ValType.String)
                        throw new Exception("native codegen (arm64): індексування [..] підтримується лише для масивів і рядків");
                    if (InferExprType(idx.Index) != ValType.Number)
                        throw new Exception("native codegen (arm64): індекс має бути числом");
                    CompileExpression(idx.Array);
                    PushX();
                    CompileExpression(idx.Index);
                    Emit("fcvtzs x10, d0");
                    PopX("x9");
                    if (containerType == ValType.Array)
                    {
                        Emit("add x9, x9, x10, lsl #3");
                        Emit("ldr d0, [x9, #8]");
                    }
                    else
                    {
                        Emit("ldrb w10, [x9, x10]"); // байт рядка (0-255)
                        Emit("ucvtf d0, w10");
                    }
                    break;
                }

            case StructInitExpression structInit:
                {
                    if (!_structFieldOffsets.TryGetValue(structInit.StructName, out var fieldOffsets))
                        throw new Exception($"native codegen (arm64): невідома структура '{structInit.StructName}'");
                    if (structInit.Fields.Count != fieldOffsets.Count)
                        throw new Exception($"native codegen (arm64): усі поля структури '{structInit.StructName}' мають бути ініціалізовані явно");
                    foreach (var f in structInit.Fields)
                    {
                        if (!fieldOffsets.ContainsKey(f.Name))
                            throw new Exception($"native codegen (arm64): структура '{structInit.StructName}' не має поля '{f.Name}'");
                        if (InferExprType(f.Value) != ValType.Number)
                            throw new Exception("native codegen (arm64): поля структур підтримуються лише числові (Number)");
                    }
                    Emit($"mov x0, #{fieldOffsets.Count * 8}");
                    Emit("bl heap_alloc");
                    PushX();
                    foreach (var f in structInit.Fields)
                    {
                        CompileExpression(f.Value);
                        Emit("ldr x9, [sp]");
                        Emit($"str d0, [x9, #{fieldOffsets[f.Name]}]");
                    }
                    PopX("x0");
                    break;
                }

            case MemberAccessExpression member:
                {
                    string structName = ResolveStructName(member.Object);
                    if (!_structFieldOffsets.TryGetValue(structName, out var fieldOffsets) || !fieldOffsets.TryGetValue(member.Member, out int fieldOffset))
                        throw new Exception($"native codegen (arm64): структура '{structName}' не має поля '{member.Member}'");
                    CompileExpression(member.Object);
                    Emit($"ldr d0, [x0, #{fieldOffset}]");
                    break;
                }

            case MethodCallExpression methodCall:
                {
                    string structName = ResolveStructName(methodCall.Object);
                    if (!_structMethods.TryGetValue(structName, out var methods) || !methods.TryGetValue(methodCall.MethodName, out var methodDecl))
                        throw new Exception($"native codegen (arm64): структура '{structName}' не має методу '{methodCall.MethodName}'");
                    int expectedArgs = methodDecl.Parameters.Count - 1;
                    if (methodCall.Arguments.Count != expectedArgs)
                        throw new Exception($"native codegen (arm64): метод '{structName}.{methodCall.MethodName}' очікує {expectedArgs} аргумент(и/ів), отримано {methodCall.Arguments.Count}");
                    for (int i = methodCall.Arguments.Count - 1; i >= 0; i--)
                    {
                        if (InferExprType(methodCall.Arguments[i]) != ValType.Number)
                            throw new Exception("native codegen (arm64): аргументи методів підтримуються лише числові (Number)");
                        CompileExpression(methodCall.Arguments[i]);
                        PushD();
                    }
                    if (InferExprType(methodCall.Object) != ValType.Struct)
                        throw new Exception("native codegen (arm64): метод можна викликати лише на структурі");
                    CompileExpression(methodCall.Object);
                    PushX();                                     // self - останнім (найближче)
                    Emit($"bl {structName}__{methodCall.MethodName}");
                    Emit($"add sp, sp, #{(methodCall.Arguments.Count + 1) * 16}");
                    break;
                }

            case FunctionExpression fnExpr:
                {
                    var freeVars = FindFreeVars(fnExpr);
                    foreach (var fv in freeVars)
                        if (!_varTypes.TryGetValue(fv, out var fvType) || fvType != ValType.Number)
                            throw new Exception($"native codegen (arm64): замикання можуть захоплювати лише числові (Number) змінні - '{fv}' не підходить");
                    string label = $".Llambda{_lambdaCounter++}";
                    _pendingLambdas.Add((label, fnExpr, freeVars));

                    if (freeVars.Count > 0)
                    {
                        Emit($"mov x0, #{freeVars.Count * 8}");
                        Emit("bl heap_alloc");
                        PushX();                                 // [env]
                        for (int i = 0; i < freeVars.Count; i++)
                        {
                            CompileExpression(new VariableExpression(freeVars[i]));
                            Emit("ldr x9, [sp]");
                            Emit($"str d0, [x9, #{i * 8}]");
                        }
                    }
                    else
                    {
                        PushX("xzr");                            // [env = NULL]
                    }
                    Emit("mov x0, #16");                          // заголовок {code:8, env:8}
                    Emit("bl heap_alloc");
                    PopX("x10");
                    Adr("x9", label);
                    Emit("str x9, [x0]");
                    Emit("str x10, [x0, #8]");
                    break;
                }

            case VariableExpression varExpr:
                {
                    if (!_varOffsets.TryGetValue(varExpr.Name, out int offset))
                        throw new Exception($"native codegen (arm64): змінна '{varExpr.Name}' використана до оголошення");
                    if (_varTypes[varExpr.Name] == ValType.Number)
                        Emit($"ldr d0, {Slot(offset)}");
                    else
                        Emit($"ldr x0, {Slot(offset)}");
                    break;
                }

            case UnaryExpression { Operator: "-" } unaryNeg:
                CompileExpression(unaryNeg.Operand);
                if (InferExprType(unaryNeg.Operand) == ValType.Number)
                    Emit("fneg d0, d0");
                else
                    Emit("neg x0, x0");
                break;

            case UnaryExpression { Operator: "!" } unaryNot:
                CompileExpression(unaryNot.Operand);
                Emit("cmp x0, #0");
                Emit("cset x0, eq");
                break;

            case UnaryExpression { Operator: "~" } unaryBitNot:
                if (InferExprType(unaryBitNot.Operand) != ValType.Number)
                    throw new Exception("native codegen (arm64): '~' підтримується лише для чисел");
                CompileExpression(unaryBitNot.Operand);
                Emit("fcvtzs x9, d0");     // нижні 32 біти - як (int) у x86/VM
                Emit("mvn w9, w9");
                Emit("scvtf d0, w9");
                break;

            case CallExpression call:
                {
                    bool isClosureVar = !_knownFunctions.Contains(call.FunctionName)
                        && _varTypes.TryGetValue(call.FunctionName, out var calleeType)
                        && calleeType == ValType.Closure;
                    if (!_knownFunctions.Contains(call.FunctionName) && !isClosureVar)
                        throw new Exception($"native codegen (arm64): невідома функція '{call.FunctionName}' (лише функції з цього ж файлу чи локальна змінна-замикання)");
                    for (int i = call.Arguments.Count - 1; i >= 0; i--)
                    {
                        if (InferExprType(call.Arguments[i]) != ValType.Number)
                            throw new Exception("native codegen (arm64): аргументи функцій підтримуються лише числові");
                        CompileExpression(call.Arguments[i]);
                        PushD();
                    }
                    if (isClosureVar)
                    {
                        CompileExpression(new VariableExpression(call.FunctionName)); // x0 = заголовок
                        Emit("ldr x9, [x0]");        // code
                        Emit("ldr x0, [x0, #8]");    // env
                        PushX();
                        Emit("blr x9");
                        Emit($"add sp, sp, #{(call.Arguments.Count + 1) * 16}");
                    }
                    else
                    {
                        Emit($"bl {call.FunctionName}");
                        if (call.Arguments.Count > 0)
                            Emit($"add sp, sp, #{call.Arguments.Count * 16}");
                    }
                    break;
                }

            case BinaryExpression { Operator: "=" } assign:
                {
                    if (assign.Left is IndexExpression idxTarget)
                    {
                        var containerType = InferExprType(idxTarget.Array);
                        if (containerType != ValType.Array)
                            throw new Exception("native codegen (arm64): індексоване присвоєння підтримується лише для масивів (рядки - лише для читання)");
                        if (InferExprType(idxTarget.Index) != ValType.Number || InferExprType(assign.Right) != ValType.Number)
                            throw new Exception("native codegen (arm64): індекс і значення елемента масиву мають бути числами");
                        CompileExpression(idxTarget.Array);
                        PushX();
                        CompileExpression(idxTarget.Index);
                        Emit("fcvtzs x10, d0");
                        PushX("x10");
                        CompileExpression(assign.Right);
                        PopX("x10");
                        PopX("x9");
                        Emit("add x9, x9, x10, lsl #3");
                        Emit("str d0, [x9, #8]");
                        break;
                    }
                    if (assign.Left is MemberAccessExpression memberTarget)
                    {
                        string structName = ResolveStructName(memberTarget.Object);
                        if (!_structFieldOffsets.TryGetValue(structName, out var fieldOffsets) || !fieldOffsets.TryGetValue(memberTarget.Member, out int fieldOffset))
                            throw new Exception($"native codegen (arm64): структура '{structName}' не має поля '{memberTarget.Member}'");
                        if (InferExprType(assign.Right) != ValType.Number)
                            throw new Exception("native codegen (arm64): поля структур підтримуються лише числові (Number)");
                        CompileExpression(memberTarget.Object);
                        PushX();
                        CompileExpression(assign.Right);
                        PopX("x9");
                        Emit($"str d0, [x9, #{fieldOffset}]");
                        break;
                    }
                    if (assign.Left is not VariableExpression target)
                        throw new Exception("native codegen (arm64): присвоєння підтримується лише у змінну, arr[i] чи obj.field");
                    if (!_varOffsets.ContainsKey(target.Name))
                        throw new Exception($"native codegen (arm64): змінна '{target.Name}' не оголошена");
                    CompileExpression(assign.Right);
                    StoreToVar(target.Name);
                    break;
                }

            case BinaryExpression { Operator: "&&" } andExpr:
                {
                    int id = _labelCounter++;
                    CompileExpression(andExpr.Left);
                    Emit($"cbz x0, .Lfalse{id}");
                    CompileExpression(andExpr.Right);
                    Emit($"cbz x0, .Lfalse{id}");
                    Emit("mov x0, #1");
                    Emit($"b .Lend{id}");
                    _asm.AppendLine($".Lfalse{id}:");
                    Emit("mov x0, #0");
                    _asm.AppendLine($".Lend{id}:");
                    break;
                }

            case BinaryExpression { Operator: "||" } orExpr:
                {
                    int id = _labelCounter++;
                    CompileExpression(orExpr.Left);
                    Emit($"cbnz x0, .Ltrue{id}");
                    CompileExpression(orExpr.Right);
                    Emit($"cbnz x0, .Ltrue{id}");
                    Emit("mov x0, #0");
                    Emit($"b .Lend{id}");
                    _asm.AppendLine($".Ltrue{id}:");
                    Emit("mov x0, #1");
                    _asm.AppendLine($".Lend{id}:");
                    break;
                }

            case BinaryExpression bin:
                {
                    var leftType = InferExprType(bin.Left);
                    if (leftType == ValType.Number)
                        CompileNumberBinary(bin);
                    else if (leftType == ValType.Bool)
                        CompileBoolBinary(bin);
                    else
                        throw new Exception($"native codegen (arm64): оператор '{bin.Operator}' для {leftType} не підтримується");
                    break;
                }

            default:
                throw new Exception($"native codegen (arm64): непідтримуваний вираз - {expr.GetType().Name}");
        }
    }

    // Число op число: ліве - на стек, праве - у d1 (Bool праворуч -> 0.0/1.0)
    private void CompileNumberBinary(BinaryExpression bin)
    {
        CompileExpression(bin.Left);
        PushD();
        CompileExpression(bin.Right);
        if (InferExprType(bin.Right) != ValType.Number)
            Emit("scvtf d0, x0");
        Emit("fmov d1, d0");
        PopD("d0");
        switch (bin.Operator)
        {
            case "+": Emit("fadd d0, d0, d1"); break;
            case "-": Emit("fsub d0, d0, d1"); break;
            case "*": Emit("fmul d0, d0, d1"); break;
            case "/": Emit("fdiv d0, d0, d1"); break;
            case "%":
                // a - b*trunc(a/b) - та сама "обрізана до нуля" семантика, що x86/VM
                Emit("fdiv d2, d0, d1");
                Emit("frintz d2, d2");
                Emit("fmsub d0, d2, d1, d0");
                break;
            case "==": Emit("fcmp d0, d1"); Emit("cset x0, eq"); break;
            case "!=": Emit("fcmp d0, d1"); Emit("cset x0, ne"); break;
            case "<": Emit("fcmp d0, d1"); Emit("cset x0, mi"); break;
            case "<=": Emit("fcmp d0, d1"); Emit("cset x0, ls"); break;
            case ">": Emit("fcmp d0, d1"); Emit("cset x0, gt"); break;
            case ">=": Emit("fcmp d0, d1"); Emit("cset x0, ge"); break;
            // Побітові - над нижніми 32 бітами, результат знаковий (як x86-бекенд і VM)
            case "&": EmitBitOp("and w9, w9, w10"); break;
            case "|": EmitBitOp("orr w9, w9, w10"); break;
            case "^": EmitBitOp("eor w9, w9, w10"); break;
            case "<<": EmitBitOp("lsl w9, w9, w10"); break;
            case ">>": EmitBitOp("asr w9, w9, w10"); break;
            default:
                throw new Exception($"native codegen (arm64): оператор '{bin.Operator}' для чисел ще не підтримується");
        }
    }

    private void EmitBitOp(string op)
    {
        Emit("fcvtzs x9, d0");
        Emit("fcvtzs x10, d1");
        Emit(op);
        Emit("scvtf d0, w9");
    }

    private void CompileBoolBinary(BinaryExpression bin)
    {
        CompileExpression(bin.Left);
        PushX();
        CompileExpression(bin.Right);
        Emit("mov x1, x0");
        PopX("x0");
        switch (bin.Operator)
        {
            case "==": Emit("cmp x0, x1"); Emit("cset x0, eq"); break;
            case "!=": Emit("cmp x0, x1"); Emit("cset x0, ne"); break;
            case "<": Emit("cmp x0, x1"); Emit("cset x0, lt"); break;
            case "<=": Emit("cmp x0, x1"); Emit("cset x0, le"); break;
            case ">": Emit("cmp x0, x1"); Emit("cset x0, gt"); break;
            case ">=": Emit("cmp x0, x1"); Emit("cset x0, ge"); break;
            case "+": Emit("add x0, x0, x1"); break;
            case "-": Emit("sub x0, x0, x1"); break;
            case "*": Emit("mul x0, x0, x1"); break;
            default:
                throw new Exception($"native codegen (arm64): оператор '{bin.Operator}' для bool ще не підтримується");
        }
    }

    // ---------------------------------------------------------------- рантайм-помічники

    private void EmitPrintStringLiteral(string text)
    {
        string label = EmitStringLiteral(text);
        Emit("mov x0, #1");
        Adr("x1", label);
        Emit($"mov x2, #{Encoding.UTF8.GetByteCount(text)}");
        Emit("mov x8, #64"); // write
        Emit("svc #0");
    }

    private void EmitHelpers()
    {
        // print_char(w0 = байт): чіпає лише x0-x2, x8, x16 - print_double
        // тримає свій стан у x9-x15/d0-d7 і на них покладається.
        _asm.AppendLine("""
            print_char:
                adrp x16, char_buf
                add x16, x16, :lo12:char_buf
                strb w0, [x16]
                mov x0, #1
                mov x1, x16
                mov x2, #1
                mov x8, #64
                svc #0
                ret

            // print_double(d0): знак, ціла частина (uint64), дробова - множенням
            // на 10 до 15 знаків (коротких round-trip записів для 0.1 тощо ще
            // немає - те саме відоме обмеження, що в x86-бекенда).
            print_double:
                stp x29, x30, [sp, #-16]!
                mov x29, sp
                fcmp d0, #0.0
                b.ge .Lpd_nonneg
                mov w0, #'-'
                bl print_char
                fneg d0, d0
            .Lpd_nonneg:
                fcvtzu x9, d0                // x9 = ціла частина
                adrp x10, print_buf
                add x10, x10, :lo12:print_buf
                add x11, x10, #23            // курсор - з кінця буфера
                mov x12, x9
                mov x13, #10
            .Lpd_intloop:
                udiv x14, x12, x13
                msub x15, x14, x13, x12      // остача = цифра
                add x15, x15, #'0'
                sub x11, x11, #1
                strb w15, [x11]
                mov x12, x14
                cbnz x12, .Lpd_intloop
                add x10, x10, #23
            .Lpd_printint:
                cmp x11, x10
                b.hs .Lpd_intdone
                ldrb w0, [x11]
                bl print_char
                add x11, x11, #1
                b .Lpd_printint
            .Lpd_intdone:
                ucvtf d1, x9
                fsub d0, d0, d1              // дробова частина
                fcmp d0, #0.0
                b.eq .Lpd_end
                mov w0, #'.'
                bl print_char
                fmov d3, #10.0
                mov x12, #15
            .Lpd_fracloop:
                fmul d0, d0, d3
                fcvtzs x13, d0
                scvtf d1, x13
                fsub d0, d0, d1
                add w0, w13, #'0'
                bl print_char
                fcmp d0, #0.0
                b.eq .Lpd_end
                subs x12, x12, #1
                b.ne .Lpd_fracloop
            .Lpd_end:
                mov w0, #10
                bl print_char
                ldp x29, x30, [sp], #16
                ret

            // print_string_value(x0 = NUL-термінований UTF-8): strlen + write + '\n'
            print_string_value:
                stp x29, x30, [sp, #-16]!
                mov x29, sp
                mov x1, x0
                mov x2, #0
            .Lpsv_len:
                ldrb w9, [x1, x2]
                cbz w9, .Lpsv_write
                add x2, x2, #1
                b .Lpsv_len
            .Lpsv_write:
                mov x0, #1
                mov x8, #64
                svc #0
                mov w0, #10
                bl print_char
                ldp x29, x30, [sp], #16
                ret

            // heap_alloc(x0 = розмір) -> x0: власний bump-розподілювач через
            // brk (без libc, без free - пам'ять "тече", як і в x86-бекенда).
            heap_alloc:
                add x0, x0, #7
                and x9, x0, #-8               // x9 = розмір, кратний 8
                adrp x10, heap_ptr
                add x10, x10, :lo12:heap_ptr
                ldr x11, [x10]
                cbnz x11, .Lha_inited
                mov x0, #0
                mov x8, #214                  // brk(0) -> поточний break
                svc #0
                str x0, [x10]
                str x0, [x10, #8]             // heap_end
                mov x11, x0
            .Lha_inited:
                add x12, x11, x9              // новий кінець
                ldr x13, [x10, #8]
                cmp x12, x13
                b.ls .Lha_have_space
                mov x14, #65536               // розширюємо з запасом 64 КБ
                add x0, x12, x14
                mov x8, #214
                svc #0
                str x0, [x10, #8]
            .Lha_have_space:
                str x12, [x10]
                mov x0, x11                   // результат - старий bump-покажчик
                ret

            // throw без жодного активного try/catch -> вихід з кодом 1
            .Lunhandled_throw:
                mov x0, #1
                mov x8, #93
                svc #0
            """);
    }

    private string EmitStringLiteral(string value, bool nullTerminate = false)
    {
        string label = $".Lstr{_stringLabelCounter++}";
        var bytes = Encoding.UTF8.GetBytes(value).Select(b => b.ToString()).ToList();
        if (nullTerminate) bytes.Add("0");
        _rodata.AppendLine($"{label}:");
        if (bytes.Count > 0)
            _rodata.AppendLine("    .byte " + string.Join(", ", bytes));
        return label;
    }

    private string EmitDoubleLiteral(double value)
    {
        string label = $".Ldbl{_stringLabelCounter++}";
        _rodata.AppendLine(".balign 8");
        _rodata.AppendLine($"{label}:");
        _rodata.AppendLine($"    .double {value.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)}");
        return label;
    }
}
