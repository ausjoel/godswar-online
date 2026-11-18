#!/bin/bash
# Builds the patched unicorn 2.1.4 (x86 only, static) used by gwhost -> third_party/lib/libunicorn-gw.a.
# Patches (grep "gwhost patch"): qemu/include/exec/ram_addr.h (no SMC dirty tracking on stores) and
# qemu/tcg/tcg-op.c (no exit-check helper call after every guest load/store) and qemu/include/exec/gen-icount.h
# (inline icount_decr test at TB start instead of a helper call per TB) and qemu/target/i386/helper.h
# (register-only x87 helpers flagged TCG_CALL_NO_RWG) and target/i386/fpu_helper.c (fast x87 conversions and
# add/sub/mul/div in host double when precision control is 53/24 bit; on by default in gwhost; GW_X87FAST=0 disables).
# Also: gw_uc_async_stop in uc.c (thread-safe stop for the preemption watchdog), bigger softmmu TLB (include/exec/cpu-defs.h) and jump cache (include/hw/core/cpu.h TB_JMP_CACHE_BITS 14).
# cmake's generated config breaks on paths with spaces, so the build runs in a temp dir.
set -e
here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d /tmp/gw-uc.XXXXXX)"
cp -R "$here/unicorn-2.1.4" "$tmp/src"; mkdir "$tmp/build"; cd "$tmp/build"
nice -n 15 cmake ../src -G Ninja -DCMAKE_BUILD_TYPE=Release -DUNICORN_ARCH=x86 -DUNICORN_BUILD_TESTS=OFF \
  -DBUILD_SHARED_LIBS=OFF -DUNICORN_LEGACY_STATIC_ARCHIVE=ON >/dev/null
nice -n 15 ninja >/dev/null
mkdir -p "$here/lib"; cp libunicorn.a "$here/lib/libunicorn-gw.a"; rm -rf "$tmp"; echo "ok: $here/lib/libunicorn-gw.a"
