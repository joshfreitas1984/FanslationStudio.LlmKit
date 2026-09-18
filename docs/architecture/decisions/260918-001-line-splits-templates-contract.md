# ADR 260918-001: Preserve the Line-Splits-Templates Contract

- Status: Accepted
- Date: 2026-09-18

## Context

Downstream translation projects deserialize `TranslationLine`, `TranslationSplit`, and
`FieldTemplate` data from YAML and consume the same model during extraction, translation, and
packaging.

## Decision

Treat the `Line -> Splits -> (Templates)` hierarchy as a stable cross-repository contract. Extend
it only with optional fields that have safe defaults. Do not change the existing shape or meaning of
serialized fields.

## Consequences

Old downstream `Files/Converted/*.yaml` files remain readable. Game-specific behavior belongs in
configuration or hooks rather than in a competing downstream data model.
