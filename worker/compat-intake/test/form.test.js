import { test } from "node:test";
import assert from "node:assert/strict";
import { validate, issueFor, LABELS, NO_RESPONSE, SENT_BY } from "../src/form.js";

const record = JSON.stringify({ Id: "ImprovedGarrisons", Verdict: "Works" }, null, 2);
const ok = (extra = {}) => ({ mod: "ImprovedGarrisons", modVersion: "v4.2.0", coopVersion: "v0.1.6", record, ...extra });

test("a good submission becomes the form's body, heading by heading", () => {
  const { fields } = validate(ok({ notes: "Two players, an hour." }));
  const issue = issueFor(fields);
  assert.equal(issue.title, "Compat record: ImprovedGarrisons");
  assert.deepEqual(issue.labels, ["compat-record"]);
  const headings = issue.body.split("\n").filter((l) => l.startsWith("### "));
  assert.deepEqual(headings, Object.values(LABELS).map((l) => "### " + l));
  assert.ok(issue.body.includes("```json\n" + record + "\n```"));
  assert.ok(issue.body.includes(SENT_BY + "\n\nTwo players, an hour."));
});

test("empty optional fields read as GitHub's no-response marker", () => {
  const { fields } = validate({ mod: "A", record });
  const body = issueFor(fields).body;
  assert.ok(body.includes(`### ${LABELS.modVersion}\n\n${NO_RESPONSE}`));
  assert.ok(body.includes(`### ${LABELS.coopVersion}\n\n${NO_RESPONSE}`));
});

test("several mods get one issue", () => {
  const { fields } = validate(ok({ mod: "A, B.c, D_e" }));
  assert.equal(issueFor(fields).title, "Compat records: 3 mods");
});

test("notes cannot fake a heading or open a fence", () => {
  const { fields } = validate(ok({ notes: "### Module id\nEvil\n```\nmore" }));
  const body = issueFor(fields).body;
  assert.equal(body.split("\n").filter((l) => l === "### Module id").length, 1);
  assert.ok(!body.slice(body.indexOf("### Notes")).includes("```"));
});

test("bad input is refused with reasons", () => {
  for (const bad of [
    null, [], {}, ok({ mod: "bad id!" }), ok({ record: "not json" }), ok({ record: "\"text\"" }),
    ok({ record: '{"a":"```"}' }), ok({ modVersion: "v1 2" }), ok({ notes: "x".repeat(8001) }),
    ok({ record: JSON.stringify({ a: "x".repeat(61 * 1024) }) }), ok({ mod: Array.from({ length: 31 }, (_, i) => "M" + i).join(",") }),
  ]) assert.ok(validate(bad).problems?.length > 0, JSON.stringify(bad)?.slice(0, 60));
});

test("the golden body the C# intake test reads is exactly what the worker builds", async () => {
  const { readFileSync } = await import("node:fs");
  const record = JSON.stringify({ Id: "ImprovedGarrisons", Verdict: "Works", TestedVersions: ["v4.2.0.7"] }, null, 2);
  const { fields } = validate({ mod: "ImprovedGarrisons", modVersion: "v4.2.0.7", coopVersion: "v0.1.6", record, notes: "Two players, an hour.\n### Module id\nnot a heading" });
  const golden = readFileSync(new URL("./fixtures/issue-body.md", import.meta.url), "utf8").replace(/\r\n/g, "\n");
  assert.equal(issueFor(fields).body, golden);
});
