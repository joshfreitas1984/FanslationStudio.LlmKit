# FanslationStudio.LlmKit — Optimization & Reuse Plan

> Companion to [ARCHITECTURE.md](./ARCHITECTURE.md). This is a prioritized backlog, not a
> commitment — treat each item as a hypothesis with a cheap way to validate it before investing in
> a full refactor. Ranked roughly by (impact × confidence) / effort.

## How to use this doc

For each item: **theory → cheap experiment to validate it → what a real fix looks like → risk**.
Do the experiment first. If the theory doesn't hold up, don't do the refactor.

---

## 1. Translation throughput / batching (highest priority — this is the one you flagged)

> **Status: implemented behind a flag, and confirmed faster on a real translation run.** See
> "What's implemented" and "Validation result" below before re-reading the original
> theory/proposal as if it were still open — the scheduling change has landed and held up in
> practice; what's left is formal head-to-head numbers before fully retiring the old path.

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

### What's implemented

`TranslationService.TranslateViaLlmAsync` is now a thin dispatcher over two schedulers, selected
by `LlmConfig.UseContinuousWorkerPool` (`Config.yaml: useContinuousWorkerPool`, default `false`):

- `TranslateViaLlmAsyncBatched` — the original scheduler described above, unchanged in behavior.
- `TranslateViaLlmAsyncPooled` — the pool-based scheduler described in "Proposed fix": every
  file is loaded up-front, each file's unique-by-`Text` splits (same per-file dedup semantics as
  the batched scheduler — translation prompts still respect each file's own
  `EnableGlossary`/`AdditionalPromptName`/etc., so this intentionally does **not** dedup across
  files beyond what the existing run-wide translation cache already does) are flattened into one
  work list across every file, and `Parallel.ForEachAsync` with
  `MaxDegreeOfParallelism = LlmConfig.MaxConcurrency ?? BatchSize ?? 20` pulls the next item as
  soon as a worker frees up — no batch barrier, no file-boundary barrier. Buffered writes still
  happen every `BatchlessBuffer` (25) completed records, now per-file under a lock instead of
  inline in a single-threaded loop. Duplicate-propagation (the batched scheduler's post-batch
  "copy first duplicate's translation to the rest" step) runs opportunistically before every
  buffered write for that file (not just once at the end), plus once more after the whole pool
  drains to catch anything translated after the last flush — scoped to the whole file instead of
  whichever batch a duplicate happened to land in (a strict improvement, since duplicates that
  were previously split across two different batches of the same file now always get linked).
  This also means a cancelled/killed run only loses the unique-split translations and duplicate
  propagation that happened since that file's last flush, not everything back to the start of the
  file — restart-safety is comparable to (arguably better than) the batched scheduler.

To try it: set `useContinuousWorkerPool: true` in `Config.yaml`, optionally tune
`maxConcurrency` (otherwise it reuses `batchSize`).

### Validation result

Confirmed faster on a real translation run in `DragonHierOverLlm` (informal — wall-clock feel,
not yet the full instrumented p95/in-flight-concurrency comparison described below). The tail-
latency theory holds up in practice: the pooled scheduler noticeably outperformed the batched one.
Formal head-to-head numbers and an output-YAML diff (see "Remaining validation work" below) are
still worth capturing before fully retiring the batched path, but confidence is now high enough to
treat `useContinuousWorkerPool: true` as the recommended setting going forward.

### Remaining validation work (nice-to-have now, not a blocker)

- Re-run the instrumentation from "Cheap experiment" above (or at minimum compare total
  wall-clock time and `Console.WriteLine` "Processed: N of M" cadence) on the same file(s) with
  `useContinuousWorkerPool: false` vs `true` to get real numbers (p95 latency, in-flight
  concurrency over time) rather than relying on the informal result above.
- Diff the resulting `Converted/*.yaml` between both paths on the same input — should be
  identical modulo LLM non-determinism (no structural differences, no missing splits).
- Once that's done, consider making `useContinuousWorkerPool: true` the default and eventually
  retiring `TranslateViaLlmAsyncBatched`.

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

### Secondary, smaller wins in the same area (done)

- **Dedup globally, not per-batch — done.** `translationCache` (in `TranslationService`) is now
  the single dedup mechanism, capped at `TranslationCacheMaxChars` (50 chars, raised from the
  original 10) rather than split into a separate uncapped run-wide cache — short/medium repeated
  strings get deduped across the whole run and across files; long one-off sentences (40-50+
  chars) are deliberately excluded so the cache doesn't grow unbounded with strings unlikely to
  repeat. Also switched to `ConcurrentDictionary<string,string>` (was a plain `Dictionary` being
  mutated from parallel workers — a real, if latent, thread-safety bug that predated this pass).
- **Rework `SplitBracketsRegexIfNeededAsync` / `SplitOnCharsIfNeededAsync` — done.** Both functions
  now translate their independent pieces via `Task.WhenAll` (parallelization) and only retry the
  piece(s) that actually failed validation, via a shared `TranslatePiecesWithRetryAsync` helper,
  instead of discarding every piece (including already-succeeded ones) on any single failure.
  `SplitBracketsRegexIfNeededAsync` also no longer hand-rolls index/offset string surgery — it now
  builds a `{n}`-placeholder template in one forward pass and calls
  `CompoundFieldSplitter.Reconstruct` to restore bracket contents, the same safe substitution used
  for CSV-cell fragments elsewhere. **This refactor also fixed a real bug**: the previous
  hand-rolled restoration discarded each bracket's actual translated content and spliced in a
  mangled substring of the internal placeholder *number* instead (`quotedText[1..^1]`), so
  bracketed text (`《...》`, `「...」`, etc.) translated via this path silently lost its
  translation. Confirmed via git history that `splitRegexPatterns` had these bracket patterns
  enabled on real `DragonHierOverLlm` runs before being disabled again — see
  `TranslationWorkflowTests.SetBracketSplitBugLinesAsInvalid` (added to flag affected lines for
  retranslation) and `TranslationServiceTests.SplitBracketsRegexIfNeededAsync_RestoresTranslatedBracketContent`
  (mocked-LLM regression test) in the respective test projects.

  Net effect: these two helpers stop being sequential multi-round-trip chains that can
  single-handedly hold up a whole worker-pool slot. Still worth measuring in a future pass whether
  bracket-split/char-split sub-paths are disproportionately represented in tail latency, but no
  further code changes are required for this item.
- **`CorrectSentenceBySentenceAsync` is the same family of problem, plus an extra naive-splitting
  risk on top — done.** It fires when `ValidationResult.RequiresSentenceBySentenceCorrection`
  (leftover-Chinese-characters case), called from inside `TranslateSplitAsync`'s own `RetryCount`
  loop:
  1. ~~Sequential per-sentence `await` loop~~ — **done**, now `Task.WhenAll`'d.
  2. ~~Splits on the literal string `". "`~~ — **done**. Replaced with a dedicated
     `SplitIntoSentences` helper that splits on `.`/`!`/`?` followed by whitespace or
     end-of-string, while tracking brace depth (never splits inside a `{n}` placeholder) and
     double-quote state (never splits inside a quoted clause) - covered by
     `TranslationServiceTests.CorrectSentenceBySentenceAsync_DoesNotSplitInsideQuotes`. Single
     quotes are intentionally not tracked (they're overwhelmingly English contraction apostrophes
     in translated output, not quote delimiters).
  3. ~~Nests inside, rather than composes with, the outer retry loop~~ — **done**. The outer retry
     loop in `TranslateSplitAsync` now keeps re-invoking `CorrectSentenceBySentenceAsync` (feeding
     the previous attempt's output back in as the next input) instead of falling through to a
     fresh whole-cell `TranslateMessagesAsync` call as soon as one correction attempt fails.
     Because `CorrectSentenceBySentenceAsync` only re-translates sentences that still contain
     Chinese characters, already-corrected sentences from a prior attempt are left untouched on
     each re-invocation - so a stubborn sentence no longer forces every already-fixed sentence in
     the same cell to be discarded and the whole cell retranslated from scratch. Only falls back
     to a normal whole-cell correction message (the old behavior) once the sentence-level retry
     budget (`RetryCount`) is exhausted and the cell is still invalid. Covered by
     `TranslationServiceTests.TranslateSplitAsync_SentenceCorrectionRetry_DoesNotFallBackToWholeCellRetranslation`
     (asserts the whole-cell translation request is only ever sent once).
  4. **Minimal-context correction prompt is a real trade-off, not just an optimization** — the
     per-sentence prompt intentionally omits the original full sentence/glossary to avoid
     re-translating everything, but that also means the model has the least context exactly when
     it's already shown it struggles with that text. Worth validating empirically (log how often
     this path actually converges vs. falls through to a full outer retry) rather than assuming
     it's a net win. Not yet done — no logging added for this specifically, and considered low
     priority relative to everything else that's landed.
- **429 backoff visibility — done.** `TranslateMessagesAsync`'s 429 backoff now logs attempt
  number, wait time per attempt, and total blocked time once backoff finishes, so a request stuck
  in backoff (up to ~5 minutes worst case: 5 retries, 5s→60s exponential) is visible in logs
  instead of only showing a generic "Backing off..." line per attempt.

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

1. ~~Instrumentation + experiment for item #1~~ — superseded; implemented and validated directly
   on a real run instead (see "Validation result" above).
2. ~~Secondary batching wins (global dedup, parallel sibling sub-translations, per-piece retry,
   `CompoundFieldSplitter`-based bracket reconstruction, quote/placeholder-aware sentence
   splitting, sentence-correction outer-retry-scoping)~~ — **all done.** The only item left in
   this section is point 4 of `CorrectSentenceBySentenceAsync` (no empirical logging of how often
   the sentence-correction path converges vs. falls through) - low priority.
3. ~~Decide on the worker-pool refactor~~ — done, `useContinuousWorkerPool: true` is the
   recommended setting going forward (see "Validation result").
4. **Remaining open items, roughly in priority order:**
   - Formal head-to-head numbers (item #1's "Remaining validation work") — nice-to-have, not
     blocking.
   - Reuse/scaffold work (item #2) whenever starting the next project, or proactively if there's
     downtime between translation runs.
   - Robustness items (#3) opportunistically.
