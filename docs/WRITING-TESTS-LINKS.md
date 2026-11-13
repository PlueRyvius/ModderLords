# Writing in-game tests for ModderLords

*A page to hand to someone who will write tests: every link is a full URL, so it still works when pasted into a
chat or an issue.*

ModderLords can run scripted checks inside a real Bannerlord game, in single player or Coop (server plus a player's game), and report pass or fail for each step. Tests come in **test modules**: a folder of JSON suites, plus step code where the built-in steps aren't enough.

Repository: https://github.com/PlueRyvius/ModderLords (public). All links below point at `main`.

## Start here

- **WRITING-TEST-MODULES.md**: the guide for writing tests. It covers how to run tests, what a step can reach in the game, rules learned from real failures, how to write steps and suites, how to make a module, and a checklist before opening a PR.
  https://github.com/PlueRyvius/ModderLords/blob/main/docs/WRITING-TEST-MODULES.md

## Reference

- **INGAME-TEST-HARNESS.md**: the full suite format, every built-in step and its arguments, the run folder's files, and the design decisions. Decision 19 is the one about sharing a world between suites.
  https://github.com/PlueRyvius/ModderLords/blob/main/docs/INGAME-TEST-HARNESS.md
- **BuiltInSteps.cs**: the code for the built-in steps, and a good model for writing new ones.
  https://github.com/PlueRyvius/ModderLords/blob/main/src/ModderLords.CompatSync/Testing/BuiltInSteps.cs

## Examples to copy

- **The built-in test modules:**
  https://github.com/PlueRyvius/ModderLords/tree/main/src/ModderLords.Core/Testing/Modules
- **stash.json**, the most complete Coop suite:
  https://github.com/PlueRyvius/ModderLords/blob/main/src/ModderLords.Core/Testing/Modules/stash/stash.json
- **A module whose steps call a mod's own code**, so they live in a separate DLL (Retinues):
  - Module: https://github.com/PlueRyvius/ModderLords/tree/main/src/ModderLords.Core/Testing/Modules/retinues
  - Step DLL: https://github.com/PlueRyvius/ModderLords/tree/main/src/ModderLords.Tests.Retinues

## How to contribute

- **AGENTS.md**: branch from `main`, one PR per change, and never copy code from Bannerlord Coop or HexTool.
  https://github.com/PlueRyvius/ModderLords/blob/main/AGENTS.md

## Before you run anything

- **Coop:** you need a Host-mode profile and a test world: a `.sav` in `%LOCALAPPDATA%\ModderLords\test-worlds\`.
- **Single player:** no test world is needed; the run makes a world itself.
- **The game runs in a small window behind your others** while a run is going, so you can keep using the PC.
- **Write new suites in the API 2 format.** The run then waits for the game to load (and in Coop for the player to join) and pauses campaign time once per launch, so your suite starts with its own checks.
- **Add `"world": "shared"`** when your suite reads its "before" values instead of assuming them. It then runs on the same world as the other checks, and costs no Coop launch of its own.
