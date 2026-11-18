#!/bin/bash
# Build gwhost (arm64 macOS). Default: links the patched static unicorn (third_party/build-unicorn.sh).
# GW_BREW_UNICORN=1 ./build.sh links Homebrew's stock libunicorn instead (slow store path; for A/B).
cd "$(dirname "$0")"
mkdir -p build
if [ -z "$GW_BREW_UNICORN" ] && [ -f third_party/lib/libunicorn-gw.a ]; then UC="third_party/lib/libunicorn-gw.a"; DEF=-DGW_PATCHED_UC=1
else UC="-L/opt/homebrew/lib -lunicorn"; DEF=; fi
exec nice -n 15 clang -O2 -g -Wall -Wno-unused-function -Wno-unused-variable -Wno-misleading-indentation -Wno-unused-but-set-variable -Wno-deprecated-declarations \
  $DEF -I/opt/homebrew/include -Ithird_party/unicorn-2.1.4/include host/*.c $UC -lpthread -liconv -lz -framework AudioToolbox -framework CoreAudio -o build/gwhost "$@"
