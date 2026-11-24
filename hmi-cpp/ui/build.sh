#!/bin/sh
# 这台 Mac 上编界面程序用（不开窗口、只出图的版本）。正式的 Windows 版走根目录的 CMake。
#   ./build.sh [缩放 1|2]
set -e
cd "$(dirname "$0")"
L=../third_party/lvgl-9.2.2
SC=${1:-1}
# 改了 lv_conf.h 要把 LVGL 整个重编一遍，不然用的还是旧配置编出来的库（踩过：改了配置以为生效了，其实没有）
if [ build/liblvgl.a -ot lv_conf.h ] 2>/dev/null; then rm -f build/liblvgl.a; fi
if [ ! -f build/liblvgl.a ]; then
  mkdir -p build/obj
  find $L/src -name '*.c' | while read f; do o=build/obj/$(echo $f | sed 's|[/.]|_|g').o; echo "$f $o"; done > build/list.txt
  cat build/list.txt | xargs -P 8 -L 1 sh -c 'clang -c -O2 -w -DLV_CONF_INCLUDE_SIMPLE -I. -I'$L' "$0" -o "$1"'
  ar rcs build/liblvgl.a build/obj/*.o
fi
clang++ -std=c++17 -O2 -Wall -Wno-unused-function -DLV_CONF_INCLUDE_SIMPLE -DUI_SCALE=$SC -DPAGES_INC="\"../gen/pages_${SC}.inc\"" -I. -I$L \
  src/main.cpp src/ui_kit.cpp ../net/tcp_server.cpp build/liblvgl.a -lm -o build/tec_hmi_$SC
