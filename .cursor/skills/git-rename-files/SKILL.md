---
name: git-rename-files
description: >-
  Renames or moves files with Git so history is preserved, then patches content on the new path.
  Use when renaming or moving a file, changing a file path, refactoring file location, or when
  the user mentions git mv, rename, or move for source files. Do not use for in-place edits only.
---

# Git rename, then patch

When renaming or moving a file (path change, not in-place content edits):

1. **Rename with Git** so history is preserved:
   - Prefer `git mv <old-path> <new-path>` (or an IDE flow that records a rename).
   - Avoid unrelated delete + add when an explicit rename is possible.

2. **Update content after the rename** on the new path (search/replace, patch, or small edits).

3. **Do not**:
   - Write a full new file at the new path and delete the old file as the primary workflow.
   - Copy entire contents to a new path then remove the old file unless `git mv` is impossible and the user agrees.

If `git mv` cannot be run, still **minimize churn**: one rename operation plus targeted edits, not wholesale recreate-and-delete.
