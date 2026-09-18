# ADR 260918-002: Define Compound Fragments in the Matching Regex

- Status: Accepted
- Date: 2026-09-18

## Context

Compound cells contain natural-language text mixed with structural separators, numbers, punctuation,
and game-specific placeholder tokens. Post-processing literal gaps after an initial regex pass
missed composite cases and could split one sentence into independently translated fragments.

## Decision

Fold characters and opted-in placeholder patterns that belong to a translatable run directly into
the run-matching regex. Keep genuine structural separators as template literals. Retain a post-pass
only for the sign/digit restart case that creates an empty gap between adjacent matches.

The shared defaults absorb Chinese punctuation, selected sentence punctuation, and the fullwidth
colon remains a fragment boundary. Game-specific ASCII punctuation and placeholder syntax must be
opted in through `CompoundFieldSplitterOptions` after checking real game data.

## Consequences

Fragments retain the context needed for translation and reconstruction remains deterministic. New
games must validate their punctuation assumptions instead of inheriting them implicitly.
