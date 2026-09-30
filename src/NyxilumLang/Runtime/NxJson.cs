using System.Globalization;
using System.Text;

namespace NyxilumLang.Runtime;

// Проста ручна серіалізація/парсинг JSON для toJson()/fromJson().
// fromJson() завжди перетворює JSON-об'єкти в NxMap (не struct — у структур
// фіксована форма, оголошена через `struct`, довільний JSON не можна
// автоматично "вгадати" як конкретний struct-тип).
public static class NxJson
{
    public static string Serialize(object? value)
    {
        var sb = new StringBuilder();
        Write(value, sb);
        return sb.ToString();
    }

    private static void Write(object? value, StringBuilder sb)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case double d:
                sb.Append(d.ToString(CultureInfo.InvariantCulture));
                break;
            case int i:
                sb.Append(i.ToString(CultureInfo.InvariantCulture));
                break;
            case string s:
                WriteString(s, sb);
                break;
            case List<object> arr:
                sb.Append('[');
                for (int idx = 0; idx < arr.Count; idx++)
                {
                    if (idx > 0) sb.Append(',');
                    Write(arr[idx], sb);
                }
                sb.Append(']');
                break;
            case NxMap map:
                sb.Append('{');
                bool firstM = true;
                foreach (var kv in map.Entries)
                {
                    if (!firstM) sb.Append(',');
                    firstM = false;
                    WriteString(kv.Key?.ToString() ?? "", sb);
                    sb.Append(':');
                    Write(kv.Value, sb);
                }
                sb.Append('}');
                break;
            case Dictionary<string, object> structDict:
                sb.Append('{');
                bool firstS = true;
                foreach (var kv in structDict)
                {
                    if (kv.Key == "__type") continue;
                    if (!firstS) sb.Append(',');
                    firstS = false;
                    WriteString(kv.Key, sb);
                    sb.Append(':');
                    Write(kv.Value, sb);
                }
                sb.Append('}');
                break;
            default:
                WriteString(value.ToString() ?? "", sb);
                break;
        }
    }

    private static void WriteString(string s, StringBuilder sb)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
    }

    public static object? Deserialize(string json)
    {
        int pos = 0;
        return ParseValue(json, ref pos);
    }

    private static object? ParseValue(string s, ref int pos)
    {
        SkipWhitespace(s, ref pos);
        if (pos >= s.Length) throw new Exception("Неочікуваний кінець JSON");
        char c = s[pos];
        if (c == '{') return ParseObject(s, ref pos);
        if (c == '[') return ParseArray(s, ref pos);
        if (c == '"') return ParseString(s, ref pos);
        if (c == 't') { Expect(s, ref pos, "true"); return true; }
        if (c == 'f') { Expect(s, ref pos, "false"); return false; }
        if (c == 'n') { Expect(s, ref pos, "null"); return null; }
        return ParseNumber(s, ref pos);
    }

    private static void SkipWhitespace(string s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
    }

    private static void Expect(string s, ref int pos, string literal)
    {
        if (pos + literal.Length > s.Length || s.Substring(pos, literal.Length) != literal)
            throw new Exception($"Очікувалось '{literal}' у JSON на позиції {pos}");
        pos += literal.Length;
    }

    private static NxMap ParseObject(string s, ref int pos)
    {
        var map = new NxMap();
        pos++; // '{'
        SkipWhitespace(s, ref pos);
        if (pos < s.Length && s[pos] == '}') { pos++; return map; }
        while (true)
        {
            SkipWhitespace(s, ref pos);
            string key = ParseString(s, ref pos);
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length || s[pos] != ':') throw new Exception("Очікувалось ':' у JSON-об'єкті");
            pos++;
            var value = ParseValue(s, ref pos);
            map.Entries[key] = value!;
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == ',') { pos++; continue; }
            if (pos < s.Length && s[pos] == '}') { pos++; break; }
            throw new Exception("Очікувалось ',' або '}' у JSON-об'єкті");
        }
        return map;
    }

    private static List<object> ParseArray(string s, ref int pos)
    {
        var list = new List<object>();
        pos++; // '['
        SkipWhitespace(s, ref pos);
        if (pos < s.Length && s[pos] == ']') { pos++; return list; }
        while (true)
        {
            var value = ParseValue(s, ref pos);
            list.Add(value!);
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == ',') { pos++; continue; }
            if (pos < s.Length && s[pos] == ']') { pos++; break; }
            throw new Exception("Очікувалось ',' або ']' у JSON-масиві");
        }
        return list;
    }

    private static string ParseString(string s, ref int pos)
    {
        if (pos >= s.Length || s[pos] != '"') throw new Exception("Очікувався рядок у JSON");
        pos++;
        var sb = new StringBuilder();
        while (pos < s.Length && s[pos] != '"')
        {
            char c = s[pos];
            if (c == '\\' && pos + 1 < s.Length)
            {
                char next = s[pos + 1];
                // \uXXXX - так JSON-серіалізатори (напр. Telegram API) кодують
                // усе не-ASCII: "євро" приходить як євро.
                // Раніше тут спрацьовувала гілка "_ => next" і з'являвся
                // сміттєвий рядок "u0454u0432...". Емодзі (сурогатні пари
                // 😀) збираються самі: кожна половинка - окремий char.
                if (next == 'u')
                {
                    if (pos + 6 > s.Length || !int.TryParse(s.AsSpan(pos + 2, 4),
                            System.Globalization.NumberStyles.HexNumber, null, out int code))
                        throw new Exception($"Невалідна \\u-послідовність у JSON на позиції {pos}");
                    sb.Append((char)code);
                    pos += 6;
                    continue;
                }
                sb.Append(next switch
                {
                    'n' => '\n', 't' => '\t', 'r' => '\r', '"' => '"', '\\' => '\\', '/' => '/',
                    'b' => '\b', 'f' => '\f',
                    _ => next
                });
                pos += 2;
            }
            else
            {
                sb.Append(c);
                pos++;
            }
        }
        pos++; // закриваюча "
        return sb.ToString();
    }

    private static double ParseNumber(string s, ref int pos)
    {
        int start = pos;
        while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '-' || s[pos] == '+' || s[pos] == '.' || s[pos] == 'e' || s[pos] == 'E')) pos++;
        return double.Parse(s.Substring(start, pos - start), CultureInfo.InvariantCulture);
    }
}
