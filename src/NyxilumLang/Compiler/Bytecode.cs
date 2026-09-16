namespace NyxilumLang.Compiler;

public enum OpCode : byte
{
    LOAD_CONST, LOAD_VAR, STORE_VAR,
    ADD, SUB, MUL, DIV, MOD,
    EQ, NEQ, LT, LTE, GT, GTE,
    AND, OR, NOT,
    /* Побітові (Фаза N8, 16.09.2026) - числа у VM все одно boxed
     * double, тож BIT_AND/OR/XOR/SHL/SHR конвертують через (long) на
     * вході й назад на виході (див. VirtualMachine.cs) - точнісінько
     * той самий трюк, що вже робить native-компілятор для % (mod).
     * BIT_NOT - унарний, як NOT вище. */
    BIT_AND, BIT_OR, BIT_XOR, SHL, SHR, BIT_NOT,
    JUMP, JUMP_IF_FALSE, CALL, RETURN,
    PRINT,
    READ_LINE, READ_INT, READ_DOUBLE,
    READ_FILE, WRITE_FILE, APPEND_FILE, FILE_EXISTS,
    SQRT, ABS, POW, SIN, COS, TAN,
    ROUND, FLOOR, CEIL, MAX, MIN,
    TO_STRING, TO_INT, TO_DOUBLE, LEN,
    ARRAY_NEW, ARRAY_GET, ARRAY_SET,
    STRUCT_NEW, STRUCT_GET, STRUCT_SET,
    CALL_NATIVE, CALL_METHOD,
    TRY_BEGIN, TRY_END, THROW,
    MAKE_CLOSURE, CALL_VALUE,
    GET_GLOBAL, SET_GLOBAL,
    POP,
    HALT
}

public class Bytecode
{
    public List<byte> Code { get; } = new();
    public List<object> Constants { get; } = new();
    public Dictionary<string, int> FunctionAddresses { get; } = new();

    public void Emit(OpCode op, int? arg = null)
    {
        Code.Add((byte)op);
        if (arg.HasValue)
        {
            Code.Add((byte)(arg.Value & 0xFF));
            Code.Add((byte)((arg.Value >> 8) & 0xFF));
        }
    }

    public void Emit(OpCode op, int arg1, int arg2)
    {
        Code.Add((byte)op);
        Code.Add((byte)(arg1 & 0xFF));
        Code.Add((byte)((arg1 >> 8) & 0xFF));
        Code.Add((byte)(arg2 & 0xFF));
        Code.Add((byte)((arg2 >> 8) & 0xFF));
    }

    public int AddConstant(object value)
    {
        Constants.Add(value);
        return Constants.Count - 1;
    }

    // Розріджена мапа "з якого байтового зсуву починається який рядок
    // джерела" — не по інструкції (це роздуло б байткод), а лише в
    // точках, де Compiler.cs переходить до компіляції нового statement.
    // Відсортована за зростанням Offset за побудовою (компілюємо
    // послідовно), тож VirtualMachine шукає останній запис з Offset <= IP.
    public List<(int Offset, int Line)> LineMap { get; } = new();

    public void MarkLine(int line)
    {
        // Кілька виразів в одному рядку джерела не повинні плодити
        // дублікати підряд — лише РЕАЛЬНА зміна рядка вартує запису.
        if (LineMap.Count > 0 && LineMap[^1].Line == line) return;
        LineMap.Add((Code.Count, line));
    }

    public byte[] ToArray() => Code.ToArray();
}