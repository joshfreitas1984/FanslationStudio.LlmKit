# ADR 260918-004: Review Whole Columns and Anchor QC State

- Status: Accepted
- Date: 2026-09-18

## Context

A compound column is reconstructed from several fragments, but translation quality is a property of
the complete cell. A correction or score cannot be mapped reliably back onto individual fragments.

## Decision

Quality review evaluates the fully reconstructed column. For a compound column, store the single QC
verdict on the fragment with `SubIndex == 0`; other fragments do not carry independent QC state.
Keep QC fields additive so existing serialized translation data remains compatible.

## Consequences

QC can detect seam and whole-sentence defects without inventing an ambiguous reverse mapping. Code
reading QC state must filter by column first and apply the anchor convention only within that column.
