namespace NyxilumLang.Native;

// Асемблерний рантайм ARM64-бекенда - лише "двері": пам'ять, байти
// рядків, сирі масиви, біти double, syscall'и Linux AArch64 (номер у x8,
// svc #0: read=63, write=64, exit=93, nanosleep=101, clock_gettime=113,
// brk=214, getrandom=278). Усе, що можна написати на самій мові, живе в
// Arm64Prelude.nx.
//
// Підпрограми rt_* приймають аргументи в x0..x4, результат - у x0, і
// можуть псувати x0-x17 (згенерований код нічого не тримає в регістрах
// між викликами - усе на стеку).
public partial class NativeCodegenArm64
{
    private static readonly (string Label, string Text)[] RuntimeStrings =
    {
        (".Lstr_null", "null"), (".Lstr_true", "True"), (".Lstr_false", "False"),
        (".Lstr_nan", "NaN"), (".Lstr_inf", "Infinity"), (".Lstr_ninf", "-Infinity"),
        (".Lstr_zero", "0"), (".Lstr_nzero", "-0"), (".Lstr_fn", "<function>"),
        (".Lstr_rterr", "Runtime Error: "),
        (".Lmsg_notnum", "Арифметика й порівняння можливі лише з числами"),
        (".Lmsg_notbool", "Умова має бути bool чи числом"),
        (".Lmsg_plus", "Оператор + працює лише з числами чи рядками"),
        (".Lmsg_index_obj", "Спроба звернутися до елемента масиву, але значення зліва - null або не масив."),
        (".Lmsg_index_num", "Індекс масиву має бути числом"),
        (".Lmsg_range", "Індекс поза межами масиву"),
        (".Lmsg_notarr", "Очікувався масив"),
        (".Lmsg_notstr", "Очікувався рядок"),
        (".Lmsg_notmap", "Очікувалась мапа"),
        (".Lmsg_notfn", "Значення не є функцією"),
        (".Lmsg_arity", "Неправильна кількість аргументів у виклику функції"),
        (".Lmsg_notstruct", "Значення не є структурою"),
        (".Lmsg_nofield", "Структура не має такого поля"),
        (".Lmsg_nomethod", "Структура не має такого методу"),
        (".Lmsg_pop", "pop() з порожнього масиву"),
        (".Lmsg_setlen", "__arrSetLen: масив можна лише вкоротити"),
        (".Lmsg_slice", "Межі підрядка поза рядком"),
        (".Lmsg_oom", "Недостатньо пам'яті"),
    };

    private const string AsmMacros = """
        // TAGOF d, s: d = мітка типу значення s:
        //   0 спец (null/false/true), 1 рядок, 2 масив, 3 мапа, 4 функція,
        //   5 структура, >= 6 - число (звичайний double)
        .macro TAGOF d, s
            lsr \d, \s, #48
            add \d, \d, #7
            and \d, \d, #0xFFFF
        .endm
        // UNBOX d, s: вказівник з нижніх 48 біт
        .macro UNBOX d, s
            and \d, \s, #0xFFFFFFFFFFFF
        .endm
        // TOBOOL r: 0/1 -> false/true
        .macro TOBOOL r
            add \r, \r, #1
            movk \r, #0xFFF9, lsl #48
        .endm
        // LSTR r, label: рядок-літерал з .rodata
        .macro LSTR r, label
            adrp \r, \label
            add \r, \r, :lo12:\label
            movk \r, #0xFFFA, lsl #48
        .endm
        .macro LNULL r
            mov \r, #0xFFF9000000000000
        .endm
        // ERR label: кинути виняток з повідомленням-рядком
        .macro ERR label
            LSTR x0, \label
            b rt_throw
        .endm
        // NUMARG r: r має бути числом, інакше помилка
        .macro NUMARG r
            TAGOF x16, \r
            cmp x16, #6
            b.lo rt_err_notnum
        .endm
        // Результат d0 -> x0
        .macro RETD
            fmov x0, d0
            ret
        .endm
        // Хвостовий виклик функції прелюдії з одним аргументом (x0)
        .macro CALLNX1 fn
            stp x29, x30, [sp, #-16]!
            mov x29, sp
            sub sp, sp, #32
            str x0, [sp, #16]
            LNULL x9
            str x9, [sp]
            bl \fn
            mov sp, x29
            ldp x29, x30, [sp], #16
            ret
        .endm
        """;

    private const string Runtime = """

        // ============================================================ помилки

        rt_err_notnum:    ERR .Lmsg_notnum
        rt_err_notbool:   ERR .Lmsg_notbool
        rt_err_plus:      ERR .Lmsg_plus
        rt_err_index_obj: ERR .Lmsg_index_obj
        rt_err_index_num: ERR .Lmsg_index_num
        rt_err_range:     ERR .Lmsg_range
        rt_err_notarr:    ERR .Lmsg_notarr
        rt_err_notstr:    ERR .Lmsg_notstr
        rt_err_notmap:    ERR .Lmsg_notmap
        rt_err_notfn:     ERR .Lmsg_notfn
        rt_err_arity:     ERR .Lmsg_arity
        rt_err_notstruct: ERR .Lmsg_notstruct
        rt_err_nofield:   ERR .Lmsg_nofield
        rt_err_nomethod:  ERR .Lmsg_nomethod
        rt_err_pop:       ERR .Lmsg_pop
        rt_err_setlen:    ERR .Lmsg_setlen
        rt_err_slice:     ERR .Lmsg_slice

        // throw x0: найближчий try-обробник або "Runtime Error" і вихід 1
        rt_throw:
            adrp x10, exc_top
            ldr x9, [x10, :lo12:exc_top]
            cbz x9, rt_uncaught
            adrp x10, exc_value
            str x0, [x10, :lo12:exc_value]
            ldr x10, [x9]
            ldr x11, [x9, #8]
            ldr x12, [x9, #16]
            mov sp, x10
            mov x29, x11
            br x12

        rt_uncaught:
            bl rt_to_str
            mov x19, x0
            mov x0, #2
            LSTR x1, .Lstr_rterr
            UNBOX x1, x1
            ldr x2, [x1]
            add x1, x1, #8
            bl rt_write
            mov x0, #2
            UNBOX x1, x19
            ldr x2, [x1]
            add x1, x1, #8
            bl rt_write
            mov x0, #2
            adrp x1, print_newline
            add x1, x1, :lo12:print_newline
            mov x2, #1
            bl rt_write
            mov x0, #1
            mov x8, #93
            svc #0

        // ============================================================ пам'ять

        // rt_alloc(x0 = байти) -> x0 = сирий обнулений вказівник, вирівняний
        // на 16. Bump-розподілювач поверх brk, без free (пам'ять ніколи не
        // повертається - для коротких процесів цього досить).
        rt_alloc:
            add x0, x0, #15
            and x9, x0, #-16
            adrp x10, heap_ptr
            add x10, x10, :lo12:heap_ptr
            ldr x11, [x10]
            cbnz x11, 1f
            mov x0, #0
            mov x8, #214
            svc #0
            add x0, x0, #15
            and x0, x0, #-16
            str x0, [x10]
            str x0, [x10, #8]
            mov x11, x0
        1:  add x12, x11, x9
            ldr x13, [x10, #8]
            cmp x12, x13
            b.ls 2f
            mov x14, #0x100000
            add x0, x12, x14
            mov x8, #214
            svc #0
            str x0, [x10, #8]
            cmp x0, x12
            b.lo rt_oom
        2:  str x12, [x10]
            mov x0, x11
            ret

        rt_oom:
            mov x0, #2
            LSTR x1, .Lmsg_oom
            UNBOX x1, x1
            ldr x2, [x1]
            add x1, x1, #8
            mov x8, #64
            svc #0
            mov x0, #1
            mov x8, #93
            svc #0

        // rt_write(x0 = fd, x1 = байти, x2 = кількість) - з дописуванням залишку
        rt_write:
            mov x9, x0
        1:  cbz x2, 2f
            mov x0, x9
            mov x8, #64
            svc #0
            cmp x0, #0
            b.le 2f
            add x1, x1, x0
            sub x2, x2, x0
            b 1b
        2:  ret

        // ============================================================ bool, виклики

        // rt_truthy(x0) -> x0 = 0/1: bool, null (хибність), число (!= 0)
        rt_truthy:
            TAGOF x16, x0
            cmp x16, #6
            b.hs 1f
            cbnz x16, rt_err_notbool
            and x9, x0, #3
            cmp x9, #2
            cset x0, eq
            ret
        1:  fmov d0, x0
            fcmp d0, #0.0
            cset x0, ne
            ret

        rt_is_null:
            LNULL x9
            cmp x0, x9
            cset x0, eq
            TOBOOL x0
            ret

        // rt_fn_prep(x0 = функція, x1 = кількість аргументів) -> x0 = код, x1 = оточення
        rt_fn_prep:
            TAGOF x16, x0
            cmp x16, #4
            b.ne rt_err_notfn
            UNBOX x9, x0
            ldr x10, [x9, #16]
            cmp x10, x1
            b.ne rt_err_arity
            ldr x0, [x9]
            ldr x1, [x9, #8]
            ret

        // ============================================================ арифметика

        rt_add:
            TAGOF x16, x0
            TAGOF x17, x1
            cmp x16, #6
            b.lo 1f
            cmp x17, #6
            b.lo 1f
            fmov d0, x0
            fmov d1, x1
            fadd d0, d0, d1
            RETD
        1:  cmp x16, #1
            b.eq 2f
            cmp x17, #1
            b.ne rt_err_plus
        2:  // рядок + будь-що -> склеювання текстових представлень
            stp x29, x30, [sp, #-32]!
            mov x29, sp
            str x1, [sp, #16]
            bl rt_to_str
            str x0, [sp, #24]
            ldr x0, [sp, #16]
            bl rt_to_str
            mov x1, x0
            ldr x0, [sp, #24]
            ldp x29, x30, [sp], #32
            b rt_concat

        rt_sub:
            NUMARG x0
            NUMARG x1
            fmov d0, x0
            fmov d1, x1
            fsub d0, d0, d1
            RETD
        rt_mul:
            NUMARG x0
            NUMARG x1
            fmov d0, x0
            fmov d1, x1
            fmul d0, d0, d1
            RETD
        rt_div:
            NUMARG x0
            NUMARG x1
            fmov d0, x0
            fmov d1, x1
            fdiv d0, d0, d1
            RETD
        // a % b = a - b*trunc(a/b) - знак як у діленого (як C#/VM)
        rt_mod:
            NUMARG x0
            NUMARG x1
            fmov d0, x0
            fmov d1, x1
            fdiv d2, d0, d1
            frintz d2, d2
            fmsub d0, d2, d1, d0
            RETD
        // унарний мінус як 0 - x: -(0) дає 0, а не -0 (так само, як VM)
        rt_neg:
            NUMARG x0
            fmov d1, x0
            fmov d0, xzr
            fsub d0, d0, d1
            RETD

        // Побітові - над 32-бітними знаковими цілими з насиченням (як VM)
        .macro BITOP name, op
        \name:
            NUMARG x0
            NUMARG x1
            fmov d0, x0
            fmov d1, x1
            fcvtzs w9, d0
            fcvtzs w10, d1
            \op w9, w9, w10
            scvtf d0, w9
            RETD
        .endm
        BITOP rt_band, and
        BITOP rt_bor, orr
        BITOP rt_bxor, eor
        BITOP rt_shl, lsl
        BITOP rt_shr, asr
        rt_bnot:
            NUMARG x0
            fmov d0, x0
            fcvtzs w9, d0
            mvn w9, w9
            scvtf d0, w9
            RETD

        .macro CMPOP name, cond
        \name:
            NUMARG x0
            NUMARG x1
            fmov d0, x0
            fmov d1, x1
            fcmp d0, d1
            cset x0, \cond
            TOBOOL x0
            ret
        .endm
        CMPOP rt_lt, mi
        CMPOP rt_le, ls
        CMPOP rt_gt, gt
        CMPOP rt_ge, ge

        // == : числа - як double (NaN != NaN), рядки - за вмістом,
        // решта - та сама сутність (масиви/мапи за посиланням, як у VM)
        rt_eq:
            TAGOF x16, x0
            TAGOF x17, x1
            cmp x16, #6
            b.lo 1f
            cmp x17, #6
            b.lo 3f
            fmov d0, x0
            fmov d1, x1
            fcmp d0, d1
            cset x0, eq
            TOBOOL x0
            ret
        1:  cmp x16, #1
            b.ne 2f
            cmp x17, #1
            b.ne 3f
            stp x29, x30, [sp, #-16]!
            mov x29, sp
            bl rt_str_eq
            ldp x29, x30, [sp], #16
            TOBOOL x0
            ret
        2:  cmp x0, x1
            cset x0, eq
            TOBOOL x0
            ret
        3:  mov x0, #0
            TOBOOL x0
            ret
        rt_ne:
            stp x29, x30, [sp, #-16]!
            mov x29, sp
            bl rt_eq
            ldp x29, x30, [sp], #16
            eor x0, x0, #3
            ret

        .macro MATH1 name, op
        \name:
            NUMARG x0
            fmov d0, x0
            \op d0, d0
            RETD
        .endm
        MATH1 rt_floor, frintm
        MATH1 rt_ceil, frintp
        MATH1 rt_round, frintn
        MATH1 rt_sqrt, fsqrt
        MATH1 rt_abs, fabs
        MATH1 rt_trunc, frintz

        // ============================================================ рядки

        // rt_str_eq(x0, x1 - рядки) -> x0 = 0/1
        rt_str_eq:
            UNBOX x9, x0
            UNBOX x10, x1
            cmp x9, x10
            b.eq 2f
            ldr x11, [x9]
            ldr x12, [x10]
            cmp x11, x12
            b.ne 3f
            add x9, x9, #8
            add x10, x10, #8
        1:  cbz x11, 2f
            ldrb w13, [x9], #1
            ldrb w14, [x10], #1
            cmp w13, w14
            b.ne 3f
            sub x11, x11, #1
            b 1b
        2:  mov x0, #1
            ret
        3:  mov x0, #0
            ret

        // rt_new_str(x0 = довжина в байтах) -> x0 = СИРИЙ вказівник на
        // новий рядок (довжина записана, байти нульові)
        rt_new_str:
            stp x29, x30, [sp, #-32]!
            mov x29, sp
            str x0, [sp, #16]
            add x0, x0, #9
            bl rt_alloc
            ldr x9, [sp, #16]
            str x9, [x0]
            ldp x29, x30, [sp], #32
            ret

        // rt_concat(x0, x1 - рядки) -> новий рядок
        rt_concat:
            stp x29, x30, [sp, #-48]!
            mov x29, sp
            UNBOX x9, x0
            UNBOX x10, x1
            stp x9, x10, [sp, #16]
            ldr x11, [x9]
            ldr x12, [x10]
            add x0, x11, x12
            bl rt_new_str
            ldp x9, x10, [sp, #16]
            ldr x11, [x9]
            ldr x12, [x10]
            add x15, x0, #8
            add x16, x9, #8
        1:  cbz x11, 2f
            ldrb w13, [x16], #1
            strb w13, [x15], #1
            sub x11, x11, #1
            b 1b
        2:  add x16, x10, #8
        3:  cbz x12, 4f
            ldrb w13, [x16], #1
            strb w13, [x15], #1
            sub x12, x12, #1
            b 3b
        4:  movk x0, #0xFFFA, lsl #48
            ldp x29, x30, [sp], #48
            ret

        // rt_to_str(x0) -> рядок: так само, як VM друкує значення
        rt_to_str:
            TAGOF x16, x0
            cmp x16, #6
            b.hs rt_num_to_str
            cmp x16, #1
            b.eq 9f
            cbnz x16, 2f
            and x9, x0, #3
            cmp x9, #1
            b.eq 3f
            cmp x9, #2
            b.eq 4f
            LSTR x0, .Lstr_null
            ret
        3:  LSTR x0, .Lstr_false
            ret
        4:  LSTR x0, .Lstr_true
            ret
        2:  cmp x16, #2
            b.eq 5f
            cmp x16, #3
            b.eq 6f
            cmp x16, #4
            b.eq 7f
            CALLNX1 {STRUCTTOSTR}
        5:  CALLNX1 {ARRTOSTR}
        6:  CALLNX1 {MAPTOSTR}
        7:  LSTR x0, .Lstr_fn
        9:  ret

        // rt_num_to_str(x0 - число): цілі до 10^15 - тут, швидко; дробові
        // й великі - найкоротший точний запис у прелюдії (__numToStrSlow)
        rt_num_to_str:
            fmov d0, x0
            fcmp d0, d0
            b.vs 5f
            fabs d1, d0
            mov x9, #0x7FF0000000000000
            fmov d2, x9
            fcmp d1, d2
            b.eq 6f
            fcmp d0, #0.0
            b.eq 7f
            movz x9, #0x2634, lsl #16
            movk x9, #0x6BF5, lsl #32
            movk x9, #0x430C, lsl #48    // 1e15
            fmov d2, x9
            fcmp d1, d2
            b.ge 8f
            frintz d3, d1
            fcmp d3, d1
            b.ne 8f
            fcvtzu x9, d1
            lsr x13, x0, #63
            mov x10, x9
            mov x11, #0
            mov x12, #10
        1:  udiv x10, x10, x12
            add x11, x11, #1
            cbnz x10, 1b
            add x14, x11, x13
            stp x29, x30, [sp, #-48]!
            mov x29, sp
            stp x9, x13, [sp, #16]
            str x14, [sp, #32]
            mov x0, x14
            bl rt_new_str
            ldp x9, x13, [sp, #16]
            ldr x14, [sp, #32]
            add x15, x0, #8
            cbz x13, 2f
            mov w16, #'-'
            strb w16, [x15]
        2:  add x17, x15, x14
            mov x12, #10
        3:  udiv x10, x9, x12
            msub x16, x10, x12, x9
            add x16, x16, #'0'
            sub x17, x17, #1
            strb w16, [x17]
            mov x9, x10
            cbnz x9, 3b
            movk x0, #0xFFFA, lsl #48
            ldp x29, x30, [sp], #48
            ret
        5:  LSTR x0, .Lstr_nan
            ret
        6:  fcmp d0, #0.0
            b.lt 4f
            LSTR x0, .Lstr_inf
            ret
        4:  LSTR x0, .Lstr_ninf
            ret
        7:  lsr x9, x0, #63
            cbnz x9, 9f
            LSTR x0, .Lstr_zero
            ret
        9:  LSTR x0, .Lstr_nzero
            ret
        8:  CALLNX1 {NUMSLOW}

        // ============================================================ друк і ввід

        rt_print:
            stp x29, x30, [sp, #-16]!
            mov x29, sp
            LNULL x9
            cmp x0, x9
            b.eq 1f
            bl rt_to_str
            UNBOX x1, x0
            ldr x2, [x1]
            add x1, x1, #8
            mov x0, #1
            bl rt_write
        1:  mov x0, #1
            adrp x1, print_newline
            add x1, x1, :lo12:print_newline
            mov x2, #1
            bl rt_write
            ldp x29, x30, [sp], #16
            ret

        rt_print_nonl:
            stp x29, x30, [sp, #-16]!
            mov x29, sp
            LNULL x9
            cmp x0, x9
            b.eq 1f
            bl rt_to_str
            UNBOX x1, x0
            ldr x2, [x1]
            add x1, x1, #8
            mov x0, #1
            bl rt_write
        1:  LNULL x0
            ldp x29, x30, [sp], #16
            ret

        // rt_read_line() -> рядок без '\n' (і без '\r'); наприкінці вводу - ""
        // (як VM). Буфер stdin 4 КБ; рядок росте вдвічі за потреби.
        rt_read_line:
            stp x29, x30, [sp, #-48]!
            mov x29, sp
            mov x0, #256
            str x0, [sp, #24]
            add x0, x0, #16
            bl rt_alloc
            str x0, [sp, #16]
            str xzr, [sp, #32]
        1:  adrp x10, inpos
            add x10, x10, :lo12:inpos
            ldr x11, [x10]
            ldr x12, [x10, #8]
            cmp x11, x12
            b.lo 3f
            ldr x13, [x10, #16]
            cbnz x13, 8f
            mov x0, #0
            adrp x1, inbuf
            add x1, x1, :lo12:inbuf
            mov x2, #4096
            mov x8, #63
            svc #0
            cmp x0, #0
            b.gt 2f
            mov x13, #1
            str x13, [x10, #16]
            b 8f
        2:  str xzr, [x10]
            str x0, [x10, #8]
            b 1b
        3:  adrp x13, inbuf
            add x13, x13, :lo12:inbuf
            ldrb w14, [x13, x11]
            add x11, x11, #1
            str x11, [x10]
            cmp w14, #10
            b.eq 8f
            ldr x15, [sp, #32]
            ldr x9, [sp, #24]
            cmp x15, x9
            b.lo 6f
            str x14, [sp, #40]
            lsl x0, x9, #1
            str x0, [sp, #24]
            add x0, x0, #16
            bl rt_alloc
            ldr x1, [sp, #16]
            ldr x15, [sp, #32]
            add x2, x1, #8
            add x3, x0, #8
            mov x4, #0
        4:  cmp x4, x15
            b.hs 5f
            ldrb w5, [x2, x4]
            strb w5, [x3, x4]
            add x4, x4, #1
            b 4b
        5:  str x0, [sp, #16]
            ldr x14, [sp, #40]
        6:  ldr x0, [sp, #16]
            ldr x15, [sp, #32]
            add x1, x0, #8
            strb w14, [x1, x15]
            add x15, x15, #1
            str x15, [sp, #32]
            b 1b
        8:  ldr x0, [sp, #16]
            ldr x15, [sp, #32]
            add x1, x0, #8
            cbz x15, 9f
            sub x2, x15, #1
            ldrb w3, [x1, x2]
            cmp w3, #13
            b.ne 9f
            mov x15, x2
        9:  str x15, [x0]
            strb wzr, [x1, x15]
            movk x0, #0xFFFA, lsl #48
            ldp x29, x30, [sp], #48
            ret

        rt_i_stdin_eof:
            adrp x10, inpos
            add x10, x10, :lo12:inpos
            ldr x11, [x10]
            ldr x12, [x10, #8]
            ldr x13, [x10, #16]
            cmp x11, x12
            cset x9, hs
            and x0, x9, x13
            TOBOOL x0
            ret

        // ============================================================ системне

        rt_timestamp:
            sub sp, sp, #16
            mov x0, #0
            mov x1, sp
            mov x8, #113
            svc #0
            ldr x9, [sp]
            add sp, sp, #16
            scvtf d0, x9
            RETD

        rt_sleep:
            NUMARG x0
            fmov d0, x0
            fcvtzs x9, d0
            mov x10, #1000
            udiv x11, x9, x10
            msub x12, x11, x10, x9
            mov x13, #16960
            movk x13, #15, lsl #16
            mul x12, x12, x13
            sub sp, sp, #16
            stp x11, x12, [sp]
            mov x0, sp
            mov x1, #0
            mov x8, #101
            svc #0
            add sp, sp, #16
            LNULL x0
            ret

        rt_exit:
            TAGOF x16, x0
            cmp x16, #6
            b.lo 1f
            fmov d0, x0
            fcvtzs w0, d0
            b 2f
        1:  mov x0, #0
        2:  mov x8, #93
            svc #0

        // Код виходу з результату main(): число, true -> 1, решта -> 0
        rt_exit_code:
            TAGOF x16, x0
            cmp x16, #6
            b.hs rt_exit
            mov x9, #2
            movk x9, #0xFFF9, lsl #48
            cmp x0, x9
            cset x0, eq
            mov x8, #93
            svc #0

        rt_i_random32:
            sub sp, sp, #16
            mov x0, sp
            mov x1, #4
            mov x2, #0
            mov x8, #278
            svc #0
            ldr w9, [sp]
            add sp, sp, #16
            ucvtf d0, w9
            RETD

        // ============================================================ масиви

        // rt_arr_new_raw(x0 = місткість, сире ціле) -> порожній масив
        rt_arr_new_raw:
            stp x29, x30, [sp, #-32]!
            mov x29, sp
            mov x9, #4
            cmp x0, x9
            csel x0, x0, x9, hi
            str x0, [sp, #16]
            lsl x0, x0, #3
            bl rt_alloc
            str x0, [sp, #24]
            mov x0, #24
            bl rt_alloc
            ldp x9, x10, [sp, #16]
            str xzr, [x0]
            str x9, [x0, #8]
            str x10, [x0, #16]
            movk x0, #0xFFFB, lsl #48
            ldp x29, x30, [sp], #32
            ret

        // rt_arr_push(x0 = масив, x1 = значення) -> null
        rt_arr_push:
            TAGOF x16, x0
            cmp x16, #2
            b.ne rt_err_notarr
            UNBOX x9, x0
            ldr x10, [x9]
            ldr x11, [x9, #8]
            cmp x10, x11
            b.lo 1f
            stp x29, x30, [sp, #-48]!
            mov x29, sp
            stp x9, x1, [sp, #16]
            lsl x11, x11, #1
            str x11, [sp, #32]
            lsl x0, x11, #3
            bl rt_alloc
            ldp x9, x1, [sp, #16]
            ldr x11, [sp, #32]
            ldr x12, [x9, #16]
            ldr x10, [x9]
            mov x13, #0
        2:  cmp x13, x10
            b.hs 3f
            ldr x14, [x12, x13, lsl #3]
            str x14, [x0, x13, lsl #3]
            add x13, x13, #1
            b 2b
        3:  str x0, [x9, #16]
            str x11, [x9, #8]
            ldp x29, x30, [sp], #48
        1:  ldr x12, [x9, #16]
            str x1, [x12, x10, lsl #3]
            add x10, x10, #1
            str x10, [x9]
            LNULL x0
            ret

        // спільна перевірка для a[i]: x9 = об'єкт масиву, x10 = індекс
        rt_index_check:
            TAGOF x16, x0
            cmp x16, #2
            b.ne rt_err_index_obj
            TAGOF x17, x1
            cmp x17, #6
            b.lo rt_err_index_num
            fmov d0, x1
            fcvtzs x10, d0
            UNBOX x9, x0
            ldr x11, [x9]
            cmp x10, x11
            b.hs rt_err_range
            ret

        rt_index_get:
            mov x15, x30
            bl rt_index_check
            ldr x12, [x9, #16]
            ldr x0, [x12, x10, lsl #3]
            ret x15

        rt_index_set:
            mov x15, x30
            bl rt_index_check
            ldr x12, [x9, #16]
            str x2, [x12, x10, lsl #3]
            mov x0, x2
            ret x15

        // довжина масиву сирим цілим (для for x in масив)
        rt_arr_len_raw:
            TAGOF x16, x0
            cmp x16, #2
            b.ne rt_err_notarr
            UNBOX x9, x0
            ldr x0, [x9]
            ret

        rt_i_arr_len:
            TAGOF x16, x0
            cmp x16, #2
            b.ne rt_err_notarr
            UNBOX x9, x0
            ldr x9, [x9]
            ucvtf d0, x9
            RETD

        // __arrNew(n): масив довжини n, заповнений null
        rt_i_arr_new:
            NUMARG x0
            stp x29, x30, [sp, #-32]!
            mov x29, sp
            fmov d0, x0
            fcvtzu x0, d0
            str x0, [sp, #16]
            bl rt_arr_new_raw
            ldr x9, [sp, #16]
            UNBOX x10, x0
            str x9, [x10]
            ldr x11, [x10, #16]
            LNULL x12
            mov x13, #0
        1:  cmp x13, x9
            b.hs 2f
            str x12, [x11, x13, lsl #3]
            add x13, x13, #1
            b 1b
        2:  ldp x29, x30, [sp], #32
            ret

        rt_i_arr_pop:
            TAGOF x16, x0
            cmp x16, #2
            b.ne rt_err_notarr
            UNBOX x9, x0
            ldr x10, [x9]
            cbz x10, rt_err_pop
            sub x10, x10, #1
            str x10, [x9]
            ldr x12, [x9, #16]
            ldr x0, [x12, x10, lsl #3]
            ret

        rt_i_arr_set_len:
            TAGOF x16, x0
            cmp x16, #2
            b.ne rt_err_notarr
            NUMARG x1
            UNBOX x9, x0
            fmov d0, x1
            fcvtzu x10, d0
            ldr x11, [x9]
            cmp x10, x11
            b.hi rt_err_setlen
            str x10, [x9]
            LNULL x0
            ret

        // ============================================================ мапи

        rt_i_map_new:
            stp x29, x30, [sp, #-32]!
            mov x29, sp
            mov x0, #4
            bl rt_arr_new_raw
            str x0, [sp, #16]
            mov x0, #4
            bl rt_arr_new_raw
            str x0, [sp, #24]
            mov x0, #16
            bl rt_alloc
            ldp x9, x10, [sp, #16]
            stp x9, x10, [x0]
            movk x0, #0xFFFC, lsl #48
            ldp x29, x30, [sp], #32
            ret

        rt_i_map_keys:
            TAGOF x16, x0
            cmp x16, #3
            b.ne rt_err_notmap
            UNBOX x9, x0
            ldr x0, [x9]
            ret

        rt_i_map_vals:
            TAGOF x16, x0
            cmp x16, #3
            b.ne rt_err_notmap
            UNBOX x9, x0
            ldr x0, [x9, #8]
            ret

        // ============================================================ "двері" для прелюдії

        // __tag(v): 0 число, 1 null, 2 bool, 3 рядок, 4 масив, 5 мапа,
        // 6 функція, 7 структура
        rt_i_tag:
            TAGOF x16, x0
            cmp x16, #6
            b.hs 1f
            cbnz x16, 2f
            and x9, x0, #3
            cmp x9, #0
            cset x9, ne
            add x9, x9, #1
            b 3f
        1:  mov x9, #0
            b 3f
        2:  add x9, x16, #2
        3:  ucvtf d0, x9
            RETD

        rt_i_str_len:
            TAGOF x16, x0
            cmp x16, #1
            b.ne rt_err_notstr
            UNBOX x9, x0
            ldr x9, [x9]
            ucvtf d0, x9
            RETD

        // __strByte(s, i) -> байт 0..255
        rt_i_str_byte:
            TAGOF x16, x0
            cmp x16, #1
            b.ne rt_err_notstr
            NUMARG x1
            UNBOX x9, x0
            fmov d0, x1
            fcvtzs x10, d0
            ldr x11, [x9]
            cmp x10, x11
            b.hs rt_err_slice
            add x9, x9, #8
            ldrb w12, [x9, x10]
            ucvtf d0, w12
            RETD

        // __strAlloc(n) -> новий рядок з n нульових байтів
        rt_i_str_alloc:
            NUMARG x0
            stp x29, x30, [sp, #-16]!
            mov x29, sp
            fmov d0, x0
            fcvtzu x0, d0
            bl rt_new_str
            movk x0, #0xFFFA, lsl #48
            ldp x29, x30, [sp], #16
            ret

        rt_i_str_set_byte:
            UNBOX x9, x0
            fmov d0, x1
            fcvtzs x10, d0
            fmov d1, x2
            fcvtzs w11, d1
            add x9, x9, #8
            strb w11, [x9, x10]
            LNULL x0
            ret

        // __strCopy(dst, dstOff, src, srcOff, n)
        rt_i_str_copy:
            UNBOX x9, x0
            add x9, x9, #8
            fmov d0, x1
            fcvtzs x10, d0
            add x9, x9, x10
            UNBOX x11, x2
            add x11, x11, #8
            fmov d0, x3
            fcvtzs x12, d0
            add x11, x11, x12
            fmov d0, x4
            fcvtzs x13, d0
        1:  cmp x13, #0
            b.le 2f
            ldrb w14, [x11], #1
            strb w14, [x9], #1
            sub x13, x13, #1
            b 1b
        2:  LNULL x0
            ret

        // __strSliceBytes(s, from, to) -> новий рядок з байтів [from, to)
        rt_i_str_slice:
            TAGOF x16, x0
            cmp x16, #1
            b.ne rt_err_notstr
            NUMARG x1
            NUMARG x2
            UNBOX x9, x0
            fmov d0, x1
            fcvtzs x10, d0
            fmov d0, x2
            fcvtzs x11, d0
            ldr x12, [x9]
            cmp x10, #0
            b.lt rt_err_slice
            cmp x11, x12
            b.gt rt_err_slice
            cmp x10, x11
            b.gt rt_err_slice
            stp x29, x30, [sp, #-32]!
            mov x29, sp
            stp x9, x10, [sp, #16]
            sub x0, x11, x10
            bl rt_new_str
            ldp x9, x10, [sp, #16]
            ldr x13, [x0]
            add x9, x9, #8
            add x9, x9, x10
            add x14, x0, #8
        1:  cbz x13, 2f
            ldrb w15, [x9], #1
            strb w15, [x14], #1
            sub x13, x13, #1
            b 1b
        2:  movk x0, #0xFFFA, lsl #48
            ldp x29, x30, [sp], #32
            ret

        // __strFindBytes(s, sub, from) -> байтовий індекс першого входження або -1
        rt_i_str_find:
            TAGOF x16, x0
            cmp x16, #1
            b.ne rt_err_notstr
            TAGOF x16, x1
            cmp x16, #1
            b.ne rt_err_notstr
            UNBOX x9, x0
            UNBOX x10, x1
            fmov d0, x2
            fcvtzs x11, d0
            ldr x12, [x9]
            ldr x13, [x10]
            add x9, x9, #8
            add x10, x10, #8
            sub x14, x12, x13
        1:  cmp x11, x14
            b.gt 4f
            mov x15, #0
        2:  cmp x15, x13
            b.hs 3f
            add x16, x11, x15
            ldrb w16, [x9, x16]
            ldrb w17, [x10, x15]
            cmp w16, w17
            b.ne 5f
            add x15, x15, #1
            b 2b
        3:  scvtf d0, x11
            RETD
        5:  add x11, x11, #1
            b 1b
        4:  fmov d0, #-1.0
            RETD

        rt_i_dbl_exp:
            ubfx x9, x0, #52, #11
            ucvtf d0, x9
            RETD
        rt_i_dbl_hi:
            ubfx x9, x0, #32, #20
            ucvtf d0, x9
            RETD
        rt_i_dbl_lo:
            mov w9, w0
            ucvtf d0, x9
            RETD
        rt_i_dbl_sign:
            lsr x9, x0, #63
            ucvtf d0, x9
            RETD

        // __write(fd, s) - сирий вивід рядка (без '\n')
        rt_i_write:
            TAGOF x16, x1
            cmp x16, #1
            b.ne rt_err_notstr
            stp x29, x30, [sp, #-16]!
            mov x29, sp
            fmov d0, x0
            fcvtzs x0, d0
            UNBOX x1, x1
            ldr x2, [x1]
            add x1, x1, #8
            bl rt_write
            LNULL x0
            ldp x29, x30, [sp], #16
            ret

        rt_i_struct_name:
            TAGOF x16, x0
            cmp x16, #5
            b.ne rt_err_notstruct
            UNBOX x9, x0
            ldr x10, [x9]
            adrp x11, .Lstructnames
            add x11, x11, :lo12:.Lstructnames
            ldr x0, [x11, x10, lsl #3]
            movk x0, #0xFFFA, lsl #48
            ret
        """;
}
