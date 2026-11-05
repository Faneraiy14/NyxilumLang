#!/usr/bin/env python3
"""Порівняльний тест цілої арифметики: усі 8 цілих типів x усі оператори x граничні значення.
Генерує ту саму програму на NyxilumLang (системний режим) і на C; вивід має збігатися побайтово.
Семантика NyxilumLang: операція в межах свого типу (u8+u8 переповнюється як u8), зсув бере
лічильник по модулю 32 (типи до 32 біт) чи 64, ділення типів до 32 біт рахується в 64 бітах
(MIN/-1 дає MIN, як у Java). Ділення на 0 і int64 MIN/-1 (етап S2) не перевіряються.
Використання: gen_arith.py <out.nx> <out.c>"""
import sys

TYPES = [("int8", 8, True), ("uint8", 8, False), ("int16", 16, True), ("uint16", 16, False),
         ("int32", 32, True), ("uint32", 32, False), ("int64", 64, True), ("uint64", 64, False)]
OPS = ["+", "-", "*", "/", "%", "&", "|", "^", "<<", ">>", "<", "<=", ">", ">=", "==", "!="]

def values(bits, signed):
    lo, hi = (-(1 << (bits - 1)), (1 << (bits - 1)) - 1) if signed else (0, (1 << bits) - 1)
    vs = {0, 1, 2, 3, 7, 13, 31, 33, 63, 100, hi, hi - 1, lo, lo + 1, (hi >> 1), 0x5A5A5A5A5A5A5A5A & hi}
    if signed: vs |= {-1, -2, -7, -13, -100, lo >> 1}
    return sorted(v for v in vs if lo <= v <= hi)

def ctype(bits, signed): return f"{'' if signed else 'u'}int{bits}_t"

nx, c = [], []
c.append("#include <stdio.h>\n#include <stdint.h>\n#define B(x) ((x) ? \"True\" : \"False\")\nint main(void) {")
nx.append("// Згенеровано gen_arith.py - не редагувати")
for name, bits, signed in TYPES:
    vs = values(bits, signed)
    ct = ctype(bits, signed)
    nx.append(f"func t_{name}(v: ptr<{name}>, n: int64) {{")
    nx.append("    for i in 0..n {\n        for j in 0..n {\n            var a = v[i]\n            var b = v[j]")
    c.append(f"  {{ static const {ct} v[] = {{{', '.join(str(x) + ('LL' if signed else 'ULL') if x != -(1 << 63) else '(-9223372036854775807LL-1)' for x in vs)}}};")
    c.append(f"    int n = {len(vs)};\n    for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) {{ {ct} a = v[i], b = v[j];")
    fmt = "%lld" if signed else "%llu"
    cast = "(long long)" if signed else "(unsigned long long)"
    wide = "int64_t" if signed else "uint64_t"
    sh = 31 if bits <= 32 else 63
    for op in OPS:
        if op in ("/", "%"):
            guard_nx = "b != 0" + (" && !(a == -9223372036854775807 - 1 && b == -1)" if bits == 64 and signed else "")
            nx.append(f"            if {guard_nx} {{\n                print(a {op} b)\n            }}")
            guard_c = "b != 0" + (" && !(a == INT64_MIN && b == -1)" if bits == 64 and signed else "")
            c.append(f"      if ({guard_c}) printf(\"{fmt}\\n\", {cast}({ct})(({wide})a {op} ({wide})b));")
        elif op in ("<<", ">>"):
            nx.append(f"            print(a {op} b)")
            if op == "<<":
                expr = f"({ct})((uint64_t)a << (b & {sh}))" if bits == 64 else f"({ct})(uint32_t)((uint32_t)a << (b & {sh}))"
            else:
                expr = f"({ct})(a >> (b & {sh}))" if bits == 64 else f"({ct})(({'int32_t' if signed else 'uint32_t'})a >> (b & {sh}))"
            c.append(f"      printf(\"{fmt}\\n\", {cast}{expr});")
        elif op in ("<", "<=", ">", ">=", "==", "!="):
            nx.append(f"            print(a {op} b)")
            c.append(f"      printf(\"%s\\n\", B(a {op} b));")
        else:
            nx.append(f"            print(a {op} b)")
            c.append(f"      printf(\"{fmt}\\n\", {cast}({ct})((uint64_t)a {op} (uint64_t)b));")
    nx.append("        }\n    }\n}")
    c.append("    }\n  }")
nx.append("func main() {")
for name, bits, signed in TYPES:
    vs = values(bits, signed)
    nx.append(f"    var v_{name}: ptr<{name}> = alloc({len(vs)} * sizeof({name}))")
    for k, x in enumerate(vs):
        lit = str(x) if x != -(1 << 63) else "-9223372036854775807 - 1"
        nx.append(f"    v_{name}[{k}] = {lit}")
    nx.append(f"    t_{name}(v_{name}, {len(vs)})")
nx.append("}")
c.append("  return 0;\n}")
open(sys.argv[1], "w").write("\n".join(nx) + "\n")
open(sys.argv[2], "w").write("\n".join(c) + "\n")
