# Replacement for GodsWar.exe 0x7760d0 (AABB vs 6-plane frustum; original x87, /Od). thiscall: ecx = 6 planes
# {nx,ny,nz,d} at 16-byte stride, [esp+4] = node (corner A = node+0, corner B = node+0xc, translation t = node+0x24).
# Returns 0 = outside (some plane has the near corner in front), 1 = intersects, 2 = inside; ret 4.
# Bit-identical to the x87 original: the precision-control bits of the x87 control word pick single (PC=24, what
# Direct3D sets under Windows/Wine) or double (PC=53, the emulator) arithmetic, same operation order, same rounding.
# The original's function-local statics (0xb3b090..0xb3b0d8) are private to it (only its atexit/unwind stubs refer
# to them) and are not written. Clobbers eax, ecx, edx, xmm0-7 (all caller-saved), keeps ebx.
.intel_syntax noprefix
.text
.globl cull
cull:
    push ebx
    mov edx, [esp+8]
    sub esp, 28                 # [esp]=A.xyz [esp+12]=B.xyz [esp+24]=fpu cw
    fnstcw [esp+24]
    movzx eax, word ptr [esp+24]
    and eax, 0x300              # 0 = 24-bit precision
    movss xmm0, [edx];      addss xmm0, [edx+0x24]; movss [esp], xmm0
    movss xmm0, [edx+4];    addss xmm0, [edx+0x28]; movss [esp+4], xmm0
    movss xmm0, [edx+8];    addss xmm0, [edx+0x2c]; movss [esp+8], xmm0
    movss xmm0, [edx+0xc];  addss xmm0, [edx+0x24]; movss [esp+12], xmm0
    movss xmm0, [edx+0x10]; addss xmm0, [edx+0x28]; movss [esp+16], xmm0
    movss xmm0, [edx+0x14]; addss xmm0, [edx+0x2c]; movss [esp+20], xmm0
    xor edx, edx                # flag
    mov ebx, 6
    xorps xmm7, xmm7
plane:
    # per axis: n >= 0 -> near (d1) uses B, far (d2) uses A; n < 0 or NaN -> swapped (x87 fcomp sets C0)
    ucomiss xmm7, [ecx]
    movss xmm1, [esp+12]
    movss xmm2, [esp]
    jbe 1f                      # 0 <= nx  (ZF/CF from 0 vs nx: CF=1 if 0<nx, ZF=1 if equal; NaN -> ZF=PF=CF=1)
    jmp 2f
1:  jp 2f                       # unordered: treat as negative
    jmp 3f
2:  movss xmm1, [esp]
    movss xmm2, [esp+12]
3:  ucomiss xmm7, [ecx+4]
    movss xmm3, [esp+16]
    movss xmm4, [esp+4]
    jbe 1f
    jmp 2f
1:  jp 2f
    jmp 3f
2:  movss xmm3, [esp+4]
    movss xmm4, [esp+16]
3:  ucomiss xmm7, [ecx+8]
    movss xmm5, [esp+20]
    movss xmm6, [esp+8]
    jbe 1f
    jmp 2f
1:  jp 2f
    jmp 3f
2:  movss xmm5, [esp+8]
    movss xmm6, [esp+20]
3:  test eax, eax
    jnz dbl
    # single precision (PC = 24 bit)
    mulss xmm1, [ecx];   mulss xmm3, [ecx+4]; addss xmm1, xmm3; mulss xmm5, [ecx+8]; addss xmm1, xmm5; addss xmm1, [ecx+12]
    mulss xmm2, [ecx];   mulss xmm4, [ecx+4]; addss xmm2, xmm4; mulss xmm6, [ecx+8]; addss xmm2, xmm6; addss xmm2, [ecx+12]
    jmp test
dbl:
    cvtss2sd xmm0, [ecx]
    cvtss2sd xmm1, xmm1; mulsd xmm1, xmm0
    cvtss2sd xmm2, xmm2; mulsd xmm2, xmm0
    cvtss2sd xmm0, [ecx+4]
    cvtss2sd xmm3, xmm3; mulsd xmm3, xmm0; addsd xmm1, xmm3
    cvtss2sd xmm4, xmm4; mulsd xmm4, xmm0; addsd xmm2, xmm4
    cvtss2sd xmm0, [ecx+8]
    cvtss2sd xmm5, xmm5; mulsd xmm5, xmm0; addsd xmm1, xmm5
    cvtss2sd xmm6, xmm6; mulsd xmm6, xmm0; addsd xmm2, xmm6
    cvtss2sd xmm0, [ecx+12]
    addsd xmm1, xmm0; addsd xmm2, xmm0
    cvtsd2ss xmm1, xmm1; cvtsd2ss xmm2, xmm2
test:
    ucomiss xmm2, xmm7          # far corner in front of the plane -> outside
    ja outside
    ucomiss xmm1, xmm7
    jbe 4f
    mov edx, 1
4:  add ecx, 16
    dec ebx
    jnz plane
    mov eax, 2
    sub eax, edx
    jmp done
outside:
    xor eax, eax
done:
    add esp, 28
    pop ebx
    ret 4
