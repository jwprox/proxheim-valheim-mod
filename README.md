# Proxheim 0.2.0

A general Valheim BepInEx plugin combining the Troll Butt Follower effect with server-only bonus Growth loot.

## Upgrade from Troll Butt Follower

1. Stop the server/game and remove the old TrollButtFollower.dll from every plugin folder where it was installed. Do not load both plugins: Proxheim declares the old plugin incompatible to avoid duplicate effects and conflicting patches.
2. Extract Proxheim-v0.2.0-Install.zip into the Valheim server/game root, or copy Proxheim.dll to BepInEx/plugins/Proxheim/.
3. Restart. Check LogOutput.log for `Proxheim loaded v0.2.0`.

Proxheim uses `com.jwpro.valheim.proxheim.cfg`. On first startup it copies settings from `com.jwpro.valheim.trollbuttfollower.cfg` if the new file does not exist. The old file is retained as a backup. Previous follower placement and sound migrations remain included. Optional client installations should upgrade to Proxheim too.

No game assemblies, BepInEx, or Harmony DLLs are included. Install the current BepInExPack Valheim separately. ValheimPlus coexistence still requires in-game verification.

## Server-only Growth bonus

The server adds a uniformly selected **10–20 extra Tar, inclusive**, to a confirmed Growth death. This is ADDITIONAL to vanilla loot, not a 10–20 total. Other creature drops are unchanged. The bonus is not multiplied by stars, player count, or world resource settings, and is split if necessary to respect the native Tar stack size.

Only the SERVER awards the bonus. Clients do not need Proxheim for loot, and an optional client copy cannot award additional bonus drops. The server hooks locally simulated deaths and the vanilla network-object destruction callback. A per-Growth marker prevents those two paths from paying twice. Merely unloading a live creature does not award loot.

**Dedicated-server limitation:** a client-owned Growth must have its zero-health state replicated before the server handles its destruction. If the final update is missing, the server deliberately skips that removal instead of awarding loot for an unconfirmed kill. It logs `Growth removed without replicated death health; skipped extra Tar`. This path needs dedicated-server testing; the plugin cannot guarantee a bonus for every remotely simulated death without that death evidence. Successful payouts log `Growth bonus: spawned ... extra Tar`.

Server config:

```ini
[GrowthDrops]
Enabled = true
MinimumExtraTar = 10
MaximumExtraTar = 20
```

Bounds are inclusive, accept 1–10000, and reversed bounds are normalized. Stop the server before editing and restart afterward. Native Tar spawning is used; no custom item or client message is needed.

## Troll commands

```text
trollbutt Player Name
trollbuttrelease Player Name
```

Full character names with spaces are supported, ignoring case. Commands are admin-only by default; server adminlist.txt is checked using the real transport connection.

With Proxheim on BOTH your client and server, plain chat commands are sent directly to the server and are intended to work even when you are alone. Slash forms and F5 console forms also work on a client with Proxheim. Other players can join without the plugin. The client displays request status and the server response; 'Request sent' alone is not proof of success. No movement lock or collision bypass is added by installing it on a client.

Without a client plugin, use ordinary chat without a slash. `/s trollbutt Player Name` is another native-chat option. A dedicated server may not see the chat when there are no other recipients or communication permissions suppress them. An unmodified client will not recognize `/trollbutt` or the custom F5 command.

## Server command-file fallback

While the world is running, create `BepInEx/config/Proxheim.commands.txt` on the server, with one command per line. The old `TrollButtFollower.commands.txt` filename is also accepted when the new filename is absent. Save/upload to a temporary name first, then rename it when complete.

The server checks once per second, renames the file to a unique .processed name, and executes its lines. Check LogOutput.log for results. To release, create the original filename again with `trollbuttrelease Player Name`. File access is the authorization for this administrative fallback. Processed files may be deleted.

## Follower behavior

- Native passive Troll follows the player's replicated position and facing; the player can move normally.
- Nameplate: `<player name>'s new home`; native enemy selection skips it as a target.
- Default position offset: `(0, -0.5, 0.25)`, relative to player facing.
- Crypt scrap-pile squishes alternate with troll grunts every two seconds. Each sound group plays every four seconds. Existing one-shots finish on release.
- Spawn announcement: `<player name> has been a bad boy and is now enjoying some time up a troll's butt!`
- Release removes the troll without loot, adds a green blob-death poof, and announces: `<player name>'s punishment has ended. The smell however...`
- Death/disconnect ends the session. Restart recovery and shutdown cleanup are silent.

The player's damage, camera, inventory, and movement remain normal. Client troll collision is not disabled, so pushing, clipping, or sliding remain possible. Other creatures may still target the player or hit the troll incidentally. Sounds and VFX depend on the native prefabs available in your game version.

Additional server settings in the Proxheim config: Commands.AdminsOnly=true, Position.Offset=(0,-0.5,0.25), Position.Yaw=0, Sounds.Enabled=true, Sounds.IntervalSeconds=2. Adjust placement with the server stopped. Legacy internal follower tags/RPC identifiers are retained for cleanup and protocol compatibility, while the plugin, namespace, assembly, config, and installation folder are renamed Proxheim.

## Build and verification

Copy Local.props.example to Local.props, set ValheimPath/BepInExPath, then use a supported .NET SDK (built with 10.0.402):

```powershell
dotnet build src/Proxheim/Proxheim.csproj -c Release
```

The project targets net472. NuGet reference assemblies are needed on first restore; game/loader assemblies come from your own installation and are never redistributed. Output: src/Proxheim/bin/Release/net472/Proxheim.dll. With the server/game stopped, `-t:Deploy` explicitly copies to the configured BepInEx/plugins/Proxheim folder. Normal builds do not deploy.

Release compilation passed with no warnings/errors. Twelve Harmony targets were checked against installed game metadata. Six death-eligibility cases, 2000 inclusive bonus rolls, fixed bounds, and reversed bounds passed. Actual network loot replication, kill detection under latency, config migration, and ValheimPlus compatibility need in-game verification.

Test on a spare world: kill a Growth on a listen host, then kill a client-owned Growth on a dedicated server with an unmodified client. Confirm exactly one bonus stack/payout per confirmed death; inspect the success/skip logs. Also verify non-Growth deaths and administrative removals do not award bonuses, then regression-test the follower commands and release cleanup.

## GitHub Actions builds and releases

`.github/workflows/build-release.yml` builds pull requests, pushes to main, version tags, and manual runs. GitHub-hosted Ubuntu downloads the current public Valheim Dedicated Server (Steam app 896660) anonymously with SteamCMD, plus the pinned BepInExPack 5.4.2333 build references. No Steam credentials or custom repository secrets are required. Game/loader assemblies are never uploaded as artifacts or release assets. A new game update can require source fixes before CI builds again.

Each successful build uploads an artifact containing the install ZIP, standalone Proxheim.dll, SHA256SUMS.txt, and RELEASE_NOTES.md. Release notes include up to 50 commits since the previous version tag and installation/upgrade notes. Actions are pinned to verified commit SHAs.

To publish a release, keep Plugin.cs and Proxheim.csproj versions synchronized, commit the changes, then push a matching tag:

```powershell
git tag v0.2.0
git push origin v0.2.0
```

Only a pushed version tag publishes a release; main/PR/manual runs only build artifacts. A mismatched tag fails packaging. The release job uses the built-in GITHUB_TOKEN with contents:write and adds GitHub-generated release notes/changelog alongside the packaged notes. Prerelease versions containing a hyphen are marked prereleases. Re-running a tag build updates its assets without replacing release notes edited by a maintainer.

For local packaging after a Release build, run `pwsh ./scripts/Package.ps1`. This writes to dist/ (ignored by Git). Use a fresh output directory when rerunning, or remove the previous dist/staging folder first. Local builds still use Local.props; CI supplies ManagedPath and BepInExPath explicitly.
