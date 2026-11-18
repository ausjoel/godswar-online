# RANK-DISPLAY (2026-11-18): show a lower armour / weapon rank effect than the gear earns, chosen per character with
# the server's /rank command. GodsWar.exe 1.5.1 (Origin layout). Three stubs at CAVE (.rdata slack, executable).
# Per-player fields (unused by the client: no instruction touches +0x9a1..+0x9a4):
#   +0x9a1 armour cap, +0x9a2 weapon cap (255 = none, 0 = no effect), +0x9a3 word 'RK' = caps valid.
# Hooks (each a 5-byte jmp; replaced bytes are re-executed in the stub):
#   0x4a7168 weapon rank (FUN_4a7010, esi = player, ebp = score) -> wpn
#   0x4a7615 armour rank (FUN_4a7370, ebx = player, [esp+0xc] = score) -> arm
#   0x4eccd9 10199 _MSG_CD_INFO handler (edi+4 = packet, esi = player) -> ttl
#   0x4eccc0 je (player not found) retargeted -> miss: own avatar (main player id == packet id) gets its caps too
# Clamp = score <= threshold[cap-1], so the existing loop picks rank <= cap and its own effect id.
.intel_syntax noprefix
.text
.set CAVE, 0x9C3500
.macro JMPA target
    .byte 0xe9
    .long \target - CAVE - (. - start) - 4
.endm
.macro CALLA target
    .byte 0xe8
    .long \target - CAVE - (. - start) - 4
.endm
start:
wpn:                                    # CAVE + 0
    mov ebp, eax
    xor edi, edi
    cmp word ptr [esi+0x9a3], 0x4B52
    jne wdone
    movzx ecx, byte ptr [esi+0x9a2]
    cmp ecx, 255
    je wdone
    test ecx, ecx
    jnz wcap
    or ebp, -1
    jmp wdone
wcap:
    mov eax, [esi+0x733c]
    mov edx, [eax+0x340]
    test edx, edx
    jz wdone
    mov eax, [eax+0x344]
    sub eax, edx
    sar eax, 2
    cmp ecx, eax
    ja wdone
    mov edx, [edx+ecx*4-4]
    test edx, edx
    js wdone
    cmp ebp, edx
    jle wdone
    mov ebp, edx
wdone:
    JMPA 0x4a7170
.p2align 4
arm:
    xor edi, edi
    cmp word ptr [ebx+0x9a3], 0x4B52
    jne adone
    movzx ecx, byte ptr [ebx+0x9a1]
    cmp ecx, 255
    je adone
    test ecx, ecx
    jnz acap
    mov dword ptr [esp+0xc], -1
    jmp adone
acap:
    mov eax, [ebx+0x6c74]
    mov edx, [eax+0x360]
    test edx, edx
    jz adone
    mov eax, [eax+0x364]
    sub eax, edx
    sar eax, 2
    cmp ecx, eax
    ja adone
    mov edx, [edx+ecx*4-4]
    test edx, edx
    js adone
    cmp [esp+0xc], edx
    jle adone
    mov [esp+0xc], edx
adone:
    JMPA 0x4a7620
.p2align 4
ttl:                                    # CAVE + 0xC0: other player found (esi = player)
    mov eax, [edi+0x50]
    mov [esi+0x99c], eax
    call setcaps
    JMPA 0x4ecce2
.p2align 4
miss:                                   # lookup failed: the stock client ignores 10199 for its own avatar
    mov esi, [0x1575eac]
    test esi, esi
    jz mout
    movzx eax, word ptr [edi+4]
    cmp eax, 92
    jb mout
    cmp dword ptr [edi+0x54], 0x4B525747
    jne mout
    mov eax, [esi+0x7fa4]               # own character id (10021 stores dbid here too)
    cmp eax, [edi+0x5c]                 # packet +88: character id in the GWRK tail
    jne mout
    call setcaps
mout:
    xor esi, esi
    xor eax, eax
    JMPA 0x4ec995
.p2align 4
setcaps:                                # esi = player, edi+4 = packet; keeps all registers
    push eax
    movzx eax, word ptr [edi+4]
    cmp eax, 88
    jb sclear
    cmp dword ptr [edi+0x54], 0x4B525747
    jne sclear
    mov al, [edi+0x58]
    mov [esi+0x9a1], al
    mov al, [edi+0x59]
    mov [esi+0x9a2], al
    mov word ptr [esi+0x9a3], 0x4B52
    pushad
    push 1
    CALLA 0x4a7010
    mov ebx, esi
    push 1
    CALLA 0x4a7370
    popad
    pop eax
    ret
sclear:
    mov word ptr [esi+0x9a3], 0
    pop eax
    ret
