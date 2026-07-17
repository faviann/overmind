# Automatic workflow overengineering

## Context

An automatic issue workflow turned a small change to pull-request instructions
into a validation subsystem. The requested behavior was a compact telemetry
table in the existing closeout path. The first implementation instead added a
standalone contract, an executable Markdown parser, repeated validation, audit
repositories, and several remediation rounds.

This is a general workflow failure mode. It can affect code, configuration,
documentation, infrastructure, or skills whenever an agent converts a bounded
behavior request into new enforcement machinery.

## What went wrong

1. **The issue prescribed proof rather than behavior.** “Public checks prove the
   contract” suggested that a new validator was part of the deliverable even
   though the same agent produced and consumed the Markdown.
2. **The workflow applied maximum ceremony unconditionally.** Delegation,
   independent review, remediation loops, and an adversarial closure gate ran
   for a small workstation-local edit.
3. **Review created a strictness ratchet.** Each review round found another way
   to make the parser more exact. Those findings were treated as requirements
   even when the issue did not require machine-readable Markdown.
4. **Generic guidance overrode local proportionality.** Advice to prefer
   deterministic validation was applied without first asking whether the
   operation was fragile enough to need executable enforcement.
5. **There was no simplicity checkpoint.** The issue was closed before comparing
   the implementation with the smallest plausible owning surface.
6. **Unversioned work triggered synthetic provenance.** Temporary Git history
   and hashes were created to imitate a repository workflow instead of using a
   lightweight issue-only record.

## Resulting symptoms

- New files or abstractions appear for behavior that fits in an existing file.
- Tests validate incidental formatting rather than user-visible behavior.
- Review findings broaden the effective contract after implementation begins.
- Evidence production costs more than the change itself.
- Automatic execution reaches closeout before a maintainer sees the true delta.

## Candidate workflow guardrails

### Prefer the smallest owning surface

Before implementation, identify the existing component that owns the requested
behavior. Adding a new file, abstraction, parser, service, or synchronization
mechanism requires a concrete reason the owning surface is insufficient.

### Make verification proportional to risk

Use prose and inspection for flexible human-readable output. Add executable
validation only when failure is costly, the format is consumed mechanically, or
the operation is genuinely fragile and repeated.

### Keep acceptance criteria behavioral

State what users or systems must observe. Put prescribed mechanisms in scope
only when those mechanisms are themselves requirements. Avoid phrases such as
“prove with a public check” when read-back inspection satisfies the need.

### Classify review findings

A review finding is blocking only when it violates the explicit contract, a
binding repository rule, or a concrete correctness or safety boundary.
Additional strictness, generality, abstraction, or hypothetical hardening is
non-blocking unless the contract calls for it.

### Run a simplicity checkpoint before closeout

Compare the final delta with the smallest plausible implementation:

- Can an added file be removed?
- Can behavior stay in an existing owning module?
- Are tests exercising behavior or incidental representation?
- Did review add requirements absent from the issue?
- Is validation effort proportionate to change risk?

Simplify before closing when the answers reveal avoidable machinery.

### Adapt workflow ceremony to the artifact

Use full multi-agent review and adversarial closeout for risky implementation or
unattended merge decisions. Use a focused edit, proportionate verification, and
one review at most for small documentation, configuration, or instruction
changes. For unversioned artifacts, record the exact change without fabricating
Git history.

## Example: issue #96

The implemented delta initially grew to four files and 281 added lines. After a
maintainer correction it still used three files and 122 added lines, including a
78-line Markdown parser. The sufficient implementation changes two existing
files by roughly two dozen lines: record the start time, add the telemetry table,
and inspect the final pull-request body. No parser or standalone contract is
needed because no external system consumes the telemetry as a strict API.
