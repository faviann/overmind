import { join } from "node:path";
import type { AgentStreamEvent } from "@ai-hero/sandcastle";
import { codex, run } from "@ai-hero/sandcastle";
import { noSandbox } from "@ai-hero/sandcastle/sandboxes/no-sandbox";

const [issueNumber, branch, defaultBranch] = process.argv.slice(2);

if (!issueNumber || !/^\d+$/.test(issueNumber) || !branch || !defaultBranch) {
  throw new Error("usage: main.mts <issue-number> <branch> <default-branch>");
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

/** Control characters, minus tab, which would garble the operator's terminal. */
const nonPrintable = /[^\P{C}\t]/gu;

const onAgentStreamEvent = (event: AgentStreamEvent): void => {
  switch (event.type) {
    case "text":
      for (const chunkLine of event.message.split("\n")) {
        const printable = chunkLine.replace(nonPrintable, "");
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
