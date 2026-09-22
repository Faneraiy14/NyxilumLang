/* Фаза N13 - freestanding C-гарнес (-m32 -ffreestanding -nostdlib, без
 * libc), той самий підхід, що n12-int-types/harness.c (включно з
 * уроком про _exit_ register-clobber - виправлено тут ОДРАЗУ, з
 * явними регістровими обмеженнями, а не ручним mov).
 */

typedef signed char i8;
typedef unsigned char u8;
typedef short i16;
typedef unsigned short u16;

extern i8  addI8(i8 a, i8 b);
extern u8  addU8(u8 a, u8 b);
extern i16 addI16(i16 a, i16 b);
extern u16 addU16(u16 a, u16 b);
extern i8  divI8(i8 a, i8 b);
extern u8  divU8(u8 a, u8 b);
extern i16 divI16(i16 a, i16 b);
extern u16 divU16(u16 a, u16 b);
extern i8  ltSignedI8(i8 a, i8 b);
extern i8  ltUnsignedU8(u8 a, u8 b);
extern i16 shrI16(i16 a, i16 n);
extern u16 shrU16(u16 a, u16 n);
extern u8  notU8(u8 a);
extern i16 roundtripSignedI16(i16 x);
extern u8  roundtripUnsignedU8(u8 x);
extern i8  numberToI8Boundary(void);
extern u8  mulU8Overflow(u8 a, u8 b);

static void _exit_(int code) {
    __asm__ volatile ( "int $0x80" : : "a"(1), "b"(code) );
    __builtin_unreachable();
}

void _start(void) {
    unsigned fail = 0;

    /* 1. Int8 переповнення: 100 + 50 = 150, за межами int8 (-128..127) -
     * МАЄ обгорнутись до -106 (150 - 256). */
    if (addI8(100, 50) != -106) fail |= (1u << 0);

    /* 2. UInt8 переповнення: 200 + 100 = 300 -> 300 mod 256 = 44. */
    if (addU8(200, 100) != 44) fail |= (1u << 1);

    /* 3. Int16 переповнення: 30000 + 10000 = 40000, за межами int16 -
     * 40000 - 65536 = -25536. */
    if (addI16(30000, 10000) != -25536) fail |= (1u << 2);

    /* 4. UInt16 переповнення: 60000 + 10000 = 70000 mod 65536 = 4464. */
    if (addU16(60000, 10000) != 4464) fail |= (1u << 3);

    /* 5. Signed 8-бітне ділення, обрізка до нуля: -100 / 7 -> -14. */
    if (divI8(-100, 7) != -14) fail |= (1u << 4);

    /* 6. Unsigned 8-бітне ділення: 250 / 7 -> 35 (byte-ширини idiv/div
     * тест - якщо компілятор помилково взяв 32-бітний div/idiv замість
     * byte-ширини, dividend у AX/EAX не збігся б, і результат був би
     * інший). */
    if (divU8(250, 7) != 35) fail |= (1u << 5);

    /* 7. Signed 16-бітне ділення: -30000 / 7 -> -4285. */
    if (divI16(-30000, 7) != -4285) fail |= (1u << 6);

    /* 8. Unsigned 16-бітне ділення: 60000 / 7 -> 8571. */
    if (divU16(60000, 7) != 8571) fail |= (1u << 7);

    /* 9. Signed '<': -1 < 1 -> true. */
    if (ltSignedI8(-1, 1) != 1) fail |= (1u << 8);

    /* 10. Unsigned '<': ТОЙ САМИЙ бітовий патерн 0xFF - як uint8 це 255,
     * НЕ менше за 1. */
    if (ltUnsignedU8((u8)0xFF, 1) != 0) fail |= (1u << 9);

    /* 11. Арифметичний зсув (signed, 16-біт): -8 >> 1 -> -4. */
    if (shrI16(-8, 1) != -4) fail |= (1u << 10);

    /* 12. Логічний зсув (unsigned, 16-біт): ТОЙ САМИЙ бітовий патерн
     * 0xFFF8 (=65528u) як uint16, >> 1 -> 32764 (0x7FFC, заповнення
     * нулями) - пряма протилежність тесту 11 з тим самим вхідним
     * патерном. */
    if (shrU16((u16)0xFFF8, 1) != 32764) fail |= (1u << 11);

    /* 13. '~' на uint8 без double round-trip: ~5 -> 250 (0xFA) -
     * ЗБЕРЕЖЕННЯ ZERO-розширення (не sign-розширення) - якщо
     * EmitNarrowExtend забули викликати після 'not', результат
     * помилково був би -6 (0xFFFFFFFA) замість 250. */
    if (notU8(5) != 250) fail |= (1u << 12);

    /* 14. toNumber/toI16 round-trip симетричний для звичайного signed
     * значення. */
    if (roundtripSignedI16(-12345) != -12345) fail |= (1u << 13);

    /* 15. toNumber/toU8 round-trip для unsigned значення. */
    if (roundtripUnsignedU8(200) != 200) fail |= (1u << 14);

    /* 16. Number -> int8 межа з обрізкою: 300.0 -> 44 (300 mod 256, все
     * ще в signed-діапазоні байта). */
    if (numberToI8Boundary() != 44) fail |= (1u << 15);

    /* 17. UInt8 множення з переповненням: 200*200=40000 mod 256 = 64 -
     * imul дає 32-бітний повний добуток, EmitNarrowExtend МАЄ обрізати
     * до байта - без цього результат був би щось зовсім інше. */
    if (mulU8Overflow(200, 200) != 64) fail |= (1u << 16);

    _exit_((int)fail);
}
