// Builds the issue a ModderLords "Submit…" turns into, exactly as GitHub renders the compat-record issue form, so the
// intake workflow (.github/workflows/compat-record.yml) reads it the same whichever way it arrived.
//
// The labels and limits are a contract with src/ModderLords.Core/Compat/CompatIntake.cs. CompatIntakeTests reads this
// file and fails when they drift apart.

export const LABELS = {
  mod: "Module id",
  modVersion: "Mod version",
  coopVersion: "Coop version",
  record: "Compatibility record",
  notes: "Notes",
};

export const NO_RESPONSE = "_No response_";
export const ISSUE_LABEL = "compat-record";
export const SENT_BY = "_Sent from ModderLords' Submit…, without a GitHub account._";

export const LIMITS = {
  submissionBytes: 60 * 1024, // CompatIntake.MaxSubmissionBytes
  recordsPerIssue: 30, // CompatIntake.MaxRecordsPerIssue
  idLength: 64, // CompatIntake.MaxIdLength
  versionLength: 40, // CompatIntake.MaxVersionLength
  notesLength: 8000, // CompatIntake.MaxNotesLength
};

const ID = /^[A-Za-z0-9._-]+(?: [A-Za-z0-9._-]+)*$/;
const VERSION = /^[A-Za-z0-9._+-]+$/;

/**
 * Checks what the app sent and returns { fields } ready for issueFor, or { problems } saying why not. This is a first
 * line only: the intake workflow validates every record in full, and the maintainer reviews the pull request.
 */
export function validate(input) {
  const problems = [];
  if (input === null || typeof input !== "object" || Array.isArray(input)) return { problems: ["The request must be a JSON object."] };

  const text = (name, max, pattern, required) => {
    const value = input[name];
    if (value === undefined || value === null || value === "") {
      if (required) problems.push(`"${name}" is missing.`);
      return null;
    }
    if (typeof value !== "string") { problems.push(`"${name}" must be text.`); return null; }
    const trimmed = value.trim();
    if (trimmed.length > max) { problems.push(`"${name}" is longer than ${max} characters.`); return null; }
    if (pattern && !pattern.test(trimmed)) { problems.push(`"${name}" has characters it may not hold.`); return null; }
    return trimmed.length === 0 ? null : trimmed;
  };

  const modField = text("mod", LIMITS.idLength * LIMITS.recordsPerIssue + 2 * LIMITS.recordsPerIssue, null, true);
  const mods = modField === null ? [] : modField.split(",").map((m) => m.trim());
  if (modField !== null) {
    if (mods.length > LIMITS.recordsPerIssue) problems.push(`At most ${LIMITS.recordsPerIssue} mods per submission.`);
    if (mods.some((m) => m.length === 0 || m.length > LIMITS.idLength || !ID.test(m))) problems.push('"mod" must be module ids separated by commas.');
  }
  const modVersion = text("modVersion", LIMITS.versionLength, VERSION, false);
  const coopVersion = text("coopVersion", LIMITS.versionLength, VERSION, false);
  const notes = text("notes", LIMITS.notesLength, null, false);

  let record = null;
  if (typeof input.record !== "string" || input.record.trim() === "") problems.push('"record" is missing.');
  else if (new TextEncoder().encode(input.record).length > LIMITS.submissionBytes) problems.push(`"record" is larger than ${LIMITS.submissionBytes} bytes.`);
  else if (input.record.includes("```")) problems.push('"record" may not contain ```.');
  else {
    try {
      const parsed = JSON.parse(input.record);
      if (parsed === null || typeof parsed !== "object") problems.push('"record" must be a JSON object.');
      else record = input.record.trim();
    } catch {
      problems.push('"record" is not valid JSON.');
    }
  }

  if (problems.length > 0) return { problems };
  return { fields: { mods, modVersion, coopVersion, record, notes } };
}

/** The issue title, the way the app's own link titles it. */
export function titleFor(mods) {
  return mods.length === 1 ? `Compat record: ${mods[0]}` : `Compat records: ${mods.length} mods`;
}

// Notes are free text from the player. A line that starts like one of the form's headings would be read as that
// field (or refused as a duplicate), and a fence would swallow what follows; both are defused, nothing else changes.
function safeNotes(notes) {
  return notes
    .replace(/\r\n?/g, "\n")
    .split("\n")
    .map((line) => (line.startsWith("#") ? "\\" + line : line.replace(/```/g, "'''")))
    .join("\n");
}

/** { title, body, labels } for the GitHub "create an issue" call. */
export function issueFor(fields) {
  const section = (label, value) => `### ${label}\n\n${value ?? NO_RESPONSE}`;
  const body = [
    section(LABELS.mod, fields.mods.join(", ")),
    section(LABELS.modVersion, fields.modVersion),
    section(LABELS.coopVersion, fields.coopVersion),
    section(LABELS.record, "```json\n" + fields.record + "\n```"),
    // Said in the notes, the last field, so the body keeps exactly the form's shape.
    section(LABELS.notes, SENT_BY + (fields.notes === null ? "" : "\n\n" + safeNotes(fields.notes))),
  ].join("\n\n");
  return { title: titleFor(fields.mods), body, labels: [ISSUE_LABEL] };
}
