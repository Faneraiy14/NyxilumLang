using System.Globalization;
using System.Text;
using NyxilumLang.AST;

namespace NyxilumLang.Tools;

// "nx dump-ast файл.nx" - ПОВНЕ дерево розбору S-виразами, кожен оператор на своєму
// рядку з номером рядка (оператор@рядок). На відміну від "nx ast" (схема для anylint,
// без виразів) тут є все: вирази, типи, сирий запис чисел. Еталон для selfhost/parser.nx
// (S8, SYSTEMS_ROADMAP.md): дерево з Nx-парсера має бути побайтово таким самим.
public static class AstSexprDumper
{
    public static string Dump(ProgramNode program)
    {
        var sb = new StringBuilder();
        foreach (var s in program.Statements)
        {
            Stmt(sb, s, 0);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static void Pad(StringBuilder sb, int d) => sb.Append(' ', d * 2);

    private static void Str(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char ch in s)
        {
            sb.Append(ch switch
            {
                '\\' => "\\\\", '"' => "\\\"", '\n' => "\\n", '\t' => "\\t", '\r' => "\\r", '\0' => "\\0",
                _ => ch.ToString()
            });
        }
        sb.Append('"');
    }

    private static void Opt(StringBuilder sb, string? s) => sb.Append(s ?? "_");

    private static void Block(StringBuilder sb, BlockStatement? b, int d)
    {
        if (b == null) { sb.Append('_'); return; }
        sb.Append("(block@").Append(b.Line);
        foreach (var s in b.Statements)
        {
            sb.Append('\n');
            Pad(sb, d + 1);
            Stmt(sb, s, d + 1);
        }
        sb.Append(')');
    }

    private static void Params(StringBuilder sb, List<FunctionParameter> ps)
    {
        sb.Append('(');
        for (int i = 0; i < ps.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(ps[i].Name).Append(':').Append(ps[i].Type);
        }
        sb.Append(')');
    }

    private static void Stmt(StringBuilder sb, StatementNode s, int d)
    {
        switch (s)
        {
            case ExpressionStatement e:
                sb.Append("(expr@").Append(e.Line).Append(' '); Expr(sb, e.Expression, d); sb.Append(')'); break;
            case VariableDeclaration v:
                sb.Append("(var@").Append(v.Line).Append(' ').Append(v.Name).Append(' '); Opt(sb, v.TypeAnnotation);
                sb.Append(v.IsVolatile ? " volatile " : " ");
                if (v.Initializer == null) sb.Append('_'); else Expr(sb, v.Initializer, d);
                sb.Append(')'); break;
            case IfStatement i:
                sb.Append("(if@").Append(i.Line).Append(' '); Expr(sb, i.Condition, d); sb.Append(' ');
                Block(sb, i.ThenBlock, d); sb.Append(' '); Block(sb, i.ElseBlock, d); sb.Append(')'); break;
            case WhileStatement w:
                sb.Append("(while@").Append(w.Line).Append(' '); Expr(sb, w.Condition, d); sb.Append(' ');
                Block(sb, w.Body, d); sb.Append(')'); break;
            case ForStatement f:
                sb.Append("(for@").Append(f.Line).Append(' ').Append(f.VariableName).Append(' '); Expr(sb, f.Start, d); sb.Append(' ');
                if (f.End == null) sb.Append('_'); else Expr(sb, f.End, d);
                sb.Append(' '); Block(sb, f.Body, d); sb.Append(')'); break;
            case BlockStatement b:
                Block(sb, b, d); break;
            case ReturnStatement r:
                sb.Append("(return@").Append(r.Line).Append(' ');
                if (r.Value == null) sb.Append('_'); else Expr(sb, r.Value, d);
                sb.Append(')'); break;
            case PrintStatement p:
                sb.Append("(print@").Append(p.Line).Append(' '); Expr(sb, p.Expression, d); sb.Append(')'); break;
            case FunctionDeclaration fn:
                sb.Append("(func@").Append(fn.Line).Append(' ').Append(fn.Name).Append(' '); Params(sb, fn.Parameters);
                sb.Append(" -> "); Opt(sb, fn.ReturnType); sb.Append(fn.IsNaked ? " naked " : " ");
                Block(sb, fn.Body, d); sb.Append(')'); break;
            case StructDeclaration st:
                sb.Append("(struct@").Append(st.Line).Append(' ').Append(st.Name).Append(" extends "); Opt(sb, st.ParentName);
                sb.Append(" (");
                for (int k = 0; k < st.Fields.Count; k++)
                {
                    if (k > 0) sb.Append(' ');
                    sb.Append(st.Fields[k].Name).Append(':').Append(st.Fields[k].Type);
                }
                sb.Append(')');
                foreach (var m in st.Methods)
                {
                    sb.Append('\n'); Pad(sb, d + 1); Stmt(sb, m, d + 1);
                }
                sb.Append(')'); break;
            case TryStatement t:
                sb.Append("(try@").Append(t.Line).Append(' '); Block(sb, t.TryBlock, d);
                sb.Append(" catch ").Append(t.CatchVariableName).Append(' '); Block(sb, t.CatchBlock, d); sb.Append(')'); break;
            case ImportStatement im:
                sb.Append("(import@").Append(im.Line).Append(' '); Str(sb, im.Path);
                if (im.Names != null) { sb.Append(" {"); foreach (var n in im.Names) sb.Append(' ').Append(n); sb.Append(" }"); }
                sb.Append(')'); break;
            case BreakStatement:
                sb.Append("(break@").Append(s.Line).Append(')'); break;
            case ContinueStatement:
                sb.Append("(continue@").Append(s.Line).Append(')'); break;
            case ThrowStatement th:
                sb.Append("(throw@").Append(th.Line).Append(' '); Expr(sb, th.Value, d); sb.Append(')'); break;
            default:
                sb.Append("(?").Append(s.GetType().Name).Append(')'); break;
        }
    }

    private static void Args(StringBuilder sb, List<ExpressionNode> args, int d)
    {
        foreach (var a in args) { sb.Append(' '); Expr(sb, a, d); }
    }

    private static void Expr(StringBuilder sb, ExpressionNode e, int d)
    {
        switch (e)
        {
            case LiteralExpression l:
                switch (l.Value)
                {
                    case null: sb.Append("null"); break;
                    case string s: Str(sb, s); break;
                    case bool b: sb.Append(b ? "true" : "false"); break;
                    case double v:
                        // сирий запис з тексту; без нього (x++ -> x = x + 1) - "#значення"
                        if (l.Raw != null) sb.Append(l.Raw);
                        else sb.Append('#').Append(v.ToString("R", CultureInfo.InvariantCulture));
                        break;
                    default: sb.Append("(?lit)"); break;
                }
                break;
            case VariableExpression v:
                sb.Append(v.Name); break;
            case BinaryExpression b:
                sb.Append('(').Append(b.Operator).Append(' '); Expr(sb, b.Left, d); sb.Append(' '); Expr(sb, b.Right, d); sb.Append(')'); break;
            case UnaryExpression u:
                sb.Append("(u").Append(u.Operator).Append(' '); Expr(sb, u.Operand, d); sb.Append(')'); break;
            case CallExpression c:
                sb.Append("(call ").Append(c.FunctionName); Args(sb, c.Arguments, d); sb.Append(')'); break;
            case ArrayLiteralExpression a:
                sb.Append("[arr"); Args(sb, a.Elements, d); sb.Append(']'); break;
            case IndexExpression ix:
                sb.Append("(idx "); Expr(sb, ix.Array, d); sb.Append(' '); Expr(sb, ix.Index, d); sb.Append(')'); break;
            case MemberAccessExpression m:
                sb.Append("(. "); Expr(sb, m.Object, d); sb.Append(' ').Append(m.Member).Append(')'); break;
            case StructInitExpression si:
                sb.Append("(new ").Append(si.StructName);
                foreach (var f in si.Fields) { sb.Append(' ').Append(f.Name).Append(':'); Expr(sb, f.Value, d); }
                sb.Append(')'); break;
            case FunctionExpression fe:
                sb.Append("(lambda "); Params(sb, fe.Parameters); sb.Append(' '); Block(sb, fe.Body, d); sb.Append(')'); break;
            case CallValueExpression cv:
                sb.Append("(callv "); Expr(sb, cv.Callee, d); Args(sb, cv.Arguments, d); sb.Append(')'); break;
            case MethodCallExpression mc:
                sb.Append("(mcall "); Expr(sb, mc.Object, d); sb.Append(' ').Append(mc.MethodName); Args(sb, mc.Arguments, d); sb.Append(')'); break;
            default:
                sb.Append("(?").Append(e.GetType().Name).Append(')'); break;
        }
    }
}
