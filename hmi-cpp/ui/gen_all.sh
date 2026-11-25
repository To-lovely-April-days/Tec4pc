#!/bin/sh
# 把 ui/design 里的设计稿全部重新翻成界面代码（两种缩放各一份）。加了新页面就在下面这行加名字。
#   ./gen_all.sh <出图工具 shot 的路径>
set -e
cd "$(dirname "$0")/.."
PAGES="Ctl Ctl_2 ChTrend ChTrend_2 Safety Safety_2 Export Export_2 Pid Pid_2 Sys Comm CommDlg CommScan CommScanRun ExitDlg LangDlg AlarmDlg AlarmListDlg NoLink Files Main Scheme Trends KeypadDlg TextDlg Seq Seq_2 StepDlg SeqTempPart SeqRefluxPart SeqWaitPart SeqStirPart SeqDosePart SeqPhPart SeqCryPart SeqPausePart CryViewPart CryEditPart"
python3 ui/tools/gen.py ui/design "$1" 2 $PAGES
python3 ui/tools/gen.py ui/design "$1" 1 $PAGES > /dev/null
