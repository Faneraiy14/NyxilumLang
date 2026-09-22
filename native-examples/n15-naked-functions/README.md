# Фаза N15 - naked-функції (живий тест)

Той самий підхід, що n12/n13/n14 - freestanding `gcc -m32
-ffreestanding -nostdlib` C-гарнес, плюс два "негативні" тести
(перевіряють, що небезпечний код чесно НЕ компілюється).

## Нове в мові

`func f() naked { ... }` - компілятор НЕ генерує стандартний пролог
(`push %ebp`/`mov %esp,%ebp`) чи епілог (`mov %ebp,%esp`/`pop %ebp`/
`ret`) - лише мітку й тіло, ДОСЛІВНО. Тіло СВІДОМО обмежене ЛИШЕ
викликами (`asm(...)` чи звичайні функції) - без параметрів. Той
самий рівень "повної відповідальності програміста", що GCC/Clang
`__attribute__((naked))`.

```
func nakedSetMarker() naked {
    asm("movl $777, __g_marker")
    asm("ret")          // МАЄ написати сам - компілятор нічого не додає
}
```

## Файли

- **`n15_test.nx`** - позитивний тест: naked-функція пише в
  compiler-керовану глобальну (`__g_marker`) через сирий `asm()`,
  повертається сама через `asm("ret")`; звичайна функція читає її
  назад ЗВИЧАЙНИМ NyxilumLang-кодом.
- **`n15_reject_var.nx`** / **`n15_reject_params.nx`** - негативні
  тести: naked із локальною змінною/параметром МАЄ впасти з чіткою
  помилкою компіляції (без прологу `%ebp` не встановлено - обидва
  були б небезпечними).
- **`harness.c`** - викликає ЗВИЧАЙНУ (не-naked) `callNakedSetMarker`,
  яка сама викликає naked-функцію - доводить коректність виклику в
  ОБИДВА боки (звідки б esp-баланс міг зламатись, якби naked щось
  робив не так).

## Як перевірити самостійно

```bash
# Позитивний тест
nx compile-native n15_test.nx -o n15_test --target nyxos-kernel
gcc -m32 -std=gnu11 -ffreestanding -nostdlib -fno-stack-protector -c harness.c -o harness.o
ld -m elf_i386 -e _start harness.o n15_test.o -o n15run
./n15run; echo "exit: $?"    # 0 = marker дійшов до 777 і назад

# Негативні тести (МАЮТЬ провалитись з чіткою помилкою)
nx compile-native n15_reject_var.nx -o /tmp/x --target nyxos-kernel     # exit 1
nx compile-native n15_reject_params.nx -o /tmp/x --target nyxos-kernel # exit 1
```

Структурна перевірка (за згенерованим `n15_test.s`) підтвердила: у
`nakedSetMarker:` немає `push %ebp`/`mov %esp,%ebp` - тіло починається
одразу з `movl $777, __g_marker`.
