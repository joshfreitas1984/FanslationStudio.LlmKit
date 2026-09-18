# ADR 260918-003: Use a Continuous Worker Pool for Translation

- Status: Accepted
- Date: 2026-09-18

## Context

The original translation scheduler processed files and fixed-size batches sequentially, creating
hard barriers and leaving workers idle while one slow item completed. Local Ollama backends still
limit real throughput, but the client should avoid unnecessary file and batch barriers.

## Decision

Support a continuous worker-pool scheduler across all files. Use `MaxConcurrency`, falling back to
`BatchSize` and then the established default, to bound in-flight work. Preserve per-file duplicate
propagation and periodic durable flushes while flattening scheduling across file and batch
boundaries. Keep the batched scheduler available for compatibility.

## Consequences

The recommended path overlaps slow tails with pending work and improves restart safety through
periodic per-file persistence. Concurrency remains a cap, not a guarantee of parallel backend
throughput.
