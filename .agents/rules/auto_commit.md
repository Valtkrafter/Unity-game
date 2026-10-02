---
name: Auto Commit and Push
description: Automatically updates a changelog, commits, and pushes changes to main at the end of each task.
---
# Auto Commit and Push Rule

## Trigger
Execute these instructions at the end of every prompt where you have modified files or made progress on the project.

## Actions
Before finishing your response to the user, you MUST do the following IN ORDER:
1. Update a `CHANGELOG.md` file in the root directory (`C:\Users\valen\Wuwa_Clone\CHANGELOG.md`). Prepend a new entry at the top of the file (below the main `# Changelog` header) containing the current date and a detailed summary of what was accomplished. If the file doesn't exist, create it.
2. Run `git add .` to stage all the changes made during the turn (including the CHANGELOG.md update).
3. Run a `git commit -m "[Description]"` command. Your commit message should be a detailed, descriptive summary of what you did.
4. Run `git push origin main` to push the changes to the remote repository.
5. In your final response to the user, notify them that you have updated the CHANGELOG, committed, and pushed the changes.
