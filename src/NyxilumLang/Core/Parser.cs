using NyxilumLang.AST;
using System.Globalization;

namespace NyxilumLang.Core;

public class Parser
{
    private readonly List<Token> _tokens;
    private int _pos;

    // Неоднозначність struct-літералів (Ідентифікатор { ... }) проти блоку
    // умовного оператора: "while i < N { ... }" — після "N" наступний "{"
    // МІГ БИ бути початком структурного літерала (детекція за великою
    // літерою в ParsePrimary), але тут це якраз відкриття тіла циклу.
    // _noStructLiteral=true, поки парситься умова if/while/for (де перед
    // "{" завжди має йти саме тіло, а не літерал), і скидається назад на
    // false усередині будь-яких дужок/квадратних дужок — там межу з "{"
    // тіла однозначно визначає закривна дужка, тож усередині них
    // структурні літерали лишаються дозволеними як завжди.
    private bool _noStructLiteral;

    public Parser(List<Token> tokens) { _tokens = tokens; _pos = 0; }

    private Token Peek() => _pos < _tokens.Count ? _tokens[_pos] : _tokens.Last();
    private Token Advance() => _pos < _tokens.Count ? _tokens[_pos++] : _tokens.Last();
    private bool IsAtEnd() => Peek().Type == TokenType.EOF;
    private Token Consume(TokenType type, string msg) { if (Peek().Type == type) return Advance(); throw new Exception($"{msg} на рядку {Peek().Line}, стовпець {Peek().Column}"); }

    public ProgramNode ParseProgram()
    {
        var program = new ProgramNode();
        while (!IsAtEnd())
        {
            if (Peek().Type == TokenType.EOF) break;
            var stmt = ParseStatement();
            if (stmt != null) program.Statements.Add(stmt);
        }
        return program;
    }

    private StatementNode? ParseStatement()
    {
        var token = Peek();

        // ';' - опційний роздільник операторів (як у C/JS/PHP), зручний
        // для кількох statements на одному рядку. Лексер завжди визнавав
        // ';' як Punctuation (див. Lexer.cs), але жодне правило парсера
        // його не споживало: токен долітав аж до ParsePrimary() як
        // "початок виразу", де для нього немає жодного правила, і падав з
        // непов'язаною помилкою "Невідомий вираз" замість того, щоб
        // просто нічого не робити. ParseProgram()/ParseBlockStatement()
        // вже толерантні до null (просто не додають), тому no-op тут
        // безпечний і для кількох ';' підряд, і для зайвого ';' в кінці.
        if (token.Type == TokenType.Punctuation && token.Value == ";")
        {
            Advance();
            return null;
        }

        var stmt = token.Type switch
        {
            TokenType.Keyword when token.Value == "func" => ParseFunctionDeclaration(),
            TokenType.Keyword when token.Value == "var" => ParseVariableDeclaration(),
            TokenType.Keyword when token.Value == "if" => ParseIfStatement(),
            TokenType.Keyword when token.Value == "while" => ParseWhileStatement(),
            TokenType.Keyword when token.Value == "for" => ParseForStatement(),
            TokenType.Keyword when token.Value == "return" => ParseReturnStatement(),
            TokenType.Keyword when token.Value == "print" => ParsePrintStatement(),
            TokenType.Keyword when token.Value == "struct" => ParseStructDeclaration(),
            TokenType.Keyword when token.Value == "try" => ParseTryStatement(),
            TokenType.Keyword when token.Value == "throw" => ParseThrowStatement(),
            TokenType.Keyword when token.Value == "import" => ParseImportStatement(),
            TokenType.Keyword when token.Value == "break" => ParseBreakStatement(),
            TokenType.Keyword when token.Value == "continue" => ParseContinueStatement(),
            TokenType.Punctuation when token.Value == "{" => ParseBlockStatement(),
            _ => ParseExpressionStatement()
        };
        // Один центральний штамп рядка на весь statement (не кожен вираз
        // усередині) — досить для "у якому рядку впала помилка виконання",
        // а не намагання відстежити кожну під-виразну позицію.
        if (stmt != null) stmt.Line = token.Line;
        return stmt;
    }

    private BreakStatement ParseBreakStatement()
    {
        Advance(); // 'break'
        return new BreakStatement();
    }

    private ContinueStatement ParseContinueStatement()
    {
        Advance(); // 'continue'
        return new ContinueStatement();
    }

    private TryStatement ParseTryStatement()
    {
        Advance(); // 'try'
        var tryBlock = ParseBlockStatement();

        if (!(Peek().Type == TokenType.Keyword && Peek().Value == "catch"))
            throw new Exception($"Очікується 'catch' на рядку {Peek().Line}, стовпець {Peek().Column}");
        Advance(); // 'catch'

        Consume(TokenType.Punctuation, "Очікується '(' після catch");
        var catchVar = Consume(TokenType.Identifier, "Очікується назва змінної помилки");
        Consume(TokenType.Punctuation, "Очікується ')'");

        var catchBlock = ParseBlockStatement();
        return new TryStatement(tryBlock, catchVar.Value, catchBlock);
    }

    private ThrowStatement ParseThrowStatement()
    {
        Advance(); // 'throw'
        var value = ParseExpression();
        return new ThrowStatement(value);
    }

    private ImportStatement ParseImportStatement()
    {
        Advance(); // 'import'
        var pathToken = Consume(TokenType.String, "Очікується шлях до файлу (рядок) після import");

        List<string>? names = null;
        if (Peek().Type == TokenType.Punctuation && Peek().Value == "{")
        {
            Advance(); // '{'
            names = new List<string>();
            while (Peek().Type != TokenType.Punctuation || Peek().Value != "}")
            {
                var nameToken = Consume(TokenType.Identifier, "Очікується назва в списку вибіркового import");
                names.Add(nameToken.Value);
                if (Peek().Type == TokenType.Punctuation && Peek().Value == ",")
                    Advance();
            }
            Consume(TokenType.Punctuation, "Очікується '}' після списку вибіркового import");
        }

        return new ImportStatement(pathToken.Value, names);
    }

    private StructDeclaration ParseStructDeclaration()
    {
        Advance();
        var name = Consume(TokenType.Identifier, "Очікується назва структури");

        string? parentName = null;
        if (Peek().Type == TokenType.Keyword && Peek().Value == "extends")
        {
            Advance();
            var parent = Consume(TokenType.Identifier, "Очікується назва батьківської структури після 'extends'");
            parentName = parent.Value;
        }

        Consume(TokenType.Punctuation, "Очікується '{'");
        var fields = new List<StructField>();
        var methods = new List<FunctionDeclaration>();
        
        while (Peek().Type != TokenType.Punctuation || Peek().Value != "}")
        {
            if (Peek().Type == TokenType.Keyword && Peek().Value == "func")
            {
                var method = ParseFunctionDeclaration(name.Value);
                methods.Add(method);
            }
            else
            {
                var fieldName = Consume(TokenType.Identifier, "Очікується назва поля");
                if (Peek().Type == TokenType.Punctuation && Peek().Value == ":")
                {
                    Advance();
                }
                else
                {
                    throw new Exception($"Очікується ':' після назви поля на рядку {Peek().Line}, стовпець {Peek().Column}");
                }
                var fieldType = ParseType();
                fields.Add(new StructField(fieldName.Value, fieldType));
            }
        }
        Consume(TokenType.Punctuation, "Очікується '}'");
        return new StructDeclaration(name.Value, fields, methods, parentName);
    }

    private FunctionDeclaration ParseFunctionDeclaration(string? parentStructName = null)
    {
        Advance();
        
        var name = Consume(TokenType.Identifier, "Очікується назва функції");
        
        string? structName = parentStructName;
        if (Peek().Type == TokenType.Punctuation && Peek().Value == ".")
        {
            Advance();
            structName = name.Value;
            name = Consume(TokenType.Identifier, "Очікується назва методу");
        }
        
        Consume(TokenType.Punctuation, "Очікується '('");
        
        var parameters = new List<FunctionParameter>();
        
        if (structName != null)
        {
            parameters.Add(new FunctionParameter("self", structName));
        }
        
        if (Peek().Type != TokenType.Punctuation || Peek().Value != ")")
        {
            do
            {
                if (Peek().Type == TokenType.Punctuation && Peek().Value == ",")
                {
                    Advance();
                    continue;
                }
                
                var pName = Consume(TokenType.Identifier, "Очікується назва параметра");
                string pType = "any";

                if (Peek().Type == TokenType.Punctuation && Peek().Value == ":")
                {
                    Advance();
                    pType = ParseType();
                }

                parameters.Add(new FunctionParameter(pName.Value, pType));
                
                if (Peek().Type == TokenType.Punctuation && Peek().Value == ",")
                {
                    Advance();
                }
                
            } while (Peek().Type != TokenType.Punctuation || Peek().Value != ")");
        }
        Consume(TokenType.Punctuation, "Очікується ')'");

        string? returnType = null;
        if (Peek().Type == TokenType.Operator && Peek().Value == "->")
        {
            Advance();
            returnType = ParseType();
        }
        
        var body = ParseBlockStatement();
        string fullName = structName != null ? $"{structName}.{name.Value}" : name.Value;
        return new FunctionDeclaration(fullName, parameters, body, returnType);
    }

    private string ParseType()
    {
        if (Peek().Type == TokenType.Punctuation && Peek().Value == "[")
        {
            Advance();
            var elementType = "any";
            if (Peek().Type != TokenType.Punctuation || Peek().Value != "]")
            {
                elementType = ParseType();
            }
            Consume(TokenType.Punctuation, "Очікується ']'");
            return $"[{elementType}]";
        }
        else if (Peek().Type == TokenType.Keyword || Peek().Type == TokenType.Identifier)
        {
            var type = Peek().Value;
            Advance();
            return type;
        }
        else
        {
            throw new Exception($"Очікується тип на рядку {Peek().Line}, стовпець {Peek().Column}");
        }
    }

    private VariableDeclaration ParseVariableDeclaration()
    {
        Advance();
        var name = Consume(TokenType.Identifier, "Очікується назва змінної");
        
        string? type = null;
        if (Peek().Type == TokenType.Punctuation && Peek().Value == ":")
        {
            Advance();
            type = ParseType();
        }
        
        ExpressionNode? init = null;
        if (Peek().Type == TokenType.Operator && Peek().Value == "=")
        {
            Advance();
            init = ParseExpression();
        }
        
        return new VariableDeclaration(name.Value, init, type);
    }

    private IfStatement ParseIfStatement()
    {
        Advance();
        // Дужки навколо умови не обробляються окремо: ParseExpression сам
        // розуміє їх як групування. Спецобробка ламала умови, що ПОЧИНАЮТЬСЯ
        // з дужки, але нею не вичерпуються: if (a) || (b) { } — вона з'їдала
        // "(a)" і одразу вимагала "{", натикаючись на "||".
        var cond = ParseConditionExpression();

        var thenBlock = ParseBlockStatement();
        BlockStatement? elseBlock = null;
        if (Peek().Type == TokenType.Keyword && Peek().Value == "else")
        {
            Advance();
            if (Peek().Type == TokenType.Keyword && Peek().Value == "if")
            {
                var nestedIf = ParseIfStatement();
                elseBlock = new BlockStatement();
                elseBlock.Statements.Add(nestedIf);
            }
            else
            {
                elseBlock = ParseBlockStatement();
            }
        }
        return new IfStatement(cond, thenBlock, elseBlock);
    }

    private WhileStatement ParseWhileStatement()
    {
        Advance();
        // Те саме, що й у if: дужки — звичайне групування у виразі.
        var cond = ParseConditionExpression();

        var body = ParseBlockStatement();
        return new WhileStatement(cond, body);
    }

    // Парсить вираз умови if/while/for з тимчасово вимкненою детекцією
    // struct-літералів на верхньому рівні (див. коментар біля
    // _noStructLiteral) — так "while i < N { ... }" не намагається
    // прочитати "N { ... }" як побудову структури N.
    private ExpressionNode ParseConditionExpression()
    {
        var outer = _noStructLiteral;
        _noStructLiteral = true;
        try
        {
            return ParseExpression();
        }
        finally
        {
            _noStructLiteral = outer;
        }
    }

    private ForStatement ParseForStatement()
    {
        Advance();
        var varName = Consume(TokenType.Identifier, "Очікується назва змінної");
        if (Peek().Type == TokenType.Keyword && Peek().Value == "in") Advance();
        else throw new Exception($"Очікується 'in' на рядку {Peek().Line}, стовпець {Peek().Column}");

        var outer = _noStructLiteral;
        _noStructLiteral = true;
        ExpressionNode start, end;
        bool isRange;
        try
        {
            start = ParseAddition();
            isRange = Peek().Type == TokenType.Operator && Peek().Value == "..";
            if (isRange)
            {
                Advance();
                end = ParseAddition();
            }
            else
            {
                end = null!;
            }
        }
        finally
        {
            _noStructLiteral = outer;
        }

        if (isRange)
        {
            var rangeBody = ParseBlockStatement();
            return new ForStatement(varName.Value, start, end, rangeBody);
        }
        // Немає '..' - це ітерація по елементах масиву: for x in arrExpr { ... }
        var arrBody = ParseBlockStatement();
        return new ForStatement(varName.Value, start, null, arrBody);
    }

    private PrintStatement ParsePrintStatement()
    {
        Advance();
        Consume(TokenType.Punctuation, "Очікується '('");
        var expr = ParseExpression();
        Consume(TokenType.Punctuation, "Очікується ')'");
        return new PrintStatement(expr);
    }

    private ReturnStatement ParseReturnStatement()
    {
        Advance();
        ExpressionNode? value = null;
        if (Peek().Type != TokenType.Punctuation || Peek().Value != "}") value = ParseExpression();
        return new ReturnStatement(value);
    }

    private BlockStatement ParseBlockStatement()
    {
        Consume(TokenType.Punctuation, "Очікується '{'");
        var block = new BlockStatement();
        while (!IsAtEnd() && !(Peek().Type == TokenType.Punctuation && Peek().Value == "}"))
        {
            var stmt = ParseStatement();
            if (stmt != null) block.Statements.Add(stmt);
        }
        Consume(TokenType.Punctuation, "Очікується '}'");
        return block;
    }

    private StatementNode ParseExpressionStatement()
    {
        var expr = ParseExpression();
        return new ExpressionStatement(expr);
    }

    private ExpressionNode ParseExpression() => ParseAssignment();

    private ExpressionNode ParseAssignment()
    {
        var left = ParseOr();
        var tok = Peek();
        if (tok.Type == TokenType.Operator && tok.Value == "=")
        {
            Advance();
            var right = ParseAssignment();
            return new BinaryExpression(left, "=", right);
        }
        // Compound assignment: x += y  -->  x = x + y
        if (tok.Type == TokenType.Operator && (tok.Value == "+=" || tok.Value == "-=" || tok.Value == "*=" || tok.Value == "/="))
        {
            string baseOp = tok.Value[0].ToString(); // '+', '-', '*', '/'
            Advance();
            var right = ParseAssignment();
            var expanded = new BinaryExpression(left, baseOp, right);
            return new BinaryExpression(left, "=", expanded);
        }
        // Postfix ++ / --  -->  x = x + 1  (returns new value, expression statement)
        if (tok.Type == TokenType.Operator && (tok.Value == "++" || tok.Value == "--"))
        {
            string baseOp = tok.Value == "++" ? "+" : "-";
            Advance();
            var one = new LiteralExpression(1.0);
            var expanded = new BinaryExpression(left, baseOp, one);
            return new BinaryExpression(left, "=", expanded);
        }
        return left;
    }

    private ExpressionNode ParseOr() { var left = ParseAnd(); while (Peek().Type == TokenType.Operator && Peek().Value == "||") { Advance(); var right = ParseAnd(); left = new BinaryExpression(left, "||", right); } return left; }
    private ExpressionNode ParseAnd() { var left = ParseEquality(); while (Peek().Type == TokenType.Operator && Peek().Value == "&&") { Advance(); var right = ParseEquality(); left = new BinaryExpression(left, "&&", right); } return left; }
    private ExpressionNode ParseEquality() { var left = ParseComparison(); while (Peek().Type == TokenType.Operator && (Peek().Value == "==" || Peek().Value == "!=")) { string op = Advance().Value; var right = ParseComparison(); left = new BinaryExpression(left, op, right); } return left; }
    private ExpressionNode ParseComparison() { var left = ParseAddition(); while (Peek().Type == TokenType.Operator && (Peek().Value == "<" || Peek().Value == "<=" || Peek().Value == ">" || Peek().Value == ">=")) { string op = Advance().Value; var right = ParseAddition(); left = new BinaryExpression(left, op, right); } return left; }
    private ExpressionNode ParseAddition() { var left = ParseMultiplication(); while (Peek().Type == TokenType.Operator && (Peek().Value == "+" || Peek().Value == "-")) { string op = Advance().Value; var right = ParseMultiplication(); left = new BinaryExpression(left, op, right); } return left; }
    private ExpressionNode ParseMultiplication() { var left = ParseUnary(); while (Peek().Type == TokenType.Operator && (Peek().Value == "*" || Peek().Value == "/" || Peek().Value == "%")) { string op = Advance().Value; var right = ParseUnary(); left = new BinaryExpression(left, op, right); } return left; }
    private ExpressionNode ParseUnary() { if (Peek().Type == TokenType.Operator && (Peek().Value == "!" || Peek().Value == "-")) { string op = Advance().Value; var right = ParseUnary(); return new UnaryExpression(op, right); } return ParsePrimary(); }

    private ExpressionNode ParsePrimary()
    {
        var token = Peek();
        
        if (token.Type == TokenType.Number) { Advance(); return new LiteralExpression(double.Parse(token.Value, CultureInfo.InvariantCulture)); }
        if (token.Type == TokenType.String) { Advance(); return new LiteralExpression(token.Value); }
        if (token.Type == TokenType.Boolean) { Advance(); return new LiteralExpression(bool.Parse(token.Value)); }
        if (token.Type == TokenType.Keyword && token.Value == "null") { Advance(); return new LiteralExpression(null!); }
        if (token.Type == TokenType.Punctuation && token.Value == "[")
        {
            Advance();
            var array = new ArrayLiteralExpression();
            // Усередині "[...]" межу з "{" тіла умови вже однозначно визначає
            // "]" — struct-літерали як елементи масиву знову дозволені,
            // навіть якщо весь масив стоїть у виразі умови if/while/for.
            var outerNoStruct = _noStructLiteral;
            _noStructLiteral = false;
            try
            {
                while (Peek().Type != TokenType.Punctuation || Peek().Value != "]")
                {
                    array.Elements.Add(ParseExpression());
                    if (Peek().Type == TokenType.Punctuation && Peek().Value == ",") Advance();
                }
            }
            finally
            {
                _noStructLiteral = outerNoStruct;
            }
            Consume(TokenType.Punctuation, "Очікується ']'");
            return array;
        }
        if (token.Type == TokenType.Keyword && token.Value == "func")
        {
            // Анонімна функція як значення: func(a, b) { ... }
            Advance();
            Consume(TokenType.Punctuation, "Очікується '('");
            var lambdaParams = new List<FunctionParameter>();
            if (Peek().Type != TokenType.Punctuation || Peek().Value != ")")
            {
                do
                {
                    if (Peek().Type == TokenType.Punctuation && Peek().Value == ",") { Advance(); continue; }
                    var pName = Consume(TokenType.Identifier, "Очікується назва параметра");
                    lambdaParams.Add(new FunctionParameter(pName.Value, "any"));
                    if (Peek().Type == TokenType.Punctuation && Peek().Value == ",") Advance();
                } while (Peek().Type != TokenType.Punctuation || Peek().Value != ")");
            }
            Consume(TokenType.Punctuation, "Очікується ')'");
            var lambdaBody = ParseBlockStatement();
            return new FunctionExpression(lambdaParams, lambdaBody);
        }
        if (token.Type == TokenType.Identifier || token.Type == TokenType.Keyword)
        {
            string name = token.Value;
            Advance();

            if (!_noStructLiteral && !string.IsNullOrEmpty(name) && char.IsUpper(name[0]) && Peek().Type == TokenType.Punctuation && Peek().Value == "{")
            {
                Advance();
                var structInit = new StructInitExpression(name);
                
                while (Peek().Type != TokenType.Punctuation || Peek().Value != "}")
                {
                    var fieldName = Consume(TokenType.Identifier, "Очікується назва поля");
                    Consume(TokenType.Punctuation, "Очікується ':'");
                    var fieldValue = ParseExpression();
                    structInit.Fields.Add(new StructFieldInit(fieldName.Value, fieldValue));
                    
                    if (Peek().Type == TokenType.Punctuation && Peek().Value == ",")
                    {
                        Advance();
                    }
                }
                Consume(TokenType.Punctuation, "Очікується '}'");
                return structInit;
            }
            ExpressionNode expr = new VariableExpression(name);
            while (true)
            {
                if (Peek().Type == TokenType.Punctuation && Peek().Value == ".")
                {
                    Advance();
                    var fieldName = Consume(TokenType.Identifier, "Очікується назва поля");
                    expr = new MemberAccessExpression(expr, fieldName.Value);
                }
                else if (Peek().Type == TokenType.Punctuation && Peek().Value == "(")
                {
                    Advance();
                    var args = new List<ExpressionNode>();
                    // Межу з "{" тіла умови тут однозначно визначає ")" —
                    // struct-літерали як аргументи виклику знову дозволені.
                    var outerNoStruct = _noStructLiteral;
                    _noStructLiteral = false;
                    try
                    {
                        while (Peek().Type != TokenType.Punctuation || Peek().Value != ")")
                        {
                            args.Add(ParseExpression());
                            if (Peek().Type == TokenType.Punctuation && Peek().Value == ",") Advance();
                        }
                    }
                    finally
                    {
                        _noStructLiteral = outerNoStruct;
                    }
                    Consume(TokenType.Punctuation, "Очікується ')'");
                    
                    if (expr is MemberAccessExpression mae)
                    {
                        expr = new MethodCallExpression(mae.Object, mae.Member, args);
                    }
                    else if (expr is VariableExpression ve)
                    {
                        var call = new CallExpression(ve.Name);
                        call.Arguments.AddRange(args);
                        expr = call;
                    }
                    else
                    {
                        // expr - уже РЕЗУЛЬТАТ попереднього виклику/індексації
                        // (f()(), arr[0](), map()["f"]()) - це не ім'я, а
                        // значення-функція, яке компілятор викличе через
                        // CALL_VALUE, обчисливши expr як звичайний вираз.
                        expr = new CallValueExpression(expr, args);
                    }
                }
                else if (Peek().Type == TokenType.Punctuation && Peek().Value == "[")
                {
                    Advance();
                    var outerNoStruct = _noStructLiteral;
                    _noStructLiteral = false;
                    ExpressionNode index;
                    try
                    {
                        index = ParseExpression();
                    }
                    finally
                    {
                        _noStructLiteral = outerNoStruct;
                    }
                    Consume(TokenType.Punctuation, "Очікується ']'");
                    expr = new IndexExpression(expr, index);
                }
                else break;
            }
            return expr;
        }
        if (token.Type == TokenType.Punctuation && token.Value == "(")
        {
            Advance();
            var outerNoStruct = _noStructLiteral;
            _noStructLiteral = false;
            ExpressionNode expr;
            try
            {
                expr = ParseExpression();
            }
            finally
            {
                _noStructLiteral = outerNoStruct;
            }
            Consume(TokenType.Punctuation, "Очікується ')'");
            return expr;
        }
        throw new Exception($"Невідомий вираз на рядку {token.Line}, стовпець {token.Column}");
    }
}