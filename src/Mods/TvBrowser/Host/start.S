/* tvbrowser-host's program entry, in place of the C library's Scrt1.o. Proton 8-10 run the game in Steam's "sniper"
   runtime (glibc 2.31), and a program linked here would otherwise need __libc_start_main from glibc 2.34: this one
   binds the old version, and passes tvb_init (start_init.c) for the constructors, which glibc before 2.34 expects
   from the program itself (2.34 and later run whatever init they are given). */
        .symver __libc_start_main, __libc_start_main@GLIBC_2.2.5
        .text
        .globl _start
        .type _start, @function
_start:
        xor %ebp, %ebp
        mov %rdx, %r9                     /* rtld_fini */
        pop %rsi                          /* argc */
        mov %rsp, %rdx                    /* argv */
        and $-16, %rsp
        push %rax
        push %rsp                         /* stack_end */
        lea tvb_init(%rip), %rcx          /* init */
        xor %r8d, %r8d                    /* fini: destructors run from the dynamic section */
        mov main@GOTPCREL(%rip), %rdi
        call *__libc_start_main@GOTPCREL(%rip)
        hlt
        .size _start, . - _start
        .section .note.GNU-stack, "", @progbits
