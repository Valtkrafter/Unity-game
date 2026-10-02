---
name: Auto Commit and Push
description: Automatically updates a changelog, commits, and pushes changes to main at the end of each task.
---
# Auto Commit and Push Rule

## Trigger
Execute these instructions at the end of every prompt where you have modified files or made progress on the project.

## Actions
Before finishing your response to the user, you MUST do the following IN ORDER:
1. If you added new features or made significant architectural changes, update the `README.md` file in the root directory (`C:\Users\valen\Wuwa_Clone\README.md`). Make sure the "Current Features & State" section stays up-to-date with exactly what the project currently has. 
2. Run `git add .` to stage all the changes made during the turn (including the README.md update).
3. Run a `git commit -m "[Description]"` command. Your commit message should be a detailed, descriptive summary of what you did.
4. Run `git push origin main` to push the changes to the remote repository.
5. In your final response to the user, notify them that you have updated the README, committed, and pushed the changes.
