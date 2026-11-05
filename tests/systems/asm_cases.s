add $1000, %rax
add $5, %al
and $-16, %rsp
cmp $300, %eax
sub $70000, %rax
xor $1, %al
cmp $0x7f, %al
or $1000, %ax
test $1, %al
test $0x10000, %rax
test $5, %ecx
shr $1, %rcx
sar $1, %eax
shl $3, %r11
shl %cl, %rax
mov %r12b, (%r13)
mov %sil, %al
movb %dil, 3(%rsp)
mov -8(%rbp), %r9
mov %r8, (%r12,%r13,8)
mov (%rsp,%rax,4), %ecx
mov 0x100(%rbp), %rax
movzbq (%rax), %rcx
movzbq %sil, %rax
movsbq %r10b, %r11
movswq 2(%r8), %rdx
movzwq %ax, %rax
movslq (%rdi), %r14
lea 16(%rsp), %rdi
lea (%rax,%rbx,2), %r8
lock xadd %rax, (%rdi)
lock cmpxchg %rcx, (%r12)
lock cmpxchgl %ecx, 4(%rdx)
xchgl %eax, (%rdi)
imul %rcx, %rax
imul $12, %rdx, %rax
imul $1000, %r9, %r10
idiv %rcx
div %r8
neg %rax
not %r15
movq %xmm1, %r8
movq %r9, %xmm14
movq %xmm0, %xmm1
movd %eax, %xmm0
movd %xmm2, %ecx
movsd (%rsp), %xmm8
movsd %xmm9, -16(%rbp)
movss %xmm0, (%rax)
addsd %xmm1, %xmm0
divss %xmm10, %xmm2
cvtsi2sdq %rax, %xmm0
cvtsi2ssq %r9, %xmm3
cvtsi2sd %eax, %xmm1
cvttsd2si %xmm0, %rax
cvttsd2si %xmm0, %eax
cvtsd2ss %xmm0, %xmm0
cvtss2sd %xmm12, %xmm1
ucomisd %xmm1, %xmm0
ucomiss %xmm9, %xmm0
pxor %xmm15, %xmm15
movapd %xmm1, %xmm11
sete %al
setnp %r9b
setae %sil
btc $63, %rax
fldl (%rsp)
flds 8(%rsp)
fstpl (%rsp)
fstps (%rsp)
fstp %st(1)
fnstsw %ax
fprem
movl $5, (%rax)
movw $7, 2(%rax)
movb $1, (%r9)
movq $-1, 8(%rsp)
mov $5, %r10d
mov $-5, %rax
movabs $0x123456789, %r11
movabs $1, %rax
push %r12
pop %rbx
call *%r11
jmp *%rdx
mov %fs:0, %rax
rep movsb
rep stosb
syscall
cqo
leave
ret
