# Git commit (this conversation only)

Perform a git commit that includes **only** changes that belong to **this** chat: files you created or edited while working on the user’s current task, or paths the user explicitly named as in scope. Treat everything else as out of scope.

## Before staging

1. Run `git status` (short format is fine) and inspect the working tree.
2. Build an explicit list of **conversation-scoped** paths. If anything is ambiguous, **stop and ask** the user which paths to include—do not guess by staging broad patterns.

## Staging rules

- Use **explicit paths only**: `git add -- <path> [<path> ...]`.
- **Do not** use `git add -A`, `git add .`, or `git commit -a` unless the user clearly states that **all** current changes are part of this conversation.
- **Do not** commit unrelated modified files, IDE noise, or accidental edits—even if they show up in `git status`.

## Commit message

- One short subject line (imperative mood), focused on what **this** conversation delivered.
- Add a body only if it clarifies scope or links several related edits.

## After committing

1. Show `git log -1 --oneline` (or equivalent) so the user sees the new commit.
2. Show `git status -sb` so they see what remains unstaged or untracked.

If there is nothing in scope to commit, say so and list what `git status` shows without committing.
