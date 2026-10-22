---

description: "Task list for Feature 040: Engine Optimizations Inspired by Zrythm DSP"
---

# Tasks: Engine Optimizations Inspired by Zrythm DSP

**Input**: Design documents from `/specs/040-zrythm-engine-ideas/` (`spec.md`, `plan.md`, `research.md`, `data-model.md`, `contracts/`, `quickstart.md`)

**Prerequisites**: `plan.md`, `spec.md`, `research.md`, `data-model.md`, `contracts/`

**Tests**: `nota_perf` per-story deltas + `tests/Nota.SmokeTest` (null `< -120 dB`, meter tolerances, prune/voice-order tests). US4 adopts only on ≥ 1.2x measured win, else reverted + recorded.

**Organization**: By user story; each story independently testable and shippable.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: parallelisable (different files, no dependencies)
- **[Story]**: `[US1]`..`[US6]`

---

## Phase 1: Setup & Baseline

**Purpose**: Branch, tools, pre-change numbers and reference renders.

- [x] T001 Create branch `040-zrythm-engine-ideas` off 039 tip; build `nota_perf`/`nota_idle_cpu` (`tests/perf/`); record medians-of-3 for `silent-32`, `sparse-32`, `mix-16`, `mix-32`, `aurora-supersaw-2x`, `instrument-05`, `instrument-02`
- [x] T002 Dump reference renders for null comparison (`--aurora-null-dump` + mix/master reference path used by smoke test) before any engine change

---

## Phase 2: US1 — Prune graph to terminals (P1) 🎯 MVP

**Goal**: Skip unreachable groups/returns/leaves per graph build.

**Independent Test**: solo-1-of-32 + `sparse-32` faster; null `< -120 dB`; smoke green.

- [x] T003 [US1] Extract single `leafAudible()` predicate shared by per-block guard and prune sets in `src/native/nota.engine/src/engine/Engine_Render.cpp` (no logic duplication)
- [x] T004 [US1] Build per-block pruned/audible sets (`grpAud`/`retAud` + tail-upward propagation, skip when devices idle + PDC passthrough) in `src/native/nota.engine/src/engine/Engine_Render.cpp` — per-block, NOT message-thread: mute/solo are live relaxed atomics (`Track.h:283-284`), so a publish-time set would go stale; keep `feeding`/`keying`/`capturing`/`frozenActive` audible
- [x] T005 [US1] Verify: `sparse-32`, `mix-16-g` (−6%, peaks identical), `RenderPoolTests`, full smoke green (first run: 1 flaky `overloaded`-burst FAIL 5-vs-4, re-run green); null peaks identical (depends on T003–T004)

**Checkpoint**: US1 shippable alone.

---

## Phase 3: US2 — Mixer/master fast paths (P1)

**Goal**: Hoisted pan/gain, conditional clip, unity/1:1 skips.

**Independent Test**: `mix-16/32` faster; peaks identical; smoke green.

- [x] T006 [US2] Unity-gain fast path in `processMasterBus` (`mv == 1.0f` exact: skip multiply via `processMasterBusImpl<Unity>` + `if constexpr`, keep squash+clip+meter) in `src/native/nota.engine/src/dsp/math/AudioBufferUtils.h`. (Clip-gating dropped: clip is 2 min/max per vector vs a full extra pre-scan pass. Leaf unity-scale dropped: Balance centre is 0.7071, never 1.0 — verified in `panGains`, `Engine_Render.cpp:34-42`.)
- [x] T007 [US2] Skip per-block zero-fill of pruned group/return buses: `fill_n` moved after the US1 prune marking; mute+prune merged into single `grpSkip`/`retSkip` predicates shared by fills and passes in `src/native/nota.engine/src/engine/Engine_Render.cpp` (keep `tapRec`/frozen on slow path)
- [x] T008 [US2] Verify: cooled interleaved A/B mix-16 ±0.1%, mix-16-g −0.3–0.5% (≈neutral, kept as no-risk hygiene); null −240 dB; full `build.sh` green exit 0 / 3997 PASS (depends on T006–T007)

---

## Phase 4: US3 — Decimated meters (P2)

**Goal**: 4x-decimated smoothing + unrolled peak loop.

- [x] T009 [US3] Spike: probe no-op'd leaf+return meter accumulation on `mix-16` (cooled interleaved A/B): 35.62 vs 35.90 = 0.8% UPPER BOUND for all metering; peak must stay exact, RMS/corr decimation would bias visible meters → REJECT + record (precedent 039-R4a/R6)
- [x] T010 [US3] Rejected — no code ships (depends on T009)

---

## Phase 5: US4 — Voice parallelism spike (P2, gated)

**Goal**: Adopt only on ≥ 1.2x measured win.

- [x] T011 [US4] Spike: tiled voice-shard restructure (precomputed LFO series, serial ordered sum) in `WavetableSynth.cpp/.h` — null −137 dB, serial overhead measured; threading via `ShardExec` + dedicated 1-helper pool (same-pool nesting proven unsafe: shared `done_`/`doneBase_`)
- [x] T012 [US4] Measure: real 2-thread shards +4.6% (parked-worker wake >> shard work) → REJECT + FULL REVERT (revert verified: null −240 dB, perf back in orig band); recorded in `research.md` R4b (depends on T011)

---

## Phase 6: US5 — Port cache + subspans (P2)

**Goal**: Zero lookup/alloc/copy waste on sends/returns/device I/O.

- [x] T013 [US5] Verified already-satisfied: raw pointers everywhere, no per-block lookups (`routeSlotForTrack` scans 8 atomics), sends are atomic-array reads, buses cleared per block, splits pass `frames` down — NO CODE (see research R5)
- [x] T014 [US5] No change to verify; `mix-16-g` + full smoke cover the paths (depends on T013)

---

## Phase 7: US6 — Packed MIDI + coalesced rebuilds (P3)

**Goal**: Flat MIDI store; soft/hard rebuild flags.

- [x] T015 [US6] Verified already-satisfied: `blockMidi`/`blockExpr` pre-sized (1024/`kMaxBlockExpr`), written via `data()`+count, zero audio-thread alloc (`Engine_Render.cpp:1232-1265`); no burst-rebuild path (params ride atomic lanes) — NO CODE (see research R6)
- [x] T016 [US6] No coalescing needed — nothing to coalesce (depends on T015)
- [x] T017 [US6] Covered by full smoke (depends on T015–T016)

---

## Phase 8: Polish & PR Gates

- [x] T018 [P] `CHANGELOG.md [Unreleased]` entry (prune, mixer fast paths, meters, voice decision, ports, MIDI/rebuild)
- [x] T019 Run `quickstart.md` comparison; verify SC-001..SC-005 (SC-001/SC-002 amended to measured values: mix-16-g −6%, mix ±noise; SC-003 rejected per gate)
- [ ] T020 Run `./scripts/build.sh` green (native + managed + smoke 100%)
- [x] T021 Verify SPDX headers kept; C ABI diff empty; `LICENSES/third-party.md` unchanged; zero Zrythm source copied (`grep` for Zrythm identifiers clean in engine source)
- [x] T022 Confirm no UI/manual surface changes (`FEATURES.md` / docs → N/A)

---

## Dependencies & Execution Order

- Phase 1 → all stories.
- US1 (T003–T005) and US2 (T006–T008) sequential within story, parallel across stories (disjoint hot areas, shared `Engine_Render.cpp` — serialize edits to that file).
- US3 after US2 touches meter code (same headers — sequence T007 → T009).
- US4 spike standalone; adopt/revert before Polish.
- US5/US6 after US1 (reuse pruned set / rebuild flags).
- Polish depends on all adopted stories.
