# ADR 260918-005: Make QC State Freshness-Gated and Non-Destructive

- Status: Accepted
- Date: 2026-09-18

## Context

Retranslation, re-export, or configuration changes can leave stored QC fields describing an older
translation. A rejected QC correction must not discard a valid ordinary translation and fall back
to raw source text.

## Decision

Compare the stored `QcReviewedText` with the freshly reconstructed effective translation before
trusting any QC score or correction. When QC state is stale, disabled, or absent, package the normal
`Translated` value. When a fresh QC score rejects `QcTranslated`, discard only the QC shortcut and
continue with normal translated-text reconstruction.

## Consequences

QC data self-invalidates when the underlying translation changes. Packaging remains non-destructive
and cannot turn a QC rejection into an untranslated raw-text fallback.
