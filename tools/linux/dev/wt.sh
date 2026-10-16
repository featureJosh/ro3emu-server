#!/usr/bin/env bash
# Worktrees for parallel agents. Each one gets branch wf/<slug> under $RO3_WT (default ~/Documents/RO3-wt), with the
# untracked data/ and re/ symlinked from this checkout so builds and tests work.
#   wt.sh new <slug> [base]   create (or reuse) and print the path; base defaults to linux-asia-offline
#   wt.sh rm <slug>           remove the worktree and delete its branch
#   wt.sh ls                  list worktrees
set -eu
repo="$(cd "$(dirname "$(readlink -f "$0")")/../../.." && pwd)"; wt="${RO3_WT:-$HOME/Documents/RO3-wt}"
cd "$repo"
case "${1:-}" in
  new)
    d="$wt/$2"
    if [ ! -d "$d" ]; then
      mkdir -p "$wt"
      git worktree add -q "$d" -b "wf/$2" "${3:-linux-asia-offline}" >&2
      for x in data re; do [ -e "$repo/$x" ] && ln -s "$repo/$x" "$d/$x"; done
    fi
    echo "$d" ;;
  rm)
    [ -d "$wt/$2" ] && git worktree remove --force "$wt/$2"
    git branch -D "wf/$2" 2>/dev/null || true ;;
  ls) git worktree list ;;
  *) sed -n '2,6p' "$0"; exit 1 ;;
esac
