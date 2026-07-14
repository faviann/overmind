# Mission: Confidently Evolve Overmind

## Why

Make Overmind cease to be a black box so I can maintain and evolve it with
confidence. I want to judge refactors and dependency changes from evidence
without accidentally weakening the memory server's guarantees.

## Success looks like

- Follow a core behavior from its public test through its implementation and
  database guarantees, then explain the journey in my own words.
- Use the project's domain terminology to explain its important workflows and
  module seams.
- Predict which observable behavior and tests a proposed change could affect.
- Evaluate architecture and NuGet ideas against concrete codebase friction.

## Constraints

- Treat this mission as provisional and revise it as the first lessons reveal
  my starting point and goals.
- Begin from very little prior familiarity with the codebase.
- Build lessons around real tests, architecture reviews, refactor ideas, and
  dogfooding work rather than a generic .NET curriculum.
- Prefer short lessons with retrieval practice and an immediate feedback loop.
- Preserve the binding specification, domain invariants, and documented
  architectural decisions.

## Out of scope

- Memorizing every class or implementation detail.
- Learning C# or .NET as general-purpose subjects. Language and runtime details
  appear only when needed to understand a specific Overmind behavior.
- Adopting a library or architecture merely because it is familiar or
  fashionable.
