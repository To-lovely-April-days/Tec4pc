#!/bin/bash
# 控温极限测试工具出包。用法：scripts/publish-limit-test.sh [RID] [版本号]
#   RID 常用：win-x64（现场那几台 Windows 电脑）/ linux-x64 / linux-arm64
#   版本号：写进程序标题和记录表「补丁版本」缺省值，一般填打到的补丁号，如 0323
# 产物在 dist/tec-limit-test-<RID>/：单文件 Tec.LimitTest(.exe)，拷整个目录到别的电脑就能跑，不用装 .NET。
# 装机步骤见 src/Tec.LimitTest/README-使用.md「装到别的电脑」。
set -e
cd "$(dirname "$0")/.."
RID=${1:-win-x64}
VER=${2:-0325}
OUT=dist/tec-limit-test-$RID

# **绝不 PublishTrimmed**：Avalonia 的反射绑定会被裁剪弄坏（跟 publish-hmi.sh 同一条规矩）。
# Version 只认数字点号，所以 1.0.<补丁号>；InformationalVersion 就是补丁号本身，界面上显示它
dotnet publish src/Tec.LimitTest -c Release -r "$RID" --self-contained \
    /p:PublishSingleFile=true \
    /p:IncludeNativeLibrariesForSelfExtract=true \
    /p:Version=1.0.$((10#$VER)) \
    /p:InformationalVersion="$VER" \
    -o "$OUT"

# 台面 / 说明一起带上，装机的人不用再翻仓库
cp src/Tec.LimitTest/README-使用.md "$OUT/"

# 顺手打成 zip，U 盘 / 微信直接传（机器上没有 zip 就跳过，目录照样能用）
if command -v zip >/dev/null 2>&1; then
    ZIP=dist/Tec.LimitTest-$RID-$VER.zip
    rm -f "$ZIP"
    (cd "$OUT" && zip -q -9 -r "../../$ZIP" .)
    echo "zip：$ZIP"
fi

echo
echo "出包完成：$OUT（版本 $VER）"
echo "整个目录拷到目标电脑，双击 Tec.LimitTest.exe；步骤见目录里的 README-使用.md。"
