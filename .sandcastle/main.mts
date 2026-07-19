import { codex, run } from "@ai-hero/sandcastle";
import { noSandbox } from "@ai-hero/sandcastle/sandboxes/no-sandbox";

const [issueNumber, branch, defaultBranch] = process.argv.slice(2);

if (!issueNumber || !/^\d+$/.test(issueNumber) || !branch || !defaultBranch) {
  throw new Error("usage: main.mts <issue-number> <branch> <default-branch>");
}

await run({
  name: `afk-issue-${issueNumber}`,
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
});
