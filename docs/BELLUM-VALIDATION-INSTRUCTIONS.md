# Bellum Civile native validation instructions

These instructions use the disposable `BellumCompatSmoke` save and the exact Bellum 1.3.1 validation gate. They do
not enable the adapter in ordinary ModderLords launches and do not modify Bellum's files.

## Before starting

Close Bannerlord, the TaleWorlds launcher, and any old dedicated-server window. Keep Steam running and signed in.
The test uses direct loopback networking, so it needs no router changes or password.

## Start and join

1. Run [`Start-Bellum-Server.cmd`](../scripts/BellumValidation/Start-Bellum-Server.cmd).
2. Leave that terminal open. Wait for the green line:

   ```text
   SERVING — coop server up, waiting for clients
   ```

3. Run [`Start-Bellum-Client.cmd`](../scripts/BellumValidation/Start-Bellum-Client.cmd).
4. In Bannerlord's Coop screen, choose direct connection and enter:

   - Address: `127.0.0.1`
   - Port: `4200`
   - Password: leave blank

5. Complete Coop character creation if it appears. Do not repeatedly retry if the game returns to the main menu;
   record what was visible and the approximate time instead.

## What counts as the first pass

Once the campaign map appears:

1. Wait at least 60 seconds without opening a battle.
2. Open the Kingdom and Clan screens and browse several kingdoms, clans, and settlements. Open any Bellum read-only
   information panels you normally use. Look for missing entries, blank names, duplicated records, exceptions, or a
   return to the main menu.
3. Let campaign time advance for at least one in-game day if Coop allows it. This exercises periodic server authority
   and a changed-state snapshot.
4. Do not confirm a Bellum political action yet. Actor-aware player commands are the next implementation tier and
   have not been claimed as safe.
5. If the above remains stable, disconnect once and reconnect to the same address. Report whether the campaign map
   returns and the Bellum information still looks correct.

## Finish cleanly

1. Exit the client normally.
2. Return to the server terminal, type `stop`, and press Enter.
3. Wait for both `Successfully saved` and `engine exit code 0` before closing the terminal.

## Send back

Report:

- whether character creation was required and completed;
- whether the campaign map appeared;
- whether Kingdom/Clan/Bellum read-only panels looked correct;
- whether one in-game day advanced without a crash;
- whether reconnect succeeded;
- the approximate time of any failure and a screenshot/error text if shown.

The main evidence logs are:

- Server launch: the newest `modderlords-launch-*.log` under
  `C:\Users\A\AppData\Local\Temp`.
- Client adapter: `C:\Users\A\Documents\Mount and Blade II Bannerlord\Configs\ModLogs\ModderLords.Compat-client.log`.
- Coop server logs: `D:\Design\Bannerlord Mods\_bellum-civile-analysis-data\live\logs`.

A successful adapter handshake should produce `operation plan agreed` on both peers and
`operation snapshot applied: bellum-civile.state.v1` in the client adapter log.
