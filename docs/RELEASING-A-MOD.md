# Releasing a mod with the test harness

How to check a mod's release with ModderLords' in-game test harness, before and after publishing it. Modular Smithing 2
(the `smithing` test module) is the first mod that does this; any mod with a test module can.

This is about a **mod's** release. ModderLords' own releases follow [RELEASING.md](RELEASING.md).

## Before publishing

1. **Check the zip.** No game starts and no profile is needed:

   ```powershell
   ModderLords.Cli test --package ModularSmithing2-v0.9.50.zip --package-version v0.9.50 --package-check
   ```

   It passes when:
   - the file is a real zip (not, for example, a tar archive named .zip);
   - it holds only the mod's folder, whose `SubModule.xml` names the mod and the version;
   - it has at least the files the mod's test module expects a release to hold (module.json's `"package": { "atLeast": ... }`).

   It prints the SHA-256: compare it with the digest the release page shows once it is up.

2. **Run the mod's test module on the zip itself**, not on your own install:

   ```powershell
   ModderLords.Cli test --profile "!Modular" --package ModularSmithing2-v0.9.50.zip --package-version v0.9.50 --single-player
   ```

   The zip is checked, unpacked into `%LOCALAPPDATA%\ModderLords\package-tests\`, and pinned for the run in memory.
   Your Modules folder and your profile on disk are not changed. Every test module that requires the mod runs,
   including its upgrade suite on every kept upgrade world (a save made with each earlier release).

   A failure in the mod's own checks is a bug to fix in the mod before publishing, not a check to loosen.

## After publishing

3. **Keep this release's upgrade world**, so the next release is tested against a save made with this one:

   ```powershell
   ModderLords.Cli test --profile "!Modular" --package ModularSmithing2-v0.9.50.zip --suite smithing/keep-upgrade-world --single-player
   ```

   The world is kept as `%LOCALAPPDATA%\ModderLords\test-worlds\upgrade-ModularSmithing2-v0.9.50.sav`, with a note
   naming the run. From then on, `upgrade` runs once on each kept world, oldest first.

4. **Later: the Workshop copy.** Players load the mod from its Workshop folder, which can behave differently from a
   local copy (MS2 v0.9.47 fixed a path bug only the Workshop copy had). Running the test module against the Workshop
   copy is deferred for now, because Steam can take weeks to update a subscribed item.
