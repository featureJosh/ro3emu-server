#!/usr/bin/env bash
# Worktrees for parallel agents. Each one gets branch wf/<slug> under $RO3_WT (default ~/Documents/RO3-wt), with the
# untracked data/ and re/ linked from this checkout so builds and tests work (junctions on Windows/Git Bash).
#   wt.sh new <slug> [base]   create (or reuse) and print the path; base defaults to main
#   wt.sh rm <slug>           remove the worktree and delete its branch
#   wt.sh ls                  list worktrees
set -eu
repo="$(cd "$(dirname "$(readlink -f "$0")")/../../.." && pwd)"; wt="${RO3_WT:-$HOME/Documents/RO3-wt}"
cd "$repo"
win() { case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) return 0 ;; *) return 1 ;; esac; }
link() { # Git Bash's ln -s copies without Developer Mode; a junction needs no rights
  if win; then powershell -NoProfile -Command "New-Item -ItemType Junction -Path '$(cygpath -w "$2")' -Target '$(cygpath -w "$1")' | Out-Null"
  else ln -s "$1" "$2"; fi
}
unlink_data() { # drop the links first so removing the worktree can never reach the real data/ and re/
  for x in data re; do
    [ -e "$1/$x" ] || continue
    if win; then powershell -NoProfile -Command "\$p='$(cygpath -w "$1/$x")'; if ((Get-Item -Force \$p).LinkType) { [IO.Directory]::Delete(\$p) }"
    elif [ -L "$1/$x" ]; then rm "$1/$x"; fi
  done
}
case "${1:-}" in
  new)
    d="$wt/$2"
    if [ ! -d "$d" ]; then
      mkdir -p "$wt"
      git worktree add -q "$d" -b "wf/$2" "${3:-main}" >&2
      for x in data re; do [ -e "$repo/$x" ] && link "$repo/$x" "$d/$x"; done
    fi
    echo "$d" ;;
  rm)
    if [ -d "$wt/$2" ]; then unlink_data "$wt/$2"; git worktree remove --force "$wt/$2"; fi
    git branch -D "wf/$2" 2>/dev/null || true ;;
  ls) git worktree list ;;
  *) sed -n '2,6p' "$0"; exit 1 ;;
esac
