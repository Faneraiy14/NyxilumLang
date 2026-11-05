#!/usr/bin/env python3
"""Порівняльний тест друку дробових. Еталон - Python (repr: найкоротший запис, що читається назад
у те саме число, при рівності - парна цифра), переписаний у формат VM (звичайний запис для
-3 <= k <= 17, інакше 1E+18). Python рахує ті самі IEEE-операції в тому самому порядку.
(VM у C# не годиться за еталон: для степенів двійки друкує запис, що читається як ІНШЕ число.)
Числа: випадкові літерали, ділення/множення, ланцюжки множення до ∞ і ділення до субнормальних і 0.
Використання: gen_float.py <out.nx> <out.expected> [seed]"""
import random, sys, math
from decimal import Decimal
rnd = random.Random(int(sys.argv[3]) if len(sys.argv) > 3 else 1)
def lit():
    k = rnd.choice([0, 1, 2, 3, 6, 9, 12])
    whole = rnd.randint(0, 10 ** rnd.randint(0, 9))
    frac = rnd.randint(0, 10 ** k - 1) if k else 0
    return f"{whole}.{str(frac).zfill(k) if k else '0'}"
def fmt(v):
    if math.isnan(v): return "NaN"
    if math.isinf(v): return "-Infinity" if v < 0 else "Infinity"
    neg = math.copysign(1, v) < 0
    if v == 0: return "-0" if neg else "0"
    t = Decimal(repr(abs(v))).normalize().as_tuple()
    ds = "".join(map(str, t.digits)); n = len(ds); k = n + t.exponent
    if k > 17 or k < -3:
        o = ds[0] + ("." + ds[1:] if n > 1 else "")
        ee = k - 1
        o += "E" + ("-" if ee < 0 else "+") + str(abs(ee)).zfill(2)
    elif k <= 0: o = "0." + "0" * -k + ds
    elif k < n: o = ds[:k] + "." + ds[k:]
    else: o = ds + "0" * (k - n)
    return ("-" if neg else "") + o
exp = []
import operator
OPF = {"+": operator.add, "-": operator.sub, "*": operator.mul, "/": operator.truediv}
out = ["// Згенеровано gen_float.py - не редагувати", "func main() {"]
for a, b in [("0.1", "0.2"), ("1.0", "3.0"), ("2.0", "3.0"), ("1.0", "7.0"), ("100.0", "3.0")]:
    out += [f"    print({a} + {b})", f"    print({a} / {b})"]
    exp += [fmt(float(a) + float(b)), fmt(float(a) / float(b))]
for _ in range(300):
    a, b = lit(), lit()
    op = rnd.choice(["+", "-", "*", "/"])
    if op == "/" and float(b) == 0: b = "1.5"
    out.append(f"    print({a} {op} {b})")
    exp.append(fmt(OPF[op](float(a), float(b))))
for start, mul in [("1.5", "7.3"), ("0.1", "10.0"), ("3.0", "1.0001"), ("1.0", "2.0")]:
    x, i = f"x{len(out)}", f"i{len(out)}"
    out += [f"    var {x} = {start}", f"    var {i} = 0"]
    v = float(start)
    for _ in range(400):
        exp.append(fmt(v)); v = v * float(mul)
    out += [f"    while {i} < 400 {{", f"        print({x})", f"        {x} = {x} * {mul}", f"        {i} = {i} + 1", "    }"]
for start, div in [("1.0", "3.0"), ("123.456", "10.0"), ("1.0", "2.0")]:
    x, i = f"x{len(out)}", f"i{len(out)}"
    out += [f"    var {x} = {start}", f"    var {i} = 0"]
    v = float(start)
    for _ in range(1100):
        exp.append(fmt(v)); v = v / float(div)
    out += [f"    while {i} < 1100 {{", f"        print({x})", f"        {x} = {x} / {div}", f"        {i} = {i} + 1", "    }"]
# випадкові біти: будь-які float64 і float32 (субнормальні, межі, NaN, нескінченності)
import struct
def f32_repr(bits):
    v = struct.unpack("<f", struct.pack("<I", bits))[0]
    if math.isnan(v) or math.isinf(v) or v == 0: return fmt(v)
    for p in range(1, 10):
        t = format(abs(v), f".{p - 1}e")
        try: back = struct.unpack("<f", struct.pack("<f", float(t)))[0]
        except OverflowError: continue
        if back == abs(v):
            d = Decimal(t).normalize().as_tuple()
            ds = "".join(map(str, d.digits)); n = len(ds); k = n + d.exponent
            if k > 17 or k < -3:
                o = ds[0] + ("." + ds[1:] if n > 1 else "") + "E" + ("-" if k - 1 < 0 else "+") + str(abs(k - 1)).zfill(2)
            elif k <= 0: o = "0." + "0" * -k + ds
            elif k < n: o = ds[:k] + "." + ds[k:]
            else: o = ds + "0" * (k - n)
            return ("-" if v < 0 else "") + o
specials64 = [0x7FF0000000000000, 0xFFF0000000000000, 0x7FF8000000000000, 1, 0x000FFFFFFFFFFFFF, 0x0010000000000000, 0x7FEFFFFFFFFFFFFF, 0x8000000000000000, 0x3FF0000000000001]
for b in specials64 + [rnd.getrandbits(64) for _ in range(3000)] + [rnd.getrandbits(52) | (rnd.randint(0, 2046) << 52) for _ in range(1000)]:
    sb = b - (1 << 64) if b >= 1 << 63 else b
    lit = str(sb) if sb != -(1 << 63) else "-9223372036854775807 - 1"
    out.append(f"    print(bitsToFloat64({lit}))")
    exp.append(fmt(struct.unpack("<d", struct.pack("<Q", b))[0]))
for b in [0x7F800000, 0xFF800000, 1, 0x007FFFFF, 0x00800000, 0x7F7FFFFF, 0x80000000] + [rnd.getrandbits(32) for _ in range(2000)]:
    sb = b - (1 << 32) if b >= 1 << 31 else b
    out.append(f"    print(bitsToFloat32(int32({sb})))")
    exp.append(f32_repr(b))
out.append("}")
open(sys.argv[1], "w").write("\n".join(out) + "\n")
open(sys.argv[2], "w").write("\n".join(exp) + "\n")
