namespace NyxilumLang.AST;

public class ExpressionStatement : StatementNode
{
    public ExpressionNode Expression { get; }
    public ExpressionStatement(ExpressionNode expr) => Expression = expr;
    public override string ToString(int indent = 0) => new string(' ', indent) + $"Expression: {Expression.ToString()}";
}

public class VariableDeclaration : StatementNode
{
    public string Name { get; }
    public ExpressionNode? Initializer { get; }
    public string? TypeAnnotation { get; }
    // Фаза N16 (нативний компілятор, самодокументація/майбутня
    // страховка): "var volatile x: uint32;" - НАРАЗІ семантично no-op
    // (жоден шлях кодогену не переупорядковує/кешує читання-запис) -
    // чесно позначено, не приховано (дивись коментар над кодогеном).
    public bool IsVolatile { get; }
    public VariableDeclaration(string name, ExpressionNode? init, string? type = null, bool isVolatile = false)
    { Name = name; Initializer = init; TypeAnnotation = type; IsVolatile = isVolatile; }
    public override string ToString(int indent = 0)
    {
        var pad = new string(' ', indent);
        var typeStr = TypeAnnotation != null ? $": {TypeAnnotation}" : "";
        var volStr = IsVolatile ? " volatile" : "";
        return $"{pad}Var:{volStr} {Name}{typeStr}" + (Initializer != null ? $"\n{Initializer.ToString(indent + 2)}" : "");
    }
}

public class IfStatement : StatementNode
{
    public ExpressionNode Condition { get; }
    public BlockStatement ThenBlock { get; }
    public BlockStatement? ElseBlock { get; }
    public IfStatement(ExpressionNode cond, BlockStatement then, BlockStatement? elseBlock = null)
    { Condition = cond; ThenBlock = then; ElseBlock = elseBlock; }
    public override string ToString(int indent = 0)
    {
        var pad = new string(' ', indent);
        var result = $"{pad}If:\n{Condition.ToString(indent + 2)}\n{ThenBlock.ToString(indent + 2)}";
        if (ElseBlock != null) result += $"\n{pad}Else:\n{ElseBlock.ToString(indent + 2)}";
        return result;
    }
}

public class WhileStatement : StatementNode
{
    public ExpressionNode Condition { get; }
    public BlockStatement Body { get; }
    public WhileStatement(ExpressionNode cond, BlockStatement body)
    { Condition = cond; Body = body; }
    public override string ToString(int indent = 0)
    {
        var pad = new string(' ', indent);
        return $"{pad}While:\n{Condition.ToString(indent + 2)}\n{Body.ToString(indent + 2)}";
    }
}

public class ForStatement : StatementNode
{
    public string VariableName { get; }
    public ExpressionNode Start { get; }
    // null => це не діапазон, а ітерація по елементах масиву (Start - вираз масиву)
    public ExpressionNode? End { get; }
    public BlockStatement Body { get; }
    public ForStatement(string varName, ExpressionNode start, ExpressionNode? end, BlockStatement body)
    { VariableName = varName; Start = start; End = end; Body = body; }
    public override string ToString(int indent = 0)
    {
        var pad = new string(' ', indent);
        var range = End != null ? $"{Start}..{End}" : $"{Start}";
        return $"{pad}For: {VariableName} in {range}\n{Body.ToString(indent + 2)}";
    }
}

public class BlockStatement : StatementNode
{
    public List<StatementNode> Statements { get; } = new();
    public override string ToString(int indent = 0)
    {
        var pad = new string(' ', indent);
        var result = $"{pad}Block:\n";
        foreach (var stmt in Statements)
            result += $"{stmt.ToString(indent + 2)}\n";
        return result;
    }
}

public class ReturnStatement : StatementNode
{
    public ExpressionNode? Value { get; }
    public ReturnStatement(ExpressionNode? value) => Value = value;
    public override string ToString(int indent = 0)
    {
        var pad = new string(' ', indent);
        return $"{pad}Return: {(Value != null ? Value.ToString() : "void")}";
    }
}

public class PrintStatement : StatementNode
{
    public ExpressionNode Expression { get; }
    public PrintStatement(ExpressionNode expr) => Expression = expr;
    public override string ToString(int indent = 0)
    {
        var pad = new string(' ', indent);
        return $"{pad}Print: {Expression.ToString()}";
    }
}

public class FunctionDeclaration : StatementNode
{
    public string Name { get; }
    public List<FunctionParameter> Parameters { get; } = new();
    public BlockStatement Body { get; }
    public string? ReturnType { get; }
    // Фаза N15 (нативний компілятор, --target nyxos-kernel лише) -
    // "func f() naked { ... }": компілятор НЕ генерує стандартний
    // пролог/епілог (push %ebp/.../pop %ebp;ret) - тіло ПОВНІСТЮ на
    // відповідальності програміста, у парі з asm() (Фаза N11).
    public bool IsNaked { get; }
    public FunctionDeclaration(string name, List<FunctionParameter> parameters, BlockStatement body, string? returnType = null, bool isNaked = false)
    { Name = name; Parameters = parameters; Body = body; ReturnType = returnType; IsNaked = isNaked; }
    public override string ToString(int indent = 0)
    {
        var pad = new string(' ', indent);
        var paramsStr = string.Join(", ", Parameters.Select(p => $"{p.Name}: {p.Type}"));
        var returnStr = ReturnType != null ? $" -> {ReturnType}" : "";
        var result = $"{pad}Func: {Name}{returnStr}\n";
        result += $"{pad}  Params: {paramsStr}\n";
        result += Body.ToString(indent + 2);
        return result;
    }
}

public class FunctionParameter
{
    public string Name { get; }
    public string Type { get; }
    public FunctionParameter(string name, string type) { Name = name; Type = type; }
}

public class StructDeclaration : StatementNode
{
    public string Name { get; }
    public string? ParentName { get; }
    public List<StructField> Fields { get; }
    public List<FunctionDeclaration> Methods { get; }
    public StructDeclaration(string name, List<StructField> fields, List<FunctionDeclaration> methods, string? parentName = null)
    { Name = name; Fields = fields; Methods = methods; ParentName = parentName; }
    public override string ToString(int indent = 0)
    {
        var pad = new string(' ', indent);
        var extendsStr = ParentName != null ? $" extends {ParentName}" : "";
        var result = $"{pad}Struct: {Name}{extendsStr}\n";
        foreach (var field in Fields)
            result += $"{pad}  {field.Name}: {field.Type}\n";
        foreach (var method in Methods)
            result += method.ToString(indent + 2) + "\n";
        return result;
    }
}

public class StructField
{
    public string Name { get; }
    public string Type { get; }
    public StructField(string name, string type) { Name = name; Type = type; }
}

public class TryStatement : StatementNode
{
    public BlockStatement TryBlock { get; }
    public string CatchVariableName { get; }
    public BlockStatement CatchBlock { get; }
    public TryStatement(BlockStatement tryBlock, string catchVariableName, BlockStatement catchBlock)
    { TryBlock = tryBlock; CatchVariableName = catchVariableName; CatchBlock = catchBlock; }
    public override string ToString(int indent = 0)
    {
        var pad = new string(' ', indent);
        return $"{pad}Try:\n{TryBlock.ToString(indent + 2)}\n{pad}Catch({CatchVariableName}):\n{CatchBlock.ToString(indent + 2)}";
    }
}

public class ImportStatement : StatementNode
{
    public string Path { get; }
    // null - імпортувати все (як раніше); непорожній список - лише названі
    // оголошення (вибірковий import "file.nx" { a, b }).
    public List<string>? Names { get; }
    public ImportStatement(string path, List<string>? names = null) { Path = path; Names = names; }
    public override string ToString(int indent = 0)
    {
        var namesStr = Names != null ? " { " + string.Join(", ", Names) + " }" : "";
        return new string(' ', indent) + $"Import: {Path}{namesStr}";
    }
}

// break / continue — виходять з найближчого циклу або переходять до його
// наступної ітерації. Значень не мають, тому вузли порожні: уся робота
// відбувається в компіляторі, який знає адреси поточного циклу.
public class BreakStatement : StatementNode
{
    public override string ToString(int indent = 0) => new string(' ', indent) + "Break";
}

public class ContinueStatement : StatementNode
{
    public override string ToString(int indent = 0) => new string(' ', indent) + "Continue";
}

public class ThrowStatement : StatementNode
{
    public ExpressionNode Value { get; }
    public ThrowStatement(ExpressionNode value) => Value = value;
    public override string ToString(int indent = 0)
    {
        var pad = new string(' ', indent);
        return $"{pad}Throw: {Value}";
    }
}