---
name: Auto Commit and Push
description: Automatically commits and pushes changes to main at the end of each task.
---
# Auto Commit and Push Rule

## Trigger
Execute these instructions at the end of every prompt where you have modified files or made progress on the project.

## Actions
Before finishing your response to the user, you MUST do the following:
1. Run `git add .` to stage all the changes made during the turn.
2. Run a `git commit -m "[Description]"` command. Your commit message should be a detailed, descriptive summary of what you did and the current state of the project.
3. Run `git push origin main` to push the changes to the remote repository.
4. In your final response to the user, notify them that you have committed and pushed the changes, and share the commit message.
