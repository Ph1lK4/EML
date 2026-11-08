# EML Launcher

[简体中文](README.md) | English

EML is a Windows launcher built for GTNH players in China.

A long GTNH playthrough can span several pack updates. Each update means setting up a new instance and bringing over your saves, maps and settings. EML puts those steps in one place: import the new pack, download missing dependencies, then copy across the player data you want to keep.

We want to make starting and sticking with GTNH easier, including for players who aren't comfortable managing game files by hand. EML can import other modpack formats too, but development is focused on the needs of the Chinese GTNH community.

The project is still in development. The interface and pack compatibility are ongoing work.

## What it does

- Transfers player data between GTNH client instances, with a file preview, backups and recovery. You can also select extra mods you added yourself.
- Detects installed Java runtimes on startup. You can choose one yourself or let EML pick one that matches the instance's requirements.
- Finds local instances under `instances/` and shows their names, Minecraft versions, loaders and paths. Instances can be renamed, deleted or exported as ZIP backups.
- Imports Prism ZIPs, Modrinth `.mrpack` files, CurseForge ZIPs and ZIPs containing a supported game directory. Dependency downloads start after import.
- Downloads mods and supported runtime dependencies listed by the pack. When a CurseForge file cannot be downloaded directly, EML looks for a Modrinth copy with the same hash and size. Files without a matching source are flagged for manual handling.
- Offers quick file checks and full hash verification. Checks account for file origins, replacements explicitly accepted by the user and player data that is expected to change during play.
- Saves launch settings per instance, including player name, Java, memory and extra JVM arguments. It also displays game logs. Normal launches check the files needed to start the game without hashing the entire pack again.

The Microsoft sign-in flow is in place, but Minecraft API approval and a successful login with a real account are still pending. Sign-in is not yet ready to use. See the [sign-in notes](docs/microsoft-login.md) for configuration and status.

## Moving your data to a newer GTNH version

Keep both instances: the old one with your player data, and a separate installation of the new version. Finish downloading the new instance's dependencies and make sure it starts successfully before migrating. Close both games first.

Select the new instance, open the GTNH migration tab (`GTNH 数据迁移`), and choose “Import data from an older GTNH instance” (`从旧 GTNH 迁入数据`). Pick the old instance, select what to copy, and review the file list before proceeding.

You can bring over:

- Singleplayer worlds, NEI records, JourneyMap data, ore vein and Thaumcraft node records, selected personal settings and your server list.
- Local data used when playing on a server. The server-player preset selects maps, global NEI records and your language preference by default.
- Your Chinese language preference and any extra mods you select individually. Extra mods may not work with the newer pack, so migration requires you to acknowledge that risk.

Migration leaves the old instance's game files intact. It backs up destination files before changing them and gives worlds with conflicting names separate folders. You can undo the last migration, but recovery will refuse to overwrite data that has changed since then, so later progress is preserved.

This feature handles client instances only. It does not migrate a server or install a Chinese translation pack. Your language setting carries over, but the translated text still comes from the new pack or a separate translation pack made for that version.

World compatibility depends on the GTNH versions involved. Follow the pack's update instructions. For large version jumps, update through the intermediate major versions and check your world at each step.

See the [GTNH migration guide](docs/gtnh-player-data-migration.md) for the full list of supported data and recovery details.

## Getting started

1. Start EML and let it detect Java and local instances.
2. Choose “Import instance” (`导入实例`), select your pack archive, and wait for import and dependency downloads to finish.
3. Review the results. Resolve any files marked for manual handling, then check again.
4. Open “Launch settings” (`启动设置`), choose Java, memory and JVM options, then click “Launch game” (`启动游戏`).

Debug builds use `instances/` in the solution directory. Release builds use `instances/` next to the launcher executable.

Pack support depends on the available metadata and loader implementations. Not every ZIP or modpack can be prepared and launched automatically. ZIP exports do not include shared dependencies stored outside the instance directory.

## Planned work

- Improve the process of importing a newer GTNH version, preparing its dependencies and moving player data across.
- Complete Minecraft API approval and verify Microsoft sign-in with real accounts.
- Improve extra-mod management and launch settings, then add online search and download options relevant to GTNH players.
- Refine the Chinese interface, setup instructions and compatibility across GTNH versions.

## Documentation

These guides are currently in Chinese:

- [Launching instances and setting JVM arguments](docs/launching.md)
- [GTNH player data migration](docs/gtnh-player-data-migration.md)
- [File integrity and changes made during play](docs/gameplay-file-policy.md)
- [Microsoft sign-in and developer configuration](docs/microsoft-login.md)

Bug reports and contributions are welcome. When reporting a problem, please include the pack name and version, your Java version, steps to reproduce it and the relevant error messages.
