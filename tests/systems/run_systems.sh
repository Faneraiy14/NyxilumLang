#!/bin/bash
# Тести системного бекенда x86-64 (SYSTEMS_ROADMAP.md, S1):
#   NN_*.nx + NN_*.expected - вивід побайтово (і "// expect-exit: N");
#   NN_*.nx з c/NN_*.c - зшивка з C (--obj + gcc);
#   gen_arith.py - уся ціла арифметика проти C;
#   gen_float.py - друк float64/float32 проти Python (найкоротший запис);
#   gen_java.py - ділення, d2i/f2l, %, float32, зсуви - побітово проти справжньої Java;
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
    # кожен тест - у своїй порожній теці, з аргументами й змінною оточення (для 11_files)
    mkdir -p "$W/run_$n"
    (cd "$W/run_$n" && NX_TEST_VAR=значення "$W/$n" "перший аргумент" two) > "$W/$n.out" 2>&1; code=$?
    want=$(grep -oP '^// expect-exit: \K[0-9]+' "$f"); want=${want:-0}
    if ! cmp -s "$n.expected" "$W/$n.out"; then bad "$n" "$(diff "$n.expected" "$W/$n.out")"
    elif [ "$code" != "$want" ]; then bad "$n" "код виходу $code, очікувався $want"
    else ok "$n"; fi
done

# HTTP: нативний сервер віддає файли, curl їх забирає (S7)
if "$NX" compile-native http_server.nx -o "$W/hs" --target linux-x64 >/dev/null 2>&1; then
    H="$W/www"; mkdir -p "$H/sub"; head -c 300000 /dev/urandom > "$H/big.bin"; echo "привіт" > "$H/sub/a.txt"
    "$W/hs" "$H" "$W/port" & hp=$!
    for _ in $(seq 50); do [ -s "$W/port" ] && break; sleep 0.1; done
    P=$(cat "$W/port" 2>/dev/null)
    r1=$(curl -s "http://127.0.0.1:$P/big.bin" | cmp - "$H/big.bin" && echo ok)
    r2=$(curl -s "http://127.0.0.1:$P/sub/a.txt")
    r3=$(curl -s -o /dev/null -w "%{http_code}" "http://127.0.0.1:$P/нема")
    r4=$(curl -s -o /dev/null -w "%{http_code}" --path-as-is "http://127.0.0.1:$P/../etc/passwd")
    curl -s "http://127.0.0.1:$P/quit" >/dev/null
    for _ in $(seq 30); do kill -0 $hp 2>/dev/null || break; sleep 0.1; done
    if [ "$r1$r2$r3$r4" = "okпривіт404403" ] && ! kill -0 $hp 2>/dev/null; then ok "HTTP-сервер на Nx: curl забрав 300 КБ, 404, 403, зупинка"
    else kill $hp 2>/dev/null; bad "HTTP-сервер" "файл:$r1 текст:$r2 нема:$r3 вихід:$r4"; fi
else bad "HTTP-сервер" "не скомпілювався"; fi

# S8: лексер і парсер на Nx (selfhost/) дають те саме, що C# (nx dump-tokens / nx dump-ast), на всіх .nx
# репозиторію, включно з selfhost_cases/ (крайні випадки й помилки - текст помилки теж має збігтися)
if "$NX" compile-native ../../selfhost/dump_tokens.nx -o "$W/dt" --target linux-x64 >/dev/null 2>&1 &&
   "$NX" compile-native ../../selfhost/dump_ast.nx -o "$W/da" --target linux-x64 >/dev/null 2>&1; then
    lt=0; lbad=""; pbad=""
    while IFS= read -r f; do
        lt=$((lt + 1))
        "$NX" dump-tokens "$f" > "$W/lc.out" 2>&1; lce=$?
        "$W/dt" "$f" > "$W/ln.out" 2>&1; lne=$?
        if [ $lce != $lne ] || ! cmp -s "$W/lc.out" "$W/ln.out"; then lbad="$lbad $f"; fi
        "$NX" dump-ast "$f" > "$W/pc.out" 2>&1; pce=$?
        "$W/da" "$f" > "$W/pn.out" 2>&1; pne=$?
        if [ $pce != $pne ] || ! cmp -s "$W/pc.out" "$W/pn.out"; then pbad="$pbad $f"; fi
    done < <(cd ../.. && git ls-files '*.nx' | sed 's|^|../../|')
    if [ -z "$lbad" ]; then ok "лексер на Nx = C# ($lt файлів)"; else bad "лексер на Nx" "$lbad"; fi
    if [ -z "$pbad" ]; then ok "парсер на Nx = C# ($lt файлів)"; else bad "парсер на Nx" "$pbad"; fi
else bad "лексер/парсер на Nx" "selfhost/dump_*.nx не скомпілювались"; fi

python3 gen_arith.py "$W/a.nx" "$W/a.c"
if gcc -O0 -w "$W/a.c" -o "$W/a_c" && "$W/a_c" > "$W/a_c.out" && "$NX" compile-native "$W/a.nx" -o "$W/a_nx" --target linux-x64 >/dev/null 2>&1 && "$W/a_nx" > "$W/a_nx.out" && cmp -s "$W/a_c.out" "$W/a_nx.out"; then
    ok "арифметика проти C ($(wc -l < "$W/a_c.out") результатів)"
else bad "арифметика проти C" "$(diff "$W/a_c.out" "$W/a_nx.out" 2>&1 | head -5)"; fi

for seed in 1 2; do
    python3 gen_float.py "$W/f.nx" "$W/f.exp" $seed
    if "$NX" compile-native "$W/f.nx" -o "$W/f" --target linux-x64 >/dev/null 2>&1 && "$W/f" > "$W/f.out" && cmp -s "$W/f.exp" "$W/f.out"; then
        ok "друк дробових проти Python, seed $seed ($(wc -l < "$W/f.exp") чисел)"
    else bad "друк дробових, seed $seed" "$(diff "$W/f.exp" "$W/f.out" 2>&1 | head -5)"; fi
done

if command -v java >/dev/null; then
    mkdir -p "$W/jv"
    for seed in 1 2; do
        python3 gen_java.py "$W/j.nx" "$W/jv/Out.java" $seed
        if (cd "$W/jv" && java Out.java > "$W/j.exp") && "$NX" compile-native "$W/j.nx" -o "$W/j" --target linux-x64 >/dev/null 2>&1 && "$W/j" > "$W/j.out" && cmp -s "$W/j.exp" "$W/j.out"; then
            ok "числа як у Java, seed $seed ($(wc -l < "$W/j.exp") результатів)"
        else bad "числа як у Java, seed $seed" "$(diff "$W/j.exp" "$W/j.out" 2>&1 | head -5)"; fi
    done
else echo "⚠️  Java не знайдено - порівняння з Java пропущено"; fi

for f in errors/*.nx; do
    want=$(grep -oP '^// expect-error: \K.+' "$f")
    out=$("$NX" compile-native "$f" -o "$W/e" --target linux-x64 2>&1)
    if [ $? -ne 0 ] && printf '%s' "$out" | grep -qF "$want"; then ok "помилка: $(basename "$f" .nx)"; else bad "помилка: $(basename "$f" .nx)" "чекав «$want», отримав: $out"; fi
done
echo "======================================"
echo "Успішно: $pass | Провалено: $fail"
[ $fail = 0 ]
