using System.Text.Json.Nodes;
using NyxilumLang.AST;

namespace NyxilumLang.Tools;

// "nx ast файл.nx" — виводить AST у JSON за канонічною "місток"-схемою
// {"type","line","attributes","children"}, яку напряму споживає anylint
// (github.com/Faneraiy14/anylint) — той самий формат, що видає й
// PhpProvider (php-parser) усередині anylint, тож структурні правила
// (dead-code-after-return, empty-catch) працюють ОДНАКОВО для .php й .nx
// без жодної зміни свого коду. Навмисно НЕ дамп внутрішньої структури
// NyxilumLang.AST "як є" — узгоджена спільна вокабулярна форма важливіша
// за повноту, інакше кожен провайдер мови в anylint писав би власне
// мапування "з нуля" замість використання одних і тих самих назв вузлів.
public static class AstJsonDumper
{
    public static string Dump(ProgramNode program)
    {
        var root = new JsonObject
        {
            ["type"] = "Root",
            ["line"] = 1,
            ["attributes"] = new JsonObject(),
            ["children"] = new JsonArray(ConvertBlock(program.Statements, 1)),
        };
        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
    }

    private static JsonObject ConvertBlock(List<StatementNode> statements, int line)
    {
        var children = new JsonArray();
        foreach (var stmt in statements)
        {
            children.Add(Convert(stmt));
        }
        return new JsonObject
        {
            ["type"] = "Block",
            ["line"] = statements.Count > 0 ? statements[0].Line : line,
            ["attributes"] = new JsonObject(),
            ["children"] = children,
        };
    }

    private static JsonObject Leaf(string type, int line, JsonObject? attributes = null)
        => new()
        {
            ["type"] = type,
            ["line"] = line,
            ["attributes"] = attributes ?? new JsonObject(),
            ["children"] = new JsonArray(),
        };

    private static JsonObject Convert(StatementNode stmt) => stmt switch
    {
        FunctionDeclaration f => new JsonObject
        {
            ["type"] = "FunctionDecl",
            ["line"] = f.Line,
            ["attributes"] = new JsonObject { ["name"] = f.Name },
            ["children"] = new JsonArray(ConvertBlock(f.Body.Statements, f.Line)),
        },
        ReturnStatement r => Leaf("Return", r.Line),
        TryStatement t => new JsonObject
        {
            ["type"] = "TryCatch",
            ["line"] = t.Line,
            ["attributes"] = new JsonObject(),
            ["children"] = new JsonArray(
                ConvertBlock(t.TryBlock.Statements, t.Line),
                new JsonObject
                {
                    ["type"] = "CatchClause",
                    ["line"] = t.Line,
                    ["attributes"] = new JsonObject { ["variable"] = t.CatchVariableName },
                    ["children"] = new JsonArray(ConvertBlock(t.CatchBlock.Statements, t.Line)),
                }
            ),
        },
        IfStatement i => new JsonObject
        {
            ["type"] = "If",
            ["line"] = i.Line,
            ["attributes"] = new JsonObject(),
            // findAll('Block') на PHP-боці збирає вузли з усього дерева
            // незалежно від позиції серед children, тож ElseBlock просто
            // не додається, коли його нема - без null-заглушки.
            ["children"] = i.ElseBlock != null
                ? new JsonArray(ConvertBlock(i.ThenBlock.Statements, i.Line), ConvertBlock(i.ElseBlock.Statements, i.Line))
                : new JsonArray(ConvertBlock(i.ThenBlock.Statements, i.Line)),
        },
        WhileStatement w => new JsonObject
        {
            ["type"] = "While",
            ["line"] = w.Line,
            ["attributes"] = new JsonObject(),
            ["children"] = new JsonArray(ConvertBlock(w.Body.Statements, w.Line)),
        },
        ForStatement fo => new JsonObject
        {
            ["type"] = "For",
            ["line"] = fo.Line,
            ["attributes"] = new JsonObject(),
            ["children"] = new JsonArray(ConvertBlock(fo.Body.Statements, fo.Line)),
        },
        StructDeclaration s => new JsonObject
        {
            ["type"] = "Other",
            ["line"] = s.Line,
            ["attributes"] = new JsonObject { ["kind"] = "Struct", ["name"] = s.Name },
            ["children"] = new JsonArray(s.Methods.Select(m => (JsonNode)Convert(m)).ToArray()),
        },
        _ => Leaf("Other", stmt.Line, new JsonObject { ["kind"] = stmt.GetType().Name }),
    };
}
