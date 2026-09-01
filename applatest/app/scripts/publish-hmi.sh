#!/bin/bash
# 设备端 HMI 出包。用法：scripts/publish-hmi.sh [RID]
#   RID 常用：linux-x64（工控机）/ linux-arm64（ARM 面板机）/ win-x64
# 产物在 dist/tec-hmi-<RID>/：单文件 Tec.Hmi(.exe) + drivers/ 空目录。
# 部署步骤见 src/Tec.Hmi/README-部署.md。
set -e
cd "$(dirname "$0")/.."
RID=${1:-linux-x64}
OUT=dist/tec-hmi-$RID

# **绝不 PublishTrimmed**：Avalonia 的反射绑定和 drivers/ 目录的运行时
# LoadFrom 都会被裁剪弄坏——省那点体积的代价是现场莫名其妙的空白控件。
dotnet publish src/Tec.Hmi -c Release -r "$RID" --self-contained \
    /p:PublishSingleFile=true \
    /p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$OUT"

# 第三方驱动包散装在 exe 旁边的 drivers/（与工作站同一套规矩）
mkdir -p "$OUT/drivers"

echo
echo "出包完成：$OUT"
echo "拷到设备后先看 src/Tec.Hmi/README-部署.md 的部署清单。"
