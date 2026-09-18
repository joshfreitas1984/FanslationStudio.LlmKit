# ADR 260918-006: Use One Root Documentation Tree

- Status: Accepted
- Date: 2026-09-18

## Context

The library and downstream game repositories accumulated overlapping root, sub-project, and feature
documentation locations. This made source-of-truth navigation ambiguous and caused architecture
material to be mixed with feature notes and investigations.

## Decision

Keep exactly one repository documentation tree at `docs/`. Use `docs/plans/` for plans and design
history, `docs/investigations/` for investigations and postmortems, `docs/features/<feature-or-project>/`
for feature documentation, and `docs/architecture/` for architecture references. Store individual
architectural decisions in `docs/architecture/decisions/` with a `yyMMdd-NNN-` filename prefix.
Keep `docs/README.md` and `docs/KNOWN_ISSUES.md` at the root of this tree.

## Consequences

Every child repository has the same navigation shape. Architecture decisions have stable, sortable
identifiers and issue indexes contain only issue/postmortem pointers.
