# Security Policy

## Reporting a vulnerability

If you have found a security issue in Sundial (the desktop app, the auto-updater, the in-game client mod, or the installer), please **do not** open a public GitHub issue.

Open a private security advisory at https://github.com/HumanGenome/Sundial/security/advisories/new. That is the only reporting channel. There is no security email address for this project; do not send reports to any address that claims to be one.

Include:

- A description of the vulnerability
- Steps to reproduce
- Affected component (app / updater / client mod / installer)
- Sundial version, shown at the bottom of the app window
- Whether the issue is currently being exploited

Reports are acknowledged within 72 hours and triaged within 7 days.

Server-side issues belong on [SundialServer](https://github.com/HumanGenome/SundialServer/security/advisories/new).

## Scope

In scope:

- Remote code execution through the app, the client mod, or the installer
- Anything that lets a malicious server or a network attacker run code on a player's machine
- Auto-update integrity: signature bypass, package substitution, or downgrade
- Local privilege escalation through the installer or the update swap
- Credential disclosure — a saved server password or RCON password leaking outside the app

Out of scope:

- Vulnerabilities in retail Solarpunk. Report those to the game's publisher.
- Vulnerabilities in third-party mods a server asks you to install.
- Cheating and anti-cheat concerns. Sundial does not provide anti-cheat.
- SmartScreen warnings on the installer. It is not code-signed yet; this is known and noted in the README.
