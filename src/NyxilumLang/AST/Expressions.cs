namespace NyxilumLang.AST;

public class LiteralExpression : ExpressionNode
{
    public object Value { get; }
    // Текст числа з коду, як є (системний бекенд x86-64 читає int64/uint64 без втрати точності double)
    public string? Raw { get; set; }
    public LiteralExpression(object value) => Value = value;
    public override string ToString(int indent = 0) => new string(' ', indent) + $"Literal: {Value}";
}

public class VariableExpression : ExpressionNode
{
    public string Name { get; }
    public VariableExpression(string name) => Name = name;
    public override string ToString(int indent = 0) => new string(' ', indent) + $"Variable: {Name}";
}

public class BinaryExpression : ExpressionNode
{
    public ExpressionNode Left { get; }
    public string Operator { get; }
    public ExpressionNode Right { get; }
    public BinaryExpression(ExpressionNode left, string op, ExpressionNode right)
    { Left = left; Operator = op; Right = right; }
    public override string ToString(int indent = 0)
    {
        var pad = new string(' ', indent);
        return $"{pad}Binary: {Operator}\n{Left.ToString(indent + 2)}\n{Right.ToString(indent + 2)}";
    }
}

public class UnaryExpression : ExpressionNode
{
    public string Operator { get; }
    public ExpressionNode Operand { get; }
    public UnaryExpression(string op, ExpressionNode operand) { Operator = op; Operand = operand; }
    public override string ToString(int indent = 0)
    {
        var pad = new string(' ', indent);
        return $"{pad}Unary: {Operator}\n{Operand.ToString(indent + 2)}";
    }
}

public class CallExpression : ExpressionNode
{
    public string FunctionName { get; }
    public List<ExpressionNode> Arguments { get; } = new();
    public CallExpression(string name) => FunctionName = name;
    public override string ToString(int indent = 0)
    {
        var pad = new string(' ', indent);
        var result = $"{pad}Call: {FunctionName}\n";
        foreach (var arg in Arguments)
            result += $"{arg.ToString(indent + 2)}\n";
        return result;
    }
}

public class ArrayLiteralExpression : ExpressionNode
{
    public List<ExpressionNode> Elements { get; } = new();
    public override string ToString(int indent = 0)
    {
        var pad = new string(' ', indent);
        var result = $"{pad}Array: [\n";
        foreach (var elem in Elements)
            result += $"{elem.ToString(indent + 2)},\n";
        result += $"{pad}]";
        return result;
    }
}

public class IndexExpression : ExpressionNode
{
    public ExpressionNode Array { get; }
    public ExpressionNode Index { get; }
    public IndexExpression(ExpressionNode array, ExpressionNode index)
    { Array = array; Index = index; }
    public override string ToString(int indent = 0)
    {
        var pad = new string(' ', indent);
        return $"{pad}Index: {Array.ToString()} [{Index.ToString()}]";
    }
}

public class MemberAccessExpression : ExpressionNode
{
    public ExpressionNode Object { get; }
    public string Member { get; }

    public MemberAccessExpression(ExpressionNode obj, string member)
    {
        Object = obj;
        Member = member;
    }

    public override string ToString(int indent = 0)
    {
        var pad = new string(' ', indent);
        return $"{pad}Member: {Object.ToString()} -> {Member}";
    }
}

public class StructInitExpression : ExpressionNode
{
    public string StructName { get; }
    public List<StructFieldInit> Fields { get; } = new();

    public StructInitExpression(string structName)
    {
        StructName = structName;
    }

    public override string ToString(int indent = 0)
    {
        var pad = new string(' ', indent);
        var result = $"{pad}StructInit: {StructName}\n";
        foreach (var field in Fields)
            result += $"{pad}  {field.Name}: {field.Value.ToString(indent + 4)}\n";
        return result;
    }
}

public class StructFieldInit
{
    public string Name { get; }
    public ExpressionNode Value { get; }

    public StructFieldInit(string name, ExpressionNode value)
    {
        Name = name;
        Value = value;
    }
}

public class FunctionExpression : ExpressionNode
{
    public List<FunctionParameter> Parameters { get; }
    public BlockStatement Body { get; }
    public FunctionExpression(List<FunctionParameter> parameters, BlockStatement body)
    { Parameters = parameters; Body = body; }
    public override string ToString(int indent = 0) => new string(' ', indent) + "FunctionExpr";
}

// Виклик значення, яке САМЕ Є виразом, а не просто ім'ям змінної — тобто
// f()() чи arr[0](). CallExpression викликає ІМ'Я функції; тут callee -
// довільний вираз, що вже обчислився в значення-функцію (замикання,
// посилання на функцію), і його треба ще й ВИКЛИКАТИ через CALL_VALUE.
public class CallValueExpression : ExpressionNode
{
    public ExpressionNode Callee { get; }
    public List<ExpressionNode> Arguments { get; } = new();

    public CallValueExpression(ExpressionNode callee, List<ExpressionNode> args)
    {
        Callee = callee;
        Arguments = args;
    }

    public override string ToString(int indent = 0)
    {
        var pad = new string(' ', indent);
        var result = $"{pad}CallValue: {Callee}\n";
        foreach (var arg in Arguments)
            result += $"{arg.ToString(indent + 2)}\n";
        return result;
    }
}

public class MethodCallExpression : ExpressionNode
{
    public ExpressionNode Object { get; }
    public string MethodName { get; }
    public List<ExpressionNode> Arguments { get; } = new();

    public MethodCallExpression(ExpressionNode obj, string methodName, List<ExpressionNode> args)
    {
        Object = obj;
        MethodName = methodName;
        Arguments = args;
    }

    public override string ToString(int indent = 0)
    {
        var pad = new string(' ', indent);
        var result = $"{pad}MethodCall: {Object.ToString()} -> {MethodName}\n";
        foreach (var arg in Arguments)
            result += $"{arg.ToString(indent + 2)}\n";
        return result;
    }
}