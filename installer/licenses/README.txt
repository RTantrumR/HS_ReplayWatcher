Third-party components installed by HS Replay Watcher

Installed into the Hearthstone folder only when it has no BepInEx yet:

- BepInEx 5.4.23.5 (BepInEx\core\BepInEx*.dll) - MIT - https://github.com/BepInEx/BepInEx - BepInEx.txt
- Unity Doorstop 4.5.0 (winhttp.dll, version.dll) - GNU LGPL 2.1 - source of this version:
  https://github.com/NeighTools/UnityDoorstop/tree/v4.5.0 - UnityDoorstop.txt
- HarmonyX 2.9.0 (BepInEx\core\0Harmony*.dll, HarmonyXInterop.dll) - MIT - https://github.com/BepInEx/HarmonyX
  - HarmonyX.txt
- MonoMod (BepInEx\core\MonoMod.*.dll) - MIT - https://github.com/MonoMod/MonoMod - MonoMod.txt
- Mono.Cecil (BepInEx\core\Mono.Cecil*.dll) - MIT - https://github.com/jbevain/cecil - Mono.Cecil.txt
- Mono class libraries (BepInEx\unstripped_corlib, except UniTask) - MIT and others as listed -
  https://github.com/mono/mono - Mono-class-libraries.txt
- UniTask (BepInEx\unstripped_corlib\UniTask*.dll) - MIT - https://github.com/Cysharp/UniTask - UniTask.txt

Always installed:

- CPython 3.12.10, embeddable package (BepInEx\HSReplayWatcher\python) - PSF License Version 2 and the
  licenses of its bundled libraries - https://www.python.org - python\LICENSE.txt

HS Replay Watcher itself (the plugin and BepInEx\HSReplayWatcher\server): Apache License 2.0, see LICENSE.txt
and NOTICE.txt in this folder.
