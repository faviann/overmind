import { readFile } from "node:fs/promises";
import { createHash } from "node:crypto";
import { homedir } from "node:os";
import { join } from "node:path";
import type { AgentStreamEvent } from "@ai-hero/sandcastle";
import { codex, run } from "@ai-hero/sandcastle";
import { noSandbox } from "@ai-hero/sandcastle/sandboxes/no-sandbox";

const [issueNumber, branch, defaultBranch, expectedWorkOnDigest] =
  process.argv.slice(2);

if (
  !issueNumber ||
  !/^\d+$/.test(issueNumber) ||
  !branch ||
  !defaultBranch ||
  !expectedWorkOnDigest ||
  !/^[0-9a-f]{64}$/.test(expectedWorkOnDigest)
) {
  throw new Error(
    "usage: main.mts <issue-number> <branch> <default-branch> <work-on-sha256>",
  );
}

const workOnSkillPath = join(
  homedir(),
  ".agents",
  "skills",
  "work-on",
  "SKILL.md",
);
let workOnInstructions: string;
try {
  workOnInstructions = await readFile(workOnSkillPath, "utf8");
} catch (error) {
  throw new Error(
    `cannot load the required work-on skill at ${workOnSkillPath}`,
    { cause: error },
  );
}
if (workOnInstructions.trim() === "") {
  throw new Error(
    `cannot load the required work-on skill because it is empty: ${workOnSkillPath}`,
  );
}
const observedWorkOnDigest = createHash("sha256")
  .update(workOnInstructions)
  .digest("hex");
if (observedWorkOnDigest !== expectedWorkOnDigest) {
  throw new Error(
    `cannot load the required work-on skill because it changed after preflight: ${workOnSkillPath}`,
  );
}

const runName = `afk-issue-${issueNumber}`;
const logPath = join(process.cwd(), ".sandcastle", "logs", `${runName}.log`);
const linePrefix = `[afk #${issueNumber}] `;

/**
 * Every terminal line carries the run prefix, so long or multi-line agent
 * content cannot be mistaken for a later lifecycle message.
 */
const emit = (line: string): void => {
  process.stdout.write(`${linePrefix}${line}\n`);
};

const ESC = "\u001B";
const BEL = "\u0007";

/**
 * Agent output can carry colour and hyperlinks; whole escape sequences (CSI, OSC,
 * two-char ESC) are dropped here because the durable log keeps the original.
 */
const ansiSequences = new RegExp(
  `${ESC}\\[[0-?]*[ -/]*[@-~]|${ESC}\\][^${BEL}${ESC}]*(?:${BEL}|${ESC}\\\\)|${ESC}[@-_]`,
  "gu",
);

/** Unicode category Other, minus tab, which would garble the operator's terminal. */
const controlChars = /[^\P{C}\t]/gu;

const onAgentStreamEvent = (event: AgentStreamEvent): void => {
  switch (event.type) {
    case "text":
      for (const chunkLine of event.message.split("\n")) {
        const printable = chunkLine
          .replace(ansiSequences, "")
          .replace(controlChars, "");
        if (printable.trim() !== "") {
          emit(printable);
        }
      }
      return;
    case "toolCall":
      // Names only; formatted arguments stay in the durable log.
      emit(`tool: ${event.name}`);
      return;
    case "raw":
      // Unparsed provider JSON never reaches the operator's terminal.
      return;
  }
};

await run({
  name: runName,
  cwd: process.cwd(),
  sandbox: noSandbox(),
  agent: codex("gpt-5.6-sol", { effort: "medium" }),
  prompt: `$work-on #${issueNumber}`,
  maxIterations: 1,
  branchStrategy: {
    type: "branch",
    branch,
    baseBranch: `origin/${defaultBranch}`,
  },
  idleTimeoutSeconds: 1200,
  completionTimeoutSeconds: 60,
  logging: {
    type: "file",
    path: logPath,
    onAgentStreamEvent,
  },
});
