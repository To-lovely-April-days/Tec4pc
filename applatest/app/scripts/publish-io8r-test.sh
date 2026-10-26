#!/bin/bash
# IO8R 继电器测试工具出包。用法：scripts/publish-io8r-test.sh [RID]
#   RID 常用：win-x64（现场那几台 Windows 电脑）/ linux-x64 / linux-arm64
# 产物在 dist/tec-io8r-test-<RID>/：单文件 Tec.Io8rTest(.exe)，拷整个目录到别的电脑就能跑，不用装 .NET。
set -e
cd "$(dirname "$0")/.."
RID=${1:-win-x64}
OUT=dist/tec-io8r-test-$RID

# **绝不 PublishTrimmed**：Avalonia 的反射绑定会被裁剪弄坏（跟 publish-hmi.sh 同一条规矩）
dotnet publish src/Tec.Io8rTest -c Release -r "$RID" --self-contained \
    /p:PublishSingleFile=true \
    /p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$OUT"

cp src/Tec.Io8rTest/README-使用.md "$OUT/"

if command -v zip >/dev/null 2>&1; then
    ZIP=dist/Tec.Io8rTest-$RID.zip
    rm -f "$ZIP"
    (cd "$OUT" && zip -q -9 -r "../../$ZIP" .)
    echo "zip：$ZIP"
fi

echo
echo "出包完成：$OUT"
echo "整个目录拷到目标电脑，双击 Tec.Io8rTest.exe；用法见目录里的 README-使用.md。"
