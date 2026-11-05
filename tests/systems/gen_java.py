#!/usr/bin/env python3
"""Числа як у Java: одна й та сама перевірка на Java (еталон - справжня JVM) і на NyxilumLang
(системний режим x86-64). Порівнюються біти: ділення/залишок int і long (разом із MIN / -1),
d2i d2l f2i f2l (NaN, нескінченності, за межами типу - насичення), % для double і float (fmod),
арифметика float32, перетворення long/int -> double/float, зсуви int (<<, >>, >>>).
NaN друкується в канонічному вигляді (як Double.doubleToLongBits).
Використання: gen_java.py <out.nx> <Out.java> [seed]"""
import random, struct, sys
rnd = random.Random(int(sys.argv[3]) if len(sys.argv) > 3 else 1)
def dbits(x): return struct.unpack("<q", struct.pack("<d", x))[0]
D = [0.0, -0.0, 1.5, -2.5, 0.1, 1e10, 3.7e19, -9.3e18, 2147483647.5, -2147483648.9, 4294967296.0,
     1e300, 5e-324, -1e-310, 9.223372036854776e18, -9.223372036854776e18, 1.8446744073709552e19,
     float("inf"), float("-inf"), float("nan"), 7.0, -7.0, 0.3, 123456.789]
D += [struct.unpack("<d", struct.pack("<Q", rnd.getrandbits(64)))[0] for _ in range(12)]
I32 = [0, 1, -1, 7, -7, 2, 100, 2147483647, -2147483648, -2147483647, 65536, 31, 32, 33] + [rnd.randint(-2**31, 2**31 - 1) for _ in range(8)]
I64 = [0, 1, -1, 7, -7, 2, 2**63 - 1, -2**63, -2**63 + 1, 2**32, 2**53 + 1, 63, 64, 65] + [rnd.randint(-2**63, 2**63 - 1) for _ in range(8)]
def l64(v): return str(v) if v != -2**63 else "-9223372036854775807 - 1"
def jl(v): return f"{v}L" if v != -2**63 else "Long.MIN_VALUE"
def ji(v): return str(v) if v != -2**31 else "Integer.MIN_VALUE"

nx = ["// Згенеровано gen_java.py - не редагувати",
      "func cb(x: float64) -> int64 {\n    if x != x {\n        return 9221120237041090560\n    }\n    return float64Bits(x)\n}",
      "func cf(x: float32) -> int32 {\n    if x != x {\n        return 2143289344\n    }\n    return float32Bits(x)\n}",
      "func main() {"]
jv = ["public class Out { public static void main(String[] args) {", "StringBuilder o = new StringBuilder();"]
def P(nxexpr, jexpr): nx.append(f"    print({nxexpr})"); jv.append(f"o.append({jexpr}).append('\\n');")

nx.append(f"    var dv: ptr<float64> = alloc({len(D)} * 8)")
jv.append(f"double[] dv = new double[{len(D)}];")
for k, x in enumerate(D):
    nx.append(f"    dv[{k}] = bitsToFloat64({l64(dbits(x))})"); jv.append(f"dv[{k}] = Double.longBitsToDouble({jl(dbits(x))});")
nx.append(f"    for i in 0..{len(D)} {{\n        var x = dv[i]"); jv.append(f"for (int i = 0; i < {len(D)}; i++) {{ double x = dv[i];")
P("int32(x)", "(int)x"); P("int64(x)", "(long)x")
P("int32(float32(x))", "(int)(float)x"); P("int64(float32(x))", "(long)(float)x")
P("cf(float32(x))", "Float.floatToIntBits((float)x)")
nx.append(f"        for j in 0..{len(D)} {{\n            var y = dv[j]"); jv.append(f"for (int j = 0; j < {len(D)}; j++) {{ double y = dv[j];")
for op in ["+", "-", "*", "/", "%"]:
    P(f"cb(x {op} y)", f"Double.doubleToLongBits(x {op} y)")
    P(f"cf(float32(x) {op} float32(y))", f"Float.floatToIntBits((float)x {op} (float)y)")
P("x < y", "(x < y ? \"True\" : \"False\")"); P("x == y", "(x == y ? \"True\" : \"False\")")
nx.append("        }\n    }"); jv.append("} }")

nx.append(f"    var iv: ptr<int32> = alloc({len(I32)} * 4)"); jv.append(f"int[] iv = new int[{len(I32)}];")
for k, v in enumerate(I32): nx.append(f"    iv[{k}] = {v if v != -2**31 else '-2147483647 - 1'}"); jv.append(f"iv[{k}] = {ji(v)};")
nx.append(f"    for i in 0..{len(I32)} {{\n        var a = iv[i]"); jv.append(f"for (int i = 0; i < {len(I32)}; i++) {{ int a = iv[i];")
P("cb(float64(a))", "Double.doubleToLongBits((double)a)"); P("cf(float32(a))", "Float.floatToIntBits((float)a)")
nx.append(f"        for j in 0..{len(I32)} {{\n            var b = iv[j]"); jv.append(f"for (int j = 0; j < {len(I32)}; j++) {{ int b = iv[j];")
nx.append("            if b != 0 {\n                print(a / b)\n                print(a % b)\n            }"); jv.append("if (b != 0) { o.append(a / b).append('\\n'); o.append(a % b).append('\\n'); }")
P("a << b", "a << b"); P("a >> b", "a >> b"); P("int32(uint32(a) >> b)", "a >>> b"); P("a * b", "a * b")
nx.append("        }\n    }"); jv.append("} }")

nx.append(f"    var lv: ptr<int64> = alloc({len(I64)} * 8)"); jv.append(f"long[] lv = new long[{len(I64)}];")
for k, v in enumerate(I64): nx.append(f"    lv[{k}] = {l64(v)}"); jv.append(f"lv[{k}] = {jl(v)};")
nx.append(f"    for i in 0..{len(I64)} {{\n        var a = lv[i]"); jv.append(f"for (int i = 0; i < {len(I64)}; i++) {{ long a = lv[i];")
P("cb(float64(a))", "Double.doubleToLongBits((double)a)"); P("cf(float32(a))", "Float.floatToIntBits((float)a)")
P("int32(a)", "(int)a")
nx.append(f"        for j in 0..{len(I64)} {{\n            var b = lv[j]"); jv.append(f"for (int j = 0; j < {len(I64)}; j++) {{ long b = lv[j];")
nx.append("            if b != 0 {\n                print(a / b)\n                print(a % b)\n            }"); jv.append("if (b != 0) { o.append(a / b).append('\\n'); o.append(a % b).append('\\n'); }")
P("a << b", "a << b"); P("a >> b", "a >> b"); P("int64(uint64(a) >> b)", "a >>> b"); P("a * b", "a * b")
nx.append("        }\n    }"); jv.append("} }")
nx.append("}"); jv.append("System.out.print(o); } }")
open(sys.argv[1], "w").write("\n".join(nx) + "\n")
open(sys.argv[2], "w").write("\n".join(jv) + "\n")
