# Troll Butt Follower v0.1.14

A server-controlled vanilla troll visual effect, with OPTIONAL client support for reliable direct commands. The server needs the plugin. Other players can join without it.

## Install and upgrade

1. Stop the server and copy TrollButtFollower.dll to BepInEx/plugins/TrollButtFollower/, replacing the previous DLL. The install ZIP has this folder layout.
2. For reliable commands while you are the only connected player, also install the SAME v0.1.14 DLL on your client in BepInEx/plugins/TrollButtFollower/.
3. Both machines need BepInExPack Valheim. Restart the server and your game. Confirm startup logs v0.1.14.

This is the same DLL for server and optional client. It does not require other players to install it, replace BepInEx/Harmony files, or alter ValheimPlus configuration. ValheimPlus coexistence has not been verified; collect LogOutput.log if it stops working. Do not keep duplicate copies of this plugin.

## With the optional client plugin

Use the Enter chat box:

```text
trollbutt Player Name
trollbuttrelease Player Name
```

These also work as `/trollbutt Player Name` and `/trollbuttrelease Player Name`, or in the F5 console without a slash if you have enabled the console. Full names with spaces are supported, ignoring case.

Client commands go directly over the connected server's peer RPC. They do not depend on chat recipients, so they are intended to work with a single player connected. They are not broadcast as ordinary chat. The client displays 'Request sent' followed by the server's response in chat/console. 'Request sent' alone is not confirmation that the command succeeded; the server must also have v0.1.14 or later.

The server identifies the request using the actual transport connection, checks readiness, validates the command, applies the existing one-second rate limit, and checks adminlist.txt when Commands.AdminsOnly is true. A client cannot claim a different admin identity through a payload. Replies are accepted only from the client's connected server.

## Without the client plugin

Plain chat commands remain supported by the server observer. Do not use a leading slash or F5 for these custom commands on an unmodified client; Valheim will reject unknown commands locally. You can also try `/s trollbutt Player Name` to use native shout chat.

The current game sends chat to individual player recipients. On a dedicated server, chat may not reach the observer when only one player is connected or communication permissions suppress other recipients. Use the optional client plugin or the server file fallback in that case. Server observer commands can be visible as ordinary chat, and responses may appear only in the client console/log.

All chat/direct commands are admin-only by default. The command file uses server filesystem access as authorization.

## Command-file fallback

While the world is running, create this file on the SERVER:

`BepInEx/config/TrollButtFollower.commands.txt`

Put one command per line, for example `trollbutt Player Name`. The server checks every second, renames the file to a unique .processed filename, then processes the lines. To release, create the original filename again containing `trollbuttrelease Player Name`.

Save or upload to a temporary filename and rename it to the inbox when complete. Check BepInEx/LogOutput.log for success or errors; .processed means claimed, not that every command succeeded. Processed files may be deleted. If your host exposes the game's actual server console, the same commands are registered there, but stock headless Valheim has no interactive command console.

## Effect behavior

- Vanilla Troll follows the player's replicated position and facing; the player moves freely.
- Nameplate: `<player name>'s new home`.
- Native m_aiSkipTarget field makes normal vanilla enemy selection skip the follower. Other creatures may still attack the PLAYER; incidental attacks remain possible.
- Troll stays passive and protected against damage through server control.
- Squishy scrap-pile hit effects from Sunken Crypts alternate with troll grunts every two seconds. Each individual group plays every four seconds. Current one-shots finish after release.
- Spawn announces: `<player name> has been a bad boy and is now enjoying some time up a troll's butt!`
- Release deletes the troll without loot, spawns a green vanilla blob-death poof, and announces: `<player name>'s punishment has ended. The smell however...`
- Death/disconnect ends the session. Recovery and shutdown cleanup are silent. Old tagged followers are removed after restarting the world.

The player retains normal damage, camera, inventory, and movement rules. Client-side troll collision is NOT disabled, even when the optional client plugin is installed; that option only adds command transport. Pushing, sliding, clipping, or latency remain possible.

## Configuration

Placement correction: v0.1.14 reverses the recent horizontal adjustment, changing Z=-0.25 to Z=+0.25 while keeping Y=-0.5. Existing previous-default configs migrate automatically; other custom coordinates are preserved. Server settings control placement. Solo direct chat commands require this DLL on BOTH client and server; server-only users still need the command file when no chat packet is sent to the server.


Generated file: BepInEx/config/com.jwpro.valheim.trollbuttfollower.cfg. Effect settings are read on the SERVER; optional client settings do not control the server.

- Commands.AdminsOnly = true
- Position.Offset = (0, -0.5, 0.25)
- Position.Yaw = 0
- Sounds.Enabled = true
- Sounds.IntervalSeconds = 2 (allowed range 1–60)

Offset is relative to player facing. Y controls height; Z controls forward/back placement. Stop, edit, restart after changes. Existing old default offsets and sound intervals migrate once; custom values and revision markers are preserved. These controls remain experimental and need visual tuning. If uninstalling, release followers before stopping the server and removing the DLL. Crash leftovers need one startup with the plugin installed for cleanup.

## Diagnostics and verification

- 'Direct command received' confirms the optional client's request reached the server.
- 'Chat command received via Say/ChatMessage' confirms ordinary chat reached the observer.
- 'Chat command rejected' reports identity, readiness, or permission issues.
- 'Chat packet observation failed' reports ordinary chat parsing errors.
- 'Sound accepted/rejected' and 'Repeating sound prefabs' show native sound selection.

Release build passed with zero warnings/errors. All eleven Harmony patch targets and named arguments were checked against installed game metadata; ten compiled parser cases passed. Actual client/server network execution and ValheimPlus compatibility have not been verified in game.

Suggested in-game test: connect alone with v0.1.14 installed on client/server, send spawn and release in chat, check direct-request and response logs, and confirm a non-admin is denied. Then connect a second client without the plugin and confirm it can join and see the effect.

## Build from source

Install a supported .NET SDK (built here using 10.0.402). Copy Local.props.example to Local.props and set ValheimPath plus BepInExPath if your mod profile is elsewhere. The project targets net472 and restores .NET Framework reference assemblies from NuGet; game/loader references are local and never bundled.

```powershell
dotnet build src/TrollButtPlugin/TrollButtPlugin.csproj -c Release
```

Output: src/TrollButtPlugin/bin/Release/net472/TrollButtFollower.dll. With the game stopped, append `-t:Deploy` to explicitly copy into the configured BepInEx plugin folder. Normal builds do not deploy.


