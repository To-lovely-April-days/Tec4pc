#!/bin/sh
# 试做用的最简构建：不用 cmake，直接把 LVGL 的 .c 编成一个静态库，再链上试做程序。库只在没编过时才编。
set -e
cd "$(dirname "$0")"
L=../third_party/lvgl-9.2.2
if [ ! -f build/liblvgl.a ]; then
  mkdir -p build/obj
  find $L/src -name '*.c' | while read f; do o=build/obj/$(echo $f | sed 's|[/.]|_|g').o; echo "$f $o"; done > build/list.txt
  cat build/list.txt | xargs -P 8 -L 1 sh -c 'clang -c -O2 -w -DLV_CONF_INCLUDE_SIMPLE -I. -I'$L' "$0" -o "$1"'
  ar rcs build/liblvgl.a build/obj/*.o
fi
PAGE=${1:-Ctl}; SC=${2:-1}
clang -O2 -Wall -Wno-unused-function -DLV_CONF_INCLUDE_SIMPLE -DSCALE=$SC -DPAGE_INC="\"build/${PAGE}_${SC}.inc\"" -I. -I$L main.c build/liblvgl.a -lm -o build/trial_${PAGE}_${SC}
