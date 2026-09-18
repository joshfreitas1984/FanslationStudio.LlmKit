# Packaging QC Rejection and Raw Fallback Investigation

The current packaging behavior is documented in [the packaging guide](../features/packaging/packaging-workflows.md).

## Incident summary

Before the 2026-09-16 fix, `PrefabTextWorkflow` and `DynamicStringWorkflow` treated a rejected QC
correction as a genuine packaging failure. They discarded the valid pre-QC `Translated` value and
fell back to raw source text. This shipped untranslated UI text and contributed to a downstream
startup failure.

## Root cause

The score gate was applied to the entire packaging decision instead of only deciding whether the
fresh `QcTranslated` shortcut could be used. CSV and JSON workflows already retained ordinary
translated text when a QC correction was rejected, so they did not share the defect.

## Correct behavior

A fresh QC score failure rejects only `QcTranslated`; packaging continues with ordinary translated
text. A genuine missing or unsafe translation remains a `RawFallback`. The two outcomes must remain
separate in reporting and tests.

See the feature guide for the workflow matrix and the downstream incident report for the original
runtime symptoms.
