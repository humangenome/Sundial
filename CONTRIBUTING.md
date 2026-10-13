# Contributing to Sundial

Short and to the point.

This repo is the player-facing side of Sundial: the desktop app, the downloads, and the documentation. Server-side issues belong on [SundialServer](https://github.com/HumanGenome/SundialServer).

## Reporting bugs

Open an issue. Include:

- Sundial version (shown at the bottom of the app window) and which package you installed: `SundialSetup-<version>.exe` or `Sundial-Launcher-Windows-x64-v<version>.zip`
- Solarpunk build ID, and whether the game is a Steam, Epic, or MS Store install
- Steps to reproduce
- The app log from `%APPDATA%\Solarpunk\`
- Whether the server you were joining is one you run, and whether anyone else hits the same thing

Two things worth checking before filing:

- **Connect does nothing.** Confirm the game is not already running under a different account, and that Steam is signed in.
- **Joined but landed on empty progress.** Character names key the save shard and are matched case-insensitively, so a renamed character is a new character.

If your issue is about specific managed hosting (panel, billing, support), contact your host directly.

## Feature requests

Open an issue. Describe the use case, not the implementation.

## Pull requests

- Branch from `main`, name `feat/<short-slug>` or `fix/<short-slug>`
- Keep commits short and focused: one logical change per commit
- Match existing code style
- Run the test suite before opening the PR
- Anything touching the auto-updater needs a note on how you validated the signature and rejection paths, because a broken updater cannot be fixed by another update

## Code of conduct

Be civil. Be technical. Don't post game-piracy or anti-cheat-evasion material in issues or PRs.
