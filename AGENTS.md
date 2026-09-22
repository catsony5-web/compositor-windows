# Working alongside the user

The user continues working on this Windows desktop while development runs. Prefer background file edits, terminal commands, headless self-tests, and offscreen WPF rendering so both activities can proceed at the same time.

- Do not focus, move, minimize, close, or send mouse/keyboard input to desktop windows unless the task strictly requires an interactive check. Explain the need before taking desktop control and keep the interaction brief.
- Do not terminate the user's applications or existing Morupixel sessions. Preserve open documents and do not overwrite, move, or remove the directory of a running portable release.
- Use `--self-test`, `--render-preview`, or `--render-studio-previews` for routine verification. Run any required background helper without opening a visible console or editor window.
- Keep source under the existing `src/App`, `src/Core`, `src/Engine`, `src/Formats`, `src/UI`, and `src/Tests` boundaries. See `docs/ARCHITECTURE.md` before reorganizing code.
- Before removing generated files, verify their resolved paths are inside this repository and check that no running process depends on the target. Preserve source, assets, project documents, and historical verification records.

An explicit instruction from the user to interact with an application takes precedence over the background-work preference for that task.

# Shared integration policy

- Read `docs/INTEGRATION.md` for Morupixel development and release work. All feature work starts from fresh `origin/main` on its own `codex/<feature>` branch/worktree; `scripts/StartFeature.ps1` creates one without touching existing work.
- Preserve other sessions' changes and integrate through a pull request. Update the branch with current main before final checks; only checked mainline commits may be published.
- Do not deliver a branch-local build as the official latest version. GitHub Releases is the shared download source, and the website follows complete releases. Keep version numbers, source commit, ZIP, checksum, and local launcher aligned.
