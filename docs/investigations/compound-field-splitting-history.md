# Compound Field Splitting: Investigation Notes

This page records historical failures that led to the current compound-field behavior. The feature
reference is [the CompoundFieldSplitter guide](../features/compound-field-splitting/compound-field-splitting.md).

## Placeholder matching

An earlier implementation merged non-structural gaps after the initial regex pass. It missed
composite cases where placeholders and punctuation were interleaved, or where a placeholder followed
a genuine literal boundary. The implementation moved placeholder patterns into the run regex so
adjacent Chinese text and placeholders are captured together. The only remaining merge pass handles
the empty gap produced by a sign-plus-digit match restart.

## Translation-cache collision

The translation-memory cache originally keyed all entries by bare `TranslationSplit.Text`.
Compound fragments could therefore reuse a whole-cell translation, or a whole cell could reuse a
fragment translation with the same source text. The fix routes compound fragments through a separate
cache and includes fragment classification in duplicate grouping and cache reads/writes. Manual
translations and glossary entries remain available to both caches as explicit overrides.

Existing corpora created before this fix may contain suspiciously long translations on
`SubIndex`-bearing fragments. Those entries need targeted retranslation rather than a full corpus
retranslation.

## Fragment-model migration cost

Changing a compound column from whole-cell text to fragment text prevents old translations from
matching automatically during export/merge. This is expected for affected compound columns and is
the reason re-export matching prefers `(Split, SubIndex, Text)` before its compatibility fallback.
