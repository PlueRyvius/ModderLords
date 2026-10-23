# Compat-record intake worker

A Cloudflare Worker that lets ModderLords' **Submit…** send a compat record without the player having a GitHub
account, and without the URL-length and clipboard workaround. It opens the same *Compat record* issue the GitHub
form would, from a bot token, with the `compat-record` label. From there nothing changes: the intake workflow
(`.github/workflows/compat-record.yml`) validates the record and opens a pull request, and the maintainer reviews and
merges it by hand. See `docs/COMPAT-RECORDS.md`.

The app only uses it once `CompatIntakeClient.DefaultEndpoint` in `src/ModderLords.Core/Compat/CompatIntakeClient.cs`
holds its address. Until then, and whenever a send fails, Submit… opens the GitHub form exactly as before.

## What it accepts

`POST /submit` with JSON holding the form's fields, nothing more:

```json
{ "mod": "ImprovedGarrisons", "modVersion": "v4.2.0.7", "coopVersion": "v0.1.6", "record": "{ ...record JSON... }", "notes": "..." }
```

It answers `201 { "number", "url" }` with the new issue, `422 { "problems": [...] }` for a submission it refuses,
`429` when a sender or the whole intake is over its limit, and `502` when GitHub refused the issue.

It checks the same limits as `CompatIntake` (a C# test keeps them in step), checks the record is JSON, and defuses
notes lines that would read as one of the form's headings. The full validation is still the intake workflow's.

## Limits and privacy

- 5 submissions an hour and 20 a day per sender, 200 a day in all. Counters live in KV and expire on their own.
- The sender is known only by a salted hash of their IP, used for those counters. The IP is never stored and never
  reaches GitHub. The issue holds only the fields above; its Notes say it came through ModderLords.
- KV is eventually consistent, so the limits stop a flood rather than a careful attacker. The intake's checks and the
  maintainer's review are what keep bad records out, as they are for the form.

## Setting it up (maintainer, once)

1. **A token for the bot.** On GitHub: *Settings > Developer settings > Fine-grained tokens > Generate new token*.
   Repository access: only `PlueRyvius/ModderLords`. Permissions: **Issues: Read and write**, nothing else. Use an
   account you are happy to have shown as the issues' author. Issues it opens do start the intake workflow (only the
   built-in `GITHUB_TOKEN` doesn't).
2. **The worker**, from this folder, signed in to Cloudflare:

   ```bash
   npx wrangler kv namespace create LIMITS
   ```

   Paste the id it prints into `wrangler.toml`, then:

   ```bash
   npx wrangler secret put GITHUB_TOKEN
   ```

   ```bash
   npx wrangler secret put IP_SALT
   ```

   (any long random string), then:

   ```bash
   npx wrangler deploy
   ```

3. **Point the app at it.** Put `https://<worker>.workers.dev/submit` in `CompatIntakeClient.DefaultEndpoint`, in the
   PR for the release that should start using it.

To try a change first, run the worker locally (`npx wrangler dev`) and start ModderLords with
`MODDERLORDS_COMPAT_INTAKE=http://localhost:8787/submit`. Only https addresses, or this PC, are accepted.

## Tests

```bash
npm test
```

`test/fixtures/issue-body.md` is a body the worker builds. Its own tests check it is exactly what `issueFor` returns,
and `CompatIntakeWorkerTests` in the C# tests checks the intake accepts it. Change both together.
