import { test } from "node:test";
import assert from "node:assert/strict";
import worker from "../src/index.js";

const kv = () => { const m = new Map(); return { get: async (k) => m.get(k) ?? null, put: async (k, v) => { m.set(k, v); } }; };
const env = () => ({ REPO: "owner/repo", GITHUB_TOKEN: "t", IP_SALT: "s", LIMITS: kv() });
const body = JSON.stringify({ mod: "A", record: JSON.stringify({ Id: "A" }) });
const post = (b = body, headers = {}) => new Request("https://intake.example/submit", {
  method: "POST", body: b, headers: { "Content-Type": "application/json", "CF-Connecting-IP": "1.2.3.4", ...headers } });

function stubGitHub(status = 201) {
  const calls = [];
  globalThis.fetch = async (url, init) => {
    calls.push({ url, init });
    return new Response(JSON.stringify({ number: 7, html_url: "https://github.com/owner/repo/issues/7" }), { status });
  };
  return calls;
}

test("a submission opens one labelled issue and returns its link", async () => {
  const calls = stubGitHub();
  const res = await worker.fetch(post(), env());
  assert.equal(res.status, 201);
  assert.deepEqual(await res.json(), { number: 7, url: "https://github.com/owner/repo/issues/7" });
  assert.equal(calls.length, 1);
  assert.equal(calls[0].url, "https://api.github.com/repos/owner/repo/issues");
  const sent = JSON.parse(calls[0].init.body);
  assert.deepEqual(sent.labels, ["compat-record"]);
  assert.ok(!calls[0].init.body.includes("1.2.3.4"), "the sender's IP never reaches GitHub");
});

test("only POST /submit with JSON", async () => {
  stubGitHub();
  assert.equal((await worker.fetch(new Request("https://intake.example/other", { method: "POST" }), env())).status, 404);
  assert.equal((await worker.fetch(new Request("https://intake.example/submit"), env())).status, 405);
  assert.equal((await worker.fetch(post(body, { "Content-Type": "text/plain" }), env())).status, 415);
  assert.equal((await worker.fetch(post("{"), env())).status, 400);
  assert.equal((await worker.fetch(post(JSON.stringify({ mod: "A" })), env())).status, 422);
});

test("the sixth submission in an hour from one sender is refused, and GitHub is not called for it", async () => {
  const calls = stubGitHub();
  const e = env();
  for (let i = 0; i < 5; i++) assert.equal((await worker.fetch(post(), e)).status, 201);
  assert.equal((await worker.fetch(post(), e)).status, 429);
  assert.equal(calls.length, 5);
  assert.equal((await worker.fetch(post(body, { "CF-Connecting-IP": "5.6.7.8" }), e)).status, 201, "another sender is not affected");
});

test("a GitHub failure is a 502 the app can fall back from", async () => {
  stubGitHub(500);
  assert.equal((await worker.fetch(post(), env())).status, 502);
});
