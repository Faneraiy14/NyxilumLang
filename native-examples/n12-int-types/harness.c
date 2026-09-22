/* Фаза N12 - freestanding C-гарнес (-m32 -ffreestanding -nostdlib, без
 * libc) - той самий підхід, що для всіх попередніх kernel-примітивів
 * цього репозиторію. Кожна перевірка встановлює один БІТ у fail_mask,
 * якщо реальний результат не збігається з еталонним C-обчисленням;
 * exit-код процесу = fail_mask (0 = усі перевірки пройшли).
 */

typedef unsigned int u32;
typedef int i32;

extern i32 addI32(i32 a, i32 b);
extern u32 addU32(u32 a, u32 b);
extern i32 divI32(i32 a, i32 b);
extern u32 divU32(u32 a, u32 b);
extern i32 ltSignedI32(i32 a, i32 b);
extern i32 ltUnsignedU32(u32 a, u32 b);
extern i32 shrI32(i32 a, i32 n);
extern u32 shrU32(u32 a, u32 n);
extern i32 notI32(i32 a);
extern i32 roundtripSignedViaNumber(i32 x);
extern u32 roundtripUnsignedViaNumber(u32 x);
extern i32 bitwiseNoConversion(i32 a, i32 b);

static void _exit_(int code) {
    /* "a"(1)/"b"(code) - явні регістрові обмеження (НЕ ручний mov
     * усередині asm-рядка) - попередня версія з ручним "mov $1,%eax"
     * ЗАТИРАЛА той самий eax, куди GCC САМ поклав code для %0 (обидва
     * претендували на eax) - реальний, знайдений живцем баг у самому
     * гарнесі (не в компільованому коді), що робив ЦЮ функцію завжди
     * exit(1) незалежно від code. */
    __asm__ volatile (
        "int $0x80"
        :
        : "a"(1), "b"(code)
    );
    __builtin_unreachable();
}

void _start(void) {
    unsigned fail = 0;

    /* 1. UInt32 wraparound: 0xFFFFFFFF + 1 -> 0 (справжнє переповнення,
     * не double-артефакт - double ЛЕГКО вмістив би 4294967296.0 без
     * жодного переповнення, тому цей тест конкретно доводить, що
     * значення справді живе в 32-бітному регістрі). */
    if (addU32(0xFFFFFFFFu, 1u) != 0u) fail |= (1u << 0);

    /* 2. Int32 wraparound: INT32_MAX + 1 -> INT32_MIN (двійкове
     * доповнення, як і в C). */
    if ((u32)addI32(2147483647, 1) != 0x80000000u) fail |= (1u << 1);

    /* 3. Signed division truncates toward zero: -7 / 2 -> -3. */
    if (divI32(-7, 2) != -3) fail |= (1u << 2);

    /* 4. Unsigned division of a value that would be "negative" if
     * reinterpreted signed: 0xFFFFFFFE (=4294967294u) / 2 -> 0x7FFFFFFF.
     * Якби компілятор помилково використав idiv (signed) тут, результат
     * був би -1 (0xFFFFFFFF), а не 0x7FFFFFFF - реальна, спостережувана
     * різниця. */
    if (divU32(0xFFFFFFFEu, 2u) != 0x7FFFFFFFu) fail |= (1u << 3);

    /* 5. Signed '<': -1 < 1 -> true (звичайне signed порівняння). */
    if (ltSignedI32(-1, 1) != 1) fail |= (1u << 4);

    /* 6. Unsigned '<': той САМИЙ бітовий патерн -1 (0xFFFFFFFF), але як
     * UInt32 це НАЙБІЛЬШЕ можливе значення, тому "< 1" тут МАЄ бути
     * false - ЯКЩО компілятор помилково лишив setl (signed) замість
     * setb (unsigned) тут, тест впіймає це напряму. */
    if (ltUnsignedU32(0xFFFFFFFFu, 1u) != 0) fail |= (1u << 5);

    /* 7. Арифметичний зсув (signed): -8 >> 1 -> -4 (знак зберігається,
     * sar). */
    if (shrI32(-8, 1) != -4) fail |= (1u << 6);

    /* 8. Логічний зсув (unsigned): те САМЕ бітове значення -8
     * (0xFFFFFFF8) як UInt32, >> 1 -> 0x7FFFFFFC (заповнення нулями,
     * shr) - ПРЯМА протилежність тесту 7 з ТИМ САМИМ вхідним бітовим
     * патерном - найпряміший доказ, що sar/shr реально обираються
     * по-різному залежно від типу, а не завжди однаково. */
    if (shrU32(0xFFFFFFF8u, 1u) != 0x7FFFFFFCu) fail |= (1u << 7);

    /* 9. Бітове "not" без double round-trip: ~5 -> -6 (двійкове
     * доповнення). */
    if (notI32(5) != -6) fail |= (1u << 8);

    /* 10. toNumber/toI32 round-trip симетричний для звичайного signed
     * значення. */
    if (roundtripSignedViaNumber(-12345) != -12345) fail |= (1u << 9);

    /* 11. toNumber/toU32 round-trip для великого unsigned значення
     * (>2^31 - саме той клас чисел, що ламав print_double до Фази N10). */
    if (roundtripUnsignedViaNumber(3000000000u) != 3000000000u) fail |= (1u << 10);

    /* 12. Побітові на Int32 напряму на GPR (and/xor/or), еталон -
     * той самий вираз, порахований тут звичайним C. */
    {
        i32 a = 0x0F0F, b = 0x00FF;
        i32 expect = (a & b) | (a ^ b);
        if (bitwiseNoConversion(a, b) != expect) fail |= (1u << 11);
    }

    _exit_((int)fail);
}
