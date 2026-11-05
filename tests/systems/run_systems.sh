#!/bin/bash
# Тести системного бекенда x86-64 (SYSTEMS_ROADMAP.md, S1):
#   NN_*.nx + NN_*.expected - вивід побайтово (і "// expect-exit: N");
#   NN_*.nx з c/NN_*.c - зшивка з C (--obj + gcc);
#   gen_arith.py - уся ціла арифметика проти C;
#   errors/*.nx - компіляція має впасти з "// expect-error: текст".
# Запуск: bash tests/systems/run_systems.sh   (NX=... - інший nx)
cd "$(dirname "${BASH_SOURCE[0]}")" || exit 1
NX="${NX:-nx}"
W="$(mktemp -d)"; trap 'rm -rf "$W"' EXIT
pass=0; fail=0
ok() { echo "✅ $1"; pass=$((pass + 1)); }
bad() { echo "❌ $1"; [ -n "$2" ] && printf '%s\n' "$2" | head -8 | sed 's/^/    /'; fail=$((fail + 1)); }

for f in [0-9]*.nx; do
    n="${f%.nx}"
    if [ -f "c/$n.c" ]; then
        if out=$("$NX" compile-native "$f" -o "$W/$n" --target linux-x64 --obj 2>&1) && gcc -O2 -w "c/$n.c" "$W/$n.o" -o "$W/$n.bin" 2>"$W/err" && res=$("$W/$n.bin" 2>&1); then ok "$n (з C)"; else bad "$n (з C)" "$out$(cat "$W/err" 2>/dev/null)$res"; fi
        continue
    fi
    if ! out=$("$NX" compile-native "$f" -o "$W/$n" --target linux-x64 2>&1); then bad "$n" "$out"; continue; fi
    "$W/$n" > "$W/$n.out" 2>&1; code=$?
    want=$(grep -oP '^// expect-exit: \K[0-9]+' "$f"); want=${want:-0}
    if ! cmp -s "$n.expected" "$W/$n.out"; then bad "$n" "$(diff "$n.expected" "$W/$n.out")"
    elif [ "$code" != "$want" ]; then bad "$n" "код виходу $code, очікувався $want"
    else ok "$n"; fi
done

python3 gen_arith.py "$W/a.nx" "$W/a.c"
if gcc -O0 -w "$W/a.c" -o "$W/a_c" && "$W/a_c" > "$W/a_c.out" && "$NX" compile-native "$W/a.nx" -o "$W/a_nx" --target linux-x64 >/dev/null 2>&1 && "$W/a_nx" > "$W/a_nx.out" && cmp -s "$W/a_c.out" "$W/a_nx.out"; then
    ok "арифметика проти C ($(wc -l < "$W/a_c.out") результатів)"
else bad "арифметика проти C" "$(diff "$W/a_c.out" "$W/a_nx.out" 2>&1 | head -5)"; fi

for f in errors/*.nx; do
    want=$(grep -oP '^// expect-error: \K.+' "$f")
    out=$("$NX" compile-native "$f" -o "$W/e" --target linux-x64 2>&1)
    if [ $? -ne 0 ] && printf '%s' "$out" | grep -qF "$want"; then ok "помилка: $(basename "$f" .nx)"; else bad "помилка: $(basename "$f" .nx)" "чекав «$want», отримав: $out"; fi
done
echo "======================================"
echo "Успішно: $pass | Провалено: $fail"
[ $fail = 0 ]
