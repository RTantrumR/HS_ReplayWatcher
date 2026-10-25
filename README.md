# HS Replay Watcher

Watch your Hearthstone Battlegrounds games again, inside the game. Every Battlegrounds game you play is
recorded, and the new **Replays** page in the Battlegrounds lobby plays any of them back in the real client:
jump to any moment, step through a fight, or watch at up to x4.

**Beta.** Expect rough edges, and please report them: the bug button opens a
[bug report](../../issues) with the details already filled in.

![The Replays page: one card per game with hero, hero powers, trinkets, final board, place and length, and one game's final hand opened under its card](images/replays-page.webp)

![A replay in the shop, with the replay bar and its timeline at the top of the screen](images/replay-bar.webp)

- **Replays page:** Battlegrounds → **Replays** shows your last games, one card each: hero, hero powers,
  trinkets, final board and hand, place, date and length. **Watch** plays the game back.
- **Replay bar:** previous/next event and turn, pause and speed, and a timeline of the whole game with its fights
  marked. Hover it to see the turn and event, click to go there. Drag the bar anywhere or shrink it to its
  buttons; **F10** hides it.
- **Keys:** **Space** pause · **←/→** previous/next event · **Ctrl+←/→** previous/next turn ·
  **Enter/Backspace** one step · **+/−** speed.
- **Three lists**, switched with the arrows on the page's title: **Replays** (your last 5 to 50 games, set in
  **Settings**), **Saved** and **Imported**. The **star** on a card saves that game to a folder of your choice,
  outside the Hearthstone folder, where it stays. Saved and Imported can be sorted by date, place, hero or
  length.

## Sharing replays

- **Send:** click the arrow next to a game's place. The game is copied to your clipboard as a file: press
  **Ctrl+V** in Discord, Telegram or any chat to attach it.
- **Receive:** copy the file you got (in Explorer) and press **Ctrl+V** on the Replays page, or use **Import**
  in the Imported list, or drop it into the `Imported` folder. Import also takes a `Power.log` from Hearthstone's
  `Logs` folder and turns its Battlegrounds games into replays.
- **Remove** an imported game with the bin next to its star. **Settings** can turn off the "are you sure"
  prompts for removing games.

A shared file has your account's session passwords and your PC's name and device id removed. An imported file
is checked line by line before the watcher uses it, and only a clean copy rebuilt from it is kept.

## Install

1. Download `HSReplayWatcher-Setup-<version>.exe` from [Releases](../../releases).
2. Close Hearthstone and run the setup. Windows may warn about an unknown publisher (the setup isn't signed):
   choose **More info → Run anyway**. The setup finds your Hearthstone folder; check it and click Install.
3. Start Hearthstone from Battle.net as usual and play Battlegrounds. After a game, open **Replays** in the
   Battlegrounds lobby.

The setup brings [BepInEx](https://github.com/BepInEx/BepInEx), the mod loader, if your Hearthstone doesn't
have it yet. If it's already there (from another mod), it's left as it is.

**Requirements:** Windows, Hearthstone for PC (made for patch 37.0).

## Reporting a bug

Click the bug on the replay bar, or left of **Back** on the Replays page. It takes a screenshot, then opens a
new GitHub issue (you need a GitHub account) with your versions, PC and the moment of the replay filled in, and
a folder with the files to attach:

1. Press **Ctrl+V** in the issue to paste the screenshot.
2. Drag the files from the folder into the issue: the game and the logs.
3. Say what you did and what went wrong.

The game file is a copy with your account's session passwords and your PC's name and device id removed; it
replays exactly as yours does.

## Uninstall

Windows Settings → Apps → HS Replay Watcher → Uninstall. BepInEx is removed too, unless another mod still uses
it or it was there before. Your recordings stay in `%LOCALAPPDATA%\HS Replay Watcher`, and saved and imported
games in the folder you chose; delete those folders to remove them as well.

## Troubleshooting

- No Replays button: check that `BepInEx\LogOutput.log` in your Hearthstone folder contains
  `HS Replay Watcher loaded`.
- After a Hearthstone patch the watcher can stop working until it's updated.

## About

An unofficial, client-side mod, not supported by Blizzard. It records a game by keeping a copy of the messages
between the game server and your client, and plays a replay from a small server on your own PC; it never
changes a live match. The watcher sends nothing anywhere: the bug button only opens a page in your browser.

## License

Copyright 2026 TantrumUA(RTantrumR). Licensed under the [Apache License 2.0](LICENSE); see [NOTICE](NOTICE). Hearthstone's
art, fonts and text that the watcher shows stay Blizzard's: they're loaded from your own installation, and the
license doesn't cover them. Built on [BepInEx](https://github.com/BepInEx/BepInEx) and
[HarmonyX](https://github.com/BepInEx/HarmonyX), with Python's embeddable package for the replay server.

Not affiliated with or endorsed by Blizzard Entertainment. Hearthstone is a trademark of Blizzard
Entertainment, Inc.
