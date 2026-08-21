#!/bin/sh
# 把提交历史里 Claude 署的名改成你自己的，并去掉 Claude 的署名尾注与私有会话链接。
#
# 用法：在你自己仓库的根目录下跑   sh rewrite-author.sh
# 可以重复跑：认的是「旧身份的邮箱」，已经改好的不会再动。
# 只改元数据，一个字节的代码都不动。
set -e

NAME="To-lovely-April-days"
MAIL="it1925184085@163.com"

# 只改这几个旧身份写的提交，别人的一律不碰。
# 按邮箱认，不按名字——名字大小写变过（Claude / claude）。
OLD_MAILS="noreply@anthropic.com asimeniosfozia173@gmail.com To-lovely-April-days@users.noreply.github.com"

# ── 跑之前的自检 ──────────────────────────────────────────────
git rev-parse --git-dir >/dev/null 2>&1 || {
  echo "✗ 这儿不是 git 仓库。cd 到你仓库的根目录再跑。"; exit 1; }

# 只看**已跟踪文件**的改动。未跟踪文件不拦：这个脚本自己就躺在仓库根目录、
# 正好是未跟踪的，连它一起算就等于这道闸永远拦着自己（第一版就是这么写错的）
[ -z "$(git status --porcelain --untracked-files=no)" ] || {
  echo "✗ 有已跟踪文件被改动 / 删除还没提交，先处理掉再跑。"
  echo "  （重写历史会重建每一笔提交，带着这些改动跑容易把它们弄丢）"
  echo
  git status --short --untracked-files=no
  echo
  echo "  要么提交： git add -A && git commit -m \"说明\""
  echo "  要么丢弃： git checkout -- .        # 恢复成上次提交的样子"
  exit 1; }

echo "改之前的作者分布："
git log --format='%an <%ae>' --branches --tags | sort | uniq -c | sort -rn
echo
echo "要改成： $NAME <$MAIL>"
echo "回车继续，Ctrl+C 放弃。"
read _ignored

# ── 开改 ─────────────────────────────────────────────────────
# 消息过滤器只用 sed，不用 perl —— Git for Windows 里 perl 不一定有。
# 最后那段 sed 是删掉结尾多余空行的老写法。
FILTER_BRANCH_SQUELCH_WARNING=1 git filter-branch -f \
  --env-filter '
    for m in '"$OLD_MAILS"'; do
      [ "$GIT_AUTHOR_EMAIL" = "$m" ] && {
        export GIT_AUTHOR_NAME="'"$NAME"'"; export GIT_AUTHOR_EMAIL="'"$MAIL"'"; }
      [ "$GIT_COMMITTER_EMAIL" = "$m" ] && {
        export GIT_COMMITTER_NAME="'"$NAME"'"; export GIT_COMMITTER_EMAIL="'"$MAIL"'"; }
    done
    true
  ' \
  --msg-filter '
    sed -e "/^Co-Authored-By: Claude/d" \
        -e "/^Claude-Session: /d" \
        -e "/^🤖 Generated with \[Claude Code\]/d" \
        -e "/^https:\/\/claude\.ai\/code\/session_/d" \
    | sed -e :a -e "/^\n*\$/{\$d;N;};/\n\$/ba"
  ' \
  -- --branches --tags

# ── 改完之后 ─────────────────────────────────────────────────
git config user.name  "$NAME"
git config user.email "$MAIL"

echo
echo "✓ 改完了。现在的作者分布："
git log --format='%an <%ae>' --branches --tags | sort | uniq -c | sort -rn
echo
echo "以后这个仓库里的新提交也会用 $NAME <$MAIL>。"
echo
echo "确认没问题再推。后悔的话，把分支退回改之前："
git for-each-ref refs/original/ --format='  git reset --hard %(objectname)   # 原 %(refname)'
