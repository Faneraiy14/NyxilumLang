#!/bin/bash
# ============================================================
# run_native.sh - тести нативного компілятора NyxilumLang.
# Кожен tests/native/*.nx виконується:
#   1) у VM (еталон виводу),
#   2) скомпільованим у x86 (nx compile-native),
#   3) скомпільованим у ARM64 (--target linux-arm64) і запущеним через
#      qemu-aarch64 - якщо є ARM64-бекенд і інструменти
#      (binutils-aarch64-linux-gnu, qemu-user); інакше ARM64 пропускається.
# Вивід має збігатися ПОБАЙТОВО (рядки "Runtime Error" VM відкидаються -
# нативна програма на необробленому throw просто виходить з кодом 1).
# Код виходу: "// expect-exit: N" у файлі, інакше - як у VM. (VM ігнорує
# return з main(), нативна програма робить його кодом виходу, як у C.)
#
# Запуск:  bash tests/native/run_native.sh      (NX=... - інший nx)
# ============================================================
cd "$(dirname "${BASH_SOURCE[0]}")" || exit 1
NX="${NX:-nx}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

have_arm64=1
if ! command -v aarch64-linux-gnu-as >/dev/null || ! command -v qemu-aarch64 >/dev/null; then
    have_arm64=0
    echo "⚠️  ARM64 пропущено: немає aarch64-linux-gnu-as або qemu-aarch64"
    echo "    (sudo apt install binutils-aarch64-linux-gnu qemu-user)"
fi

pass=0
fail=0
for f in *.nx; do
    name="${f%.nx}"
    expected_exit=$(grep -oP '^// expect-exit: \K[0-9]+' "$f")
    vm_out=$(timeout 30 "$NX" "$f" 2>&1)
    vm_code=$?
    vm_out=$(printf '%s\n' "$vm_out" | grep -v '^Runtime Error' | grep -v '^  рядок ' | grep -v '^Traceback')
    [ -z "$expected_exit" ] && expected_exit=$vm_code

    ok=1
    report=""
    for arch in x86 arm64; do
        if [ "$arch" = "arm64" ] && [ "$have_arm64" = 0 ]; then
            continue
        fi
        bin="$WORK/${name}_$arch"
        if [ "$arch" = "x86" ]; then
            build=$("$NX" compile-native "$f" -o "$bin" 2>&1)
            run=("$bin")
        else
            build=$("$NX" compile-native "$f" -o "$bin" --target linux-arm64 2>&1)
            run=(qemu-aarch64 "$bin")
        fi
        if [ ! -x "$bin" ]; then
            ok=0
            report+="    $arch: не скомпілювалось: $(printf '%s' "$build" | tail -1)"$'\n'
            continue
        fi
        out=$(timeout 30 "${run[@]}" 2>&1)
        code=$?
        if [ "$out" != "$vm_out" ]; then
            ok=0
            report+="    $arch: вивід відрізняється від VM:"$'\n'"$(diff <(printf '%s\n' "$vm_out") <(printf '%s\n' "$out") | head -8 | sed 's/^/      /')"$'\n'
        fi
        if [ "$code" != "$expected_exit" ]; then
            ok=0
            report+="    $arch: код виходу $code, очікувався $expected_exit"$'\n'
        fi
    done

    if [ $ok = 1 ]; then
        echo "✅ $name"
        pass=$((pass + 1))
    else
        echo "❌ $name"
        printf '%s' "$report"
        fail=$((fail + 1))
    fi
done

echo "======================================"
echo "Успішно: $pass | Провалено: $fail"
[ $fail = 0 ]
