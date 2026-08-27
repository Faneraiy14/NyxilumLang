namespace NyxilumLang.Core;

public class Lexer
{
    private readonly string _source;
    private int _pos;
    private int _line = 1;
    private int _column = 1;
    private readonly List<Token> _tokens = new();
    private readonly HashSet<string> _keywords = new()
    {
        "func", "var", "if", "else", "while", "for", "return",
        "break", "continue",
        "true", "false", "null", "print", "in",
        "struct", "self", "import", "extends", "super",
        "try", "catch", "throw",
        "readLine", "readInt", "readDouble",
        "readFile", "writeFile", "appendFile", "fileExists",
        "sqrt", "abs", "pow", "sin", "cos", "tan",
        "round", "floor", "ceil", "max", "min"
    };

    public Lexer(string source) { _source = source; }

    public List<Token> Tokenize()
    {
        while (_pos < _source.Length)
        {
            char c = _source[_pos];

            if (char.IsWhiteSpace(c))
            {
                if (c == '\n') { _line++; _column = 1; }
                else { _column++; }
                _pos++;
                continue;
            }

            if (c == '/' && _pos + 1 < _source.Length && _source[_pos + 1] == '/')
            {
                while (_pos < _source.Length && _source[_pos] != '\n') _pos++;
                continue;
            }

            if (c == '/' && _pos + 1 < _source.Length && _source[_pos + 1] == '*')
            {
                _pos += 2;
                while (_pos + 1 < _source.Length && !(_source[_pos] == '*' && _source[_pos + 1] == '/')) _pos++;
                _pos += 2;
                continue;
            }

            if (char.IsDigit(c))
            {
                string num = "";
                while (_pos < _source.Length && char.IsDigit(_source[_pos]))
                {
                    num += _source[_pos];
                    _pos++;
                }
                if (_pos < _source.Length && _source[_pos] == '.')
                {
                    if (_pos + 1 < _source.Length && _source[_pos + 1] == '.')
                    {
                        _tokens.Add(new Token(TokenType.Number, num, _line, _column));
                        _column += num.Length;
                        continue;
                    }
                    num += ".";
                    _pos++;
                    while (_pos < _source.Length && char.IsDigit(_source[_pos]))
                    {
                        num += _source[_pos];
                        _pos++;
                    }
                }
                _tokens.Add(new Token(TokenType.Number, num, _line, _column));
                _column += num.Length;
                continue;
            }

            if (c == '.')
            {
                if (_pos + 1 < _source.Length && _source[_pos + 1] == '.')
                {
                    _tokens.Add(new Token(TokenType.Operator, "..", _line, _column));
                    _pos += 2;
                    _column += 2;
                    continue;
                }
                _tokens.Add(new Token(TokenType.Punctuation, ".", _line, _column));
                _pos++;
                _column++;
                continue;
            }

            if (c == '"')
            {
                _pos++;
                string str = "";
                var literalParts = new List<string>();
                var exprTokenLists = new List<List<Token>>();
                bool hasInterpolation = false;

                while (_pos < _source.Length && _source[_pos] != '"')
                {
                    if (_source[_pos] == '\\' && _pos + 1 < _source.Length)
                    {
                        char escaped = _source[_pos + 1] switch
                        {
                            'n' => '\n',
                            't' => '\t',
                            'r' => '\r',
                            '0' => '\0',
                            '"' => '"',
                            '\\' => '\\',
                            '$' => '$',
                            var other => other
                        };
                        str += escaped;
                        _pos += 2;
                        continue;
                    }

                    // Інтерполяція ${вираз}: розпізнається лише на точній парі
                    // символів $ і { (заекранувати як звичайний $ можна через \$).
                    // Вкладені { } всередині виразу (мапи/структури) рахуються по
                    // глибині — але лапки чи коментарі всередині самого ${...} тут
                    // не підтримуються (винести в змінну поза рядком, якщо треба).
                    if (_source[_pos] == '$' && _pos + 1 < _source.Length && _source[_pos + 1] == '{')
                    {
                        hasInterpolation = true;
                        literalParts.Add(str);
                        str = "";

                        _pos += 2;
                        int exprStart = _pos;
                        int depth = 1;
                        while (_pos < _source.Length && depth > 0)
                        {
                            if (_source[_pos] == '{') depth++;
                            else if (_source[_pos] == '}') { depth--; if (depth == 0) break; }
                            _pos++;
                        }
                        if (depth != 0)
                            throw new Exception($"Незакрита інтерполяція \"${{...\" у рядку {_line}");

                        string exprSource = _source.Substring(exprStart, _pos - exprStart);
                        _pos++; // '}'

                        var exprTokens = new Lexer(exprSource).Tokenize();
                        exprTokens.RemoveAt(exprTokens.Count - 1); // прибрати EOF
                        exprTokenLists.Add(exprTokens);
                        continue;
                    }

                    str += _source[_pos];
                    _pos++;
                }
                _pos++;

                if (!hasInterpolation)
                {
                    _tokens.Add(new Token(TokenType.String, str, _line, _column));
                    _column += str.Length + 2;
                    continue;
                }

                literalParts.Add(str);

                // Розгортаємо "a${b}c" у ("" + "a" + (b) + "c") — той самий
                // оператор +, яким і так усюди конкатенують рядок з числом чи
                // будь-чим іншим (Nx сам приводить до рядка при конкатенації,
                // див. VirtualMachine.ADD) — тому ні парсер, ні VM не потребують
                // жодних змін під новий синтаксис. Порожній рядок на початку —
                // гарантія, що ланцюжок стає рядковим із самого першого +,
                // незалежно від того, чи рядок починається з тексту чи з виразу.
                _tokens.Add(new Token(TokenType.Punctuation, "(", _line, _column));
                _tokens.Add(new Token(TokenType.String, "", _line, _column));
                for (int i = 0; i < literalParts.Count; i++)
                {
                    _tokens.Add(new Token(TokenType.Operator, "+", _line, _column));
                    _tokens.Add(new Token(TokenType.String, literalParts[i], _line, _column));
                    if (i < exprTokenLists.Count)
                    {
                        _tokens.Add(new Token(TokenType.Operator, "+", _line, _column));
                        _tokens.Add(new Token(TokenType.Punctuation, "(", _line, _column));
                        _tokens.AddRange(exprTokenLists[i]);
                        _tokens.Add(new Token(TokenType.Punctuation, ")", _line, _column));
                    }
                }
                _tokens.Add(new Token(TokenType.Punctuation, ")", _line, _column));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                string word = "";
                // Апостроф - законна частина українського письма всередині
                // слова (ім'я, об'єкт, п'ять), а не рядковий роздільник (той
                // - лише "). Дозволяємо його ТІЛЬКИ між двома літерами, щоб
                // не проковтнути випадковий/кінцевий апостроф і не
                // приховати справжню помилку "Невідомий символ" деінде.
                while (_pos < _source.Length && (
                    char.IsLetterOrDigit(_source[_pos]) || _source[_pos] == '_' ||
                    (_source[_pos] == '\'' && _pos + 1 < _source.Length && char.IsLetter(_source[_pos + 1]))))
                {
                    word += _source[_pos];
                    _pos++;
                }

                TokenType type = TokenType.Identifier;
                if (_keywords.Contains(word))
                {
                    type = word == "true" || word == "false" ? TokenType.Boolean : TokenType.Keyword;
                }
                _tokens.Add(new Token(type, word, _line, _column));
                _column += word.Length;
                continue;
            }

            string op = "";
            if (c == '-' && _pos + 1 < _source.Length && _source[_pos + 1] == '>')
            {
                op = "->";
                _pos += 2;
            }
            else if (c == '=' && _pos + 1 < _source.Length && _source[_pos + 1] == '=')
            {
                op = "==";
                _pos += 2;
            }
            else if (c == '!' && _pos + 1 < _source.Length && _source[_pos + 1] == '=')
            {
                op = "!=";
                _pos += 2;
            }
            else if (c == '>' && _pos + 1 < _source.Length && _source[_pos + 1] == '=')
            {
                op = ">=";
                _pos += 2;
            }
            else if (c == '<' && _pos + 1 < _source.Length && _source[_pos + 1] == '=')
            {
                op = "<=";
                _pos += 2;
            }
            else if (c == '&' && _pos + 1 < _source.Length && _source[_pos + 1] == '&')
            {
                op = "&&";
                _pos += 2;
            }
            else if (c == '|' && _pos + 1 < _source.Length && _source[_pos + 1] == '|')
            {
                op = "||";
                _pos += 2;
            }
            else if (c == '+' && _pos + 1 < _source.Length && _source[_pos + 1] == '+')
            {
                op = "++";
                _pos += 2;
            }
            else if (c == '-' && _pos + 1 < _source.Length && _source[_pos + 1] == '-')
            {
                op = "--";
                _pos += 2;
            }
            else if (c == '+' && _pos + 1 < _source.Length && _source[_pos + 1] == '=')
            {
                op = "+=";
                _pos += 2;
            }
            else if (c == '-' && _pos + 1 < _source.Length && _source[_pos + 1] == '=')
            {
                op = "-=";
                _pos += 2;
            }
            else if (c == '*' && _pos + 1 < _source.Length && _source[_pos + 1] == '=')
            {
                op = "*=";
                _pos += 2;
            }
            else if (c == '/' && _pos + 1 < _source.Length && _source[_pos + 1] == '=')
            {
                op = "/=";
                _pos += 2;
            }
            else if ("+-*/%=!<>|&".Contains(c))
            {
                op = c.ToString();
                _pos++;
            }
            else if ("(){}[],;:".Contains(c))
            {
                op = c.ToString();
                _pos++;
                _tokens.Add(new Token(TokenType.Punctuation, op, _line, _column));
                _column++;
                continue;
            }
            else
            {
                throw new Exception($"Невідомий символ '{c}' на рядку {_line}, стовпець {_column}");
            }

            if (!string.IsNullOrEmpty(op))
            {
                _tokens.Add(new Token(TokenType.Operator, op, _line, _column));
                _column += op.Length;
            }
        }

        _tokens.Add(new Token(TokenType.EOF, "", _line, _column));
        return _tokens;
    }
}