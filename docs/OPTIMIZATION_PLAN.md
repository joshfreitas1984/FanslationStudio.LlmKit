# FanslationStudio.LlmKit — Optimization & Reuse Plan

> Companion to [ARCHITECTURE.md](./ARCHITECTURE.md). This is a prioritized backlog, not a
> commitment — treat each item as a hypothesis with a cheap way to validate it before investing in
> a full refactor. Ranked roughly by (impact × confidence) / effort.

## How to use this doc

For each item: **theory → cheap experiment to validate it → what a real fix looks like → risk**.
Do the experiment first. If the theory doesn't hold up, don't do the refactor.

---

## 1. Translation throughput / batching (highest priority — this is the one you flagged)

### Current behavior (see ARCHITECTURE.md § Translation pipeline)

```
foreach file (sequential)
    foreach batch of `batchSize` lines (sequential, hard barrier)
        Task.WhenAll(unique splits in THIS batch only)   <- concurrency window
        wait for the whole batch to finish before starting the next one
```

Concurrency is capped at "however many unique strings are in one batch" (`batchSize`, currently
`100`), and every batch is a **hard synchronization barrier** — the next batch cannot start until
the slowest item in the current batch finishes, including its retries/corrections and any
sequential sub-translation (bracket pre-splits, split-character segmentation,
sentence-by-sentence correction). Files are also fully sequential — no overlap between file N's
long tail and file N+1's first requests.

### Theory

The real bottleneck isn't total request count, it's **idle time at the tail of each batch**:
whichever split needs a retry/correction/sub-split (several sequential round-trips) holds up
every other request slot in that batch from being reused, and the same happens again at the
boundary between files. If most splits finish fast and a minority need 2-4x the round trips, the
"batch of N, wait for all N" model spends a lot of wall-clock time with concurrency far below its
cap.

### Cheap experiment (do this before refactoring anything)

Add lightweight instrumentation (temporary, or behind a debug flag) rather than guessing:

1. Log, per split request: start timestamp, end timestamp, retry count, which sub-path was taken
   (direct / bracket-split / char-split / sentence-correction).
2. From that log, compute: (a) actual concurrent-in-flight count over time for a run, (b) time
   spent per batch where in-flight count < `batchSize` (i.e., the "drained but waiting" tail), (c)
   distribution of per-split latency (median vs p95) — a large median/p95 gap confirms the tail
   theory.
3. Run this on one real file from `DragonHierOverLlm/Files` end-to-end and eyeball the gap. This
   takes an evening, not a refactor, and tells you whether item below is worth doing at all.

If in-flight concurrency regularly drops to single digits before a batch completes, the theory is
confirmed and the fix below has a real payoff. If concurrency stays near `batchSize` throughout,
the bottleneck is elsewhere (e.g. server-side throughput, not client-side scheduling) and this
item should be deprioritized in favor of #2/#3.

### Proposed fix (only after the experiment confirms it)

Replace "sequential batches of N, one file at a time" with a **single continuous worker pool
across the whole run**:

- Flatten all files' unique splits into one stream/`Channel<T>` up front (still dedup by `Text`
  globally, not per-batch — see item #2).
- Run a fixed number of concurrent workers (`SemaphoreSlim`/`Parallel.ForEachAsync` with
  `MaxDegreeOfParallelism` tuned to what the backend can actually sustain — this becomes the real
  concurrency knob, decoupled from "how many lines is a batch") that pull the next item as soon as
  they finish one, instead of waiting for a whole batch to drain.
- Keep periodic buffered writes (every N completed records) but make the write itself
  non-blocking relative to in-flight requests (e.g. hand off to a background flush task rather
  than `await File.WriteAllText` inline in the hot loop).
- File boundaries become irrelevant to scheduling — a file's last few slow items overlap with the
  next file's first items automatically because everything shares one worker pool.

This is the "massive refactor" — do it as a separate branch, keep the current implementation
behind a flag until throughput is compared on a real file set (see Testing the theory, below).

### Note: overlap with `CompoundFieldSplitter` for CSV-sourced files

Since `CompoundFieldSplitter` now decomposes CSV cells at export time (splitting only at genuine
game-syntax separators — `;`, `-`, `&`, `|`, method-call `--Name`), a `TranslationSplit.Text` for a
`RegularDb` file has already had those characters stripped out as fragment boundaries by the time
it reaches `TranslateSplitAsync`. That means re-enabling `-`, `:`, `|`, `&`, or `;` in
`Config.yaml: splitCharactersList` for CSV files is now redundant at best (they won't appear in
the text to split on) and unsafe at worst if a future game/file type feeds whole untouched strings
through the same list. `SplitOnCharsIfNeededAsync` itself is still needed for literal in-sentence
formatting (`\n`, `<br>`, enumeration glyphs) and for non-CSV `TextFileType`s
(`DynamicStrings`/`LocalTextString`/`PrefabText`) that never go through
`ExportGameSpecificTextAssetsToCustomFormat` — just don't reintroduce CSV structural separators
into it.

### Secondary, smaller wins in the same area (do these regardless — low effort, no architecture change)

- **Dedup globally, not per-batch**: `uniqueSplits` grouping in `TranslateViaLlmAsync` only looks
  within the current batch. Build one dictionary of already-seen `Text → Translated` for the whole
  file (or whole run) and skip a repeat immediately, before it ever reaches the worker. With
  `batchSize=100` this is probably already catching most repeats, but it's a one-line broadening
  for zero added complexity.
- **Rework `SplitBracketsRegexIfNeededAsync` / `SplitOnCharsIfNeededAsync` — these are the actual
  "slow tail" source, not just an opportunity to parallelize.** Both functions share two problems
  beyond sequential `await`:
  1. **Sequential `foreach` + `await` per piece** — a cell that splits into 4 pieces is 4x
     latency, serialized, even though the pieces are independent. Straightforward fix:
     `Task.WhenAll` over the pieces instead of a loop.
  2. **All-or-nothing failure discards already-good work** — if *any* one piece fails validation,
     the whole function returns `(true, string.Empty)` and the caller's retry loop
     (`TranslateSplitAsync`'s `RetryCount`) re-translates **every** piece again from scratch,
     including ones that already succeeded. Because a different piece can fail on each retry,
     this can churn without ever converging, and wastes LLM calls on pieces that were already
     fine. Fix: track per-piece validity, only retry the failed piece(s), reuse cached-good
     results for the rest — same idea `TranslateSplitAsync` itself already uses at the whole-cell
     level, just not propagated down into these two helpers.
  3. **`SplitBracketsRegexIfNeededAsync` hand-rolls index/offset string surgery**
     (`modifiedRaw[..adjustedIndex] + quotedText + ...`) to build a placeholder-substituted
     sentence before re-translating it — this is a bespoke, more fragile reimplementation of what
     `CompoundFieldSplitter.Decompose`/`Reconstruct` already do safely via `{n}` templates
     (quote/bracket-balance bugs here would be easy to introduce and hard to notice, since the
     failure mode is silently wrong reconstructed text, not an exception). Worth converting this
     function to build a `Decompose`-style template + fragment list and calling `Reconstruct`,
     rather than manual substring math.

  Net effect once fixed: these two helpers stop being sequential multi-round-trip chains that can
  single-handedly hold up a whole worker-pool slot (item #1's refactor helps less than expected if
  the tail latency is actually concentrated here rather than in "which item happened to need a
  retry"). Recommend measuring this specifically in the item #1 instrumentation — tag logged
  requests with whether they came from a bracket-split/char-split/sentence-correction sub-path
  (already listed above) and check if these sub-paths are disproportionately represented in the
  tail.
- **`CorrectSentenceBySentenceAsync` is the same family of problem, plus an extra naive-splitting
  risk on top — likely the worst offender of the three.** It fires when
  `ValidationResult.RequiresSentenceBySentenceCorrection` (leftover-Chinese-characters case),
  called from inside `TranslateSplitAsync`'s own `RetryCount` loop:
  1. **Sequential per-sentence `await` loop** — identical issue to the two helpers above; the
     sentences are independent and should be `Task.WhenAll`'d.
  2. **Splits on the literal string `". "`** — this is the same class of mistake
     `CompoundFieldSplitter` was built to fix for CSV cells (naive `line.Split(',')`), just applied
     to sentences: no quote/bracket awareness (a period inside a quoted clause splits mid-thought),
     no placeholder/token awareness (a `{0}` or `#PlayerName#` token near a period isn't
     protected, unlike everywhere else in the pipeline), and it can't handle `!`/`?`-terminated or
     no-ASCII-punctuation sentences — plausible exactly in the case this function exists for
     (leftover CJK text may have no ASCII period at all).
  3. **Nests inside, rather than composes with, the outer retry loop** — if the sentence-by-
     sentence pass still fails validation afterward, control falls through to the *outer*
     `RetryCount` loop, which regenerates messages and retries the **whole cell** again from
     scratch, discarding any sentences the inner pass did fix. Worst case this is
     `RetryCount × sentence-count` LLM calls for one troublesome cell, most of them redundant —
     same "discard good partial work" issue as the two helpers above, but nested one level deeper
     so it compounds further.
  4. **Minimal-context correction prompt is a real trade-off, not just an optimization** — the
     per-sentence prompt intentionally omits the original full sentence/glossary to avoid
     re-translating everything, but that also means the model has the least context exactly when
     it's already shown it struggles with that text. Worth validating empirically (log how often
     this path actually converges vs. falls through to a full outer retry) rather than assuming
     it's a net win.

  Fix direction is the same as above: parallelize the independent per-sentence calls, and make
  the outer retry only re-attempt sentences that are still failing rather than the whole cell.
  Longer term, consider whether "sentence" splitting here could reuse `CompoundFieldSplitter`-style
  fragment boundaries (or at minimum a regex that respects placeholders/quotes) instead of a bare
  `Split(". ")`.
- **Increase `RetryCount`/backoff visibility**: 429 backoff (5s→60s, 5 retries) inside
  `TranslateMessagesAsync` is invisible to the batch scheduler — a single throttled request can
  block a worker slot for up to ~5 minutes worst case. Once on a worker-pool model this stops
  blocking *other* work, but it's worth logging when it happens either way, since it's a likely
  culprit for tail latency.

### Test plan for the refactor itself

- Keep both code paths (`TranslateViaLlmAsync` old vs new) behind a config flag during transition.
- Re-run the same instrumented log from the experiment above against the new pool-based path on
  the same file(s) and compare: total wall-clock time, average in-flight concurrency, p95 latency.
- Validate correctness separately from speed: same input file translated both ways should produce
  identical (or near-identical, modulo LLM non-determinism) `Translated` values — diff the two
  output YAMLs for anything beyond expected non-determinism.
- Only delete the old path once a full real translation run (not just a sample) has been done
  end-to-end on the new path without regressions.

---

## 2. Reuse / setup-time reduction (you want to reuse this kit on future projects)

These are lower risk than #1 and directly address "reduce time spent setting things up":

- **Extract a `dotnet new` template (or a `TextFileToSplit[]` + `Config.yaml` scaffold script)**
  for a brand-new game project: working-directory layout (`Raw/Dumped`, `Raw/Export`, `Converted`,
  `Mod`, `Glossary/`, `Config.yaml`, `ManualTranslations.yaml`), plus a starter `TextFileToSplit`
  list and empty prompt-override folder. Today this is copy-pasted by hand per game
  (`DragonHierOverLlm`, presumably `LegendOfMortalOverLlm`) — a scaffold script pays for itself on
  project #3.
- **Document the "what do I configure per new game" checklist** directly in ARCHITECTURE.md's
  extension-points section (already started) and keep it current — right now this knowledge is
  split across this repo's copilot-instructions and the downstream repo's instructions file.
- **Only one model preset exists (`Qwen25`)** — if future projects use different local models
  (Qwen3, Llama, etc.), the preset-loading code (`GetQwen25Preset`) is copy-paste-shaped, not
  actually generic. Worth a small refactor to a preset registry keyed by name/enum before adding a
  second preset, rather than after.
- **`ConfigurationExtensions.GetConfiguration` throws on missing files in several places** (API
  key required, no models configured) — good — but errors don't say *which* downstream project /
  working directory failed when this kit is used from multiple repos simultaneously in an agent
  session. Worth including `workingDirectory` in exception messages.

---

## 3. Robustness / correctness (lower urgency, do opportunistically)

- **`MergeFilesIntoTranslatedAsync` matching fallback** (`GameFileHandlingBase.cs`) still has a
  `Text`-only fallback path (documented as intentional backward-compat in this repo's
  instructions) — this is a known, accepted risk of cross-matching unrelated fragments with
  identical text across unrelated lines. Not urgent, but if translation quality bugs ever look
  like "wrong line got some other line's translation," check this path first.
- **`TranslateMessagesAsync` swallows exceptions into an empty string when
  `config.SkipLineValidation` is true** — silently produces blank translations on transient HTTP
  errors instead of retrying. Low priority since `SkipLineValidation` is opt-in, but worth a log
  line at minimum.

---

## Suggested order of work

1. Instrumentation + experiment for item #1 (cheap, answers the actual question you asked).
2. Secondary batching wins (global dedup, parallel sibling sub-translations) — low effort,
   independent of whether the full worker-pool refactor happens.
3. Decide on the worker-pool refactor based on experiment results.
4. Reuse/scaffold work (item #2) whenever starting the next project, or proactively if there's
   downtime between translation runs.
5. Robustness items (#3) opportunistically.
