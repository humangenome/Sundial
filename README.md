<p align="center">
  <img src="docs/img/sundial-logo.png" alt="Sundial" width="440">
</p>

<p align="center">
  <a href="#install"><img src="https://img.shields.io/badge/Platform-Windows_10%2F11%2FServer-blue.svg" alt="Platform"></a>
  <a href="https://store.steampowered.com/app/1805110/"><img src="https://img.shields.io/badge/Game-Solarpunk-darkgreen.svg" alt="Game"></a>
  <a href="#how-many-players-can-join"><img src="https://img.shields.io/badge/Players-up_to_4-brightgreen.svg" alt="Players"></a>
  <a href="https://github.com/HumanGenome/SundialServer"><img src="https://img.shields.io/badge/Server_Source-SundialServer-444.svg" alt="Server Source"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-lightgrey.svg" alt="License"></a>
</p>

# Sundial

**Solarpunk** has no dedicated server. Its multiplayer is a session hosted from one player's copy of the game, so the world is only online while that player is. [SundialServer](https://github.com/HumanGenome/SundialServer) runs Solarpunk headless on a Windows machine with an Epic sign-in of its own, so the world stays online with nobody hosting from their PC.

Players join with the game's own invite code. There is nothing to install.

## How players join

1. Get the server's 5-letter code from whoever runs the server. A rented server's host shows it with the server's details.
2. In Solarpunk, choose **Multiplayer**, **Join session**, **Enter Code**, and type the code.

The code changes each time the server starts, so check it again after a restart.

## What the server does

- **Stays online.** The world runs on the server whether or not anyone is playing.
- **Its own Epic identity.** Each server signs in to Epic Online Services as itself, so two servers on one machine never share an address.
- **Bring your own world.** Upload a save from your single-player game and the group carries on from it. Your character in that world stays yours; the server keeps a hidden player of its own.
- **World snapshots.** The server snapshots the world on request (and on every autosave when that is switched on) and can put an earlier snapshot back, taking a snapshot of the running world first.
- **Admin console.** Source RCON for `status`, `players` and the snapshot commands.
- **Server query.** The server answers Source A2S query, so server lists, monitoring tools and Discord bots can read its status and player count.

## The Sundial app (legacy)

The Sundial app was for Solarpunk before 1.3.0, when players joined a server by address. Solarpunk 1.3.0 moved the game's multiplayer to Epic Online Services, and the app cannot join a 1.3.0 server; players do not need it now. Its releases stay on this repo's [release page](https://github.com/HumanGenome/Sundial/releases) for reference.

## Install

### Rent a server
[SurvivalServers.com Solarpunk hosting](https://www.survivalservers.com/services/game_servers/solarpunk/?utm_source=github&utm_medium=readme_install&utm_campaign=sundial) runs SundialServer for you on Solarpunk 1.3.0, and shows the server's invite code in its control panel.

### Self-hosted servers
SundialServer's [self-host steps](https://github.com/HumanGenome/SundialServer#installation) were written for Solarpunk before 1.3.0 and have not been redone for 1.3.0 yet. A server set up from them today does not open its world. The server package is on the [SundialServer release page](https://github.com/HumanGenome/SundialServer/releases/latest), and a 1.3.0 host needs the Solarpunk game files from your own copy.

## Releases

This repo's releases are the legacy Sundial app (`SundialSetup-<version>.exe` and a portable zip) for Solarpunk before 1.3.0. The server package, `SundialServer-v<version>.zip`, is published on the [SundialServer releases](https://github.com/HumanGenome/SundialServer/releases/latest).

## Source

**[SundialServer](https://github.com/HumanGenome/SundialServer)** is the open-source dedicated server. This repo carries the documentation and the legacy app's releases.

## FAQ

### Do players need to install anything?
No. Solarpunk's own Join session screen takes the server's invite code.

### How many players can join?
Up to four, the number Solarpunk's [Steam page](https://store.steampowered.com/app/1805110/) gives: "made for solo play and small group cooperative play (up to 4 players)". The host sets the server's slot count.

### Why did the code change?
The server gets a new invite code each time it starts. Ask the host for the current one.

### Is Linux supported?
No. The server is Windows only (Windows 10, 11 or Server, 64-bit) because it runs the Windows build of Solarpunk.

### Is this official?
No. Sundial is an independent project. The developers of Solarpunk do not ship dedicated servers for it.

## Contributing

Bug reports are welcome; [CONTRIBUTING.md](CONTRIBUTING.md) lists what to include. Server problems belong on [SundialServer](https://github.com/HumanGenome/SundialServer/issues).

## Community note

Sundial is a community project and is not affiliated with or endorsed by the developers of Solarpunk.

## License

MIT. See [LICENSE](LICENSE).

## Credits

- [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS): Unreal Engine scripting and modding framework, used on the server
- [Avalonia](https://avaloniaui.net/): .NET UI framework used by the legacy Sundial app
