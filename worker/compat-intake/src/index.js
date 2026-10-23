// ModderLords compat-record intake. The app's Submit… posts a record here; this opens the same "Compat record" issue
// the GitHub form would, from a bot account, so a player needs no GitHub account and no copy-paste. The intake
// workflow then validates the record and opens a pull request for the maintainer to review, exactly as before.
//
// Bindings (wrangler.toml): GITHUB_TOKEN and IP_SALT are secrets; LIMITS is a KV namespace; REPO is a variable.
// The token needs Issues: read and write on that one repository, and nothing else.
//
// PRIVACY. What is stored: per-hour and per-day counters under a salted hash of the sender's IP, which expire on
// their own. The IP itself is never stored or forwarded. The issue holds only the fields the app sent.

import { validate, issueFor } from "./form.js";

const MAX_REQUEST_BYTES = 70 * 1024;
const PER_IP_PER_HOUR = 5;
const PER_IP_PER_DAY = 20;
const ALL_PER_DAY = 200;

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname !== "/submit") return reply(404, { error: "Not found." });
    if (request.method !== "POST") return reply(405, { error: "Use POST." }, { Allow: "POST" });
    if (!(request.headers.get("Content-Type") || "").toLowerCase().startsWith("application/json"))
      return reply(415, { error: "Send JSON." });

    const raw = await request.text();
    if (new TextEncoder().encode(raw).length > MAX_REQUEST_BYTES) return reply(413, { error: "The submission is too large." });
    let input;
    try { input = JSON.parse(raw); } catch { return reply(400, { error: "The request is not valid JSON." }); }

    const checked = validate(input);
    if (checked.problems) return reply(422, { error: "The submission was refused.", problems: checked.problems });

    const limited = await overLimit(request, env);
    if (limited) return reply(429, { error: limited }, { "Retry-After": "3600" });

    const issue = issueFor(checked.fields);
    const gh = await fetch(`https://api.github.com/repos/${env.REPO}/issues`, {
      method: "POST",
      headers: {
        Authorization: `Bearer ${env.GITHUB_TOKEN}`,
        Accept: "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "modderlords-compat-intake",
        "Content-Type": "application/json",
      },
      body: JSON.stringify(issue),
    });
    if (!gh.ok) {
      // GitHub's own message stays in the worker's log; the app only needs to know to fall back to the form.
      console.log("GitHub refused the issue", gh.status, (await gh.text()).slice(0, 500));
      return reply(502, { error: "GitHub did not accept the issue. Try again later, or use the GitHub form." });
    }
    const created = await gh.json();
    return reply(201, { number: created.number, url: created.html_url });
  },
};

/** Null when the sender may submit now, otherwise why not. Counts the submission when it is allowed. */
async function overLimit(request, env) {
  const now = new Date();
  const hour = now.toISOString().slice(0, 13);
  const day = now.toISOString().slice(0, 10);
  const who = await hash((request.headers.get("CF-Connecting-IP") || "unknown") + "|" + env.IP_SALT);

  const keys = [
    { key: `h:${hour}:${who}`, max: PER_IP_PER_HOUR, ttl: 3600 * 2, why: "Too many submissions from here in the last hour. Try again later." },
    { key: `d:${day}:${who}`, max: PER_IP_PER_DAY, ttl: 86400 * 2, why: "Too many submissions from here today. Try again tomorrow." },
    { key: `d:${day}:all`, max: ALL_PER_DAY, ttl: 86400 * 2, why: "The intake is busy today. Try again tomorrow, or use the GitHub form." },
  ];
  const counts = await Promise.all(keys.map((k) => env.LIMITS.get(k.key).then((v) => Number(v) || 0)));
  const hit = keys.find((k, i) => counts[i] >= k.max);
  if (hit) return hit.why;
  // KV is eventually consistent, so two requests in the same second can both pass. These limits stop a flood, not
  // a careful attacker; the intake's own checks and the maintainer's review are what keep bad records out.
  await Promise.all(keys.map((k, i) => env.LIMITS.put(k.key, String(counts[i] + 1), { expirationTtl: k.ttl })));
  return null;
}

async function hash(text) {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
  return [...new Uint8Array(digest)].slice(0, 12).map((b) => b.toString(16).padStart(2, "0")).join("");
}

function reply(status, body, headers = {}) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json", ...headers } });
}
