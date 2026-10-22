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

- [ ] T006 [US2] Unity-gain fast path in `processMasterBus` (`mv == 1.0f` exact: skip multiply, keep squash+clip+meter) in `src/native/nota.engine/src/dsp/math/AudioBufferUtils.h`. (Clip-gating dropped: clip is 2 min/max per vector vs a full extra pre-scan pass. Leaf unity-scale dropped: Balance centre is 0.7071, never 1.0 — verified in `panGains`, `Engine_Render.cpp:34-42`.)
- [ ] T007 [US2] Skip per-block zero-fill of pruned group/return buses: move `groupBus_`/`returnBus_` `fill_n` after the US1 prune marking in `src/native/nota.engine/src/engine/Engine_Render.cpp` and fill only audible buses (pruned buses are never read — both passes skip before reading)
- [ ] T008 [US2] Verify: `mix-16`, `mix-32`, meter tests, null `< -120 dB` (depends on T006–T007)

---

## Phase 4: US3 — Decimated meters (P2)

**Goal**: 4x-decimated smoothing + unrolled peak loop.

- [ ] T009 [US3] Spike first: `StereoMeterAccumulator` (`SimdAudio.h`) computes exact peak/RMS/L·R reductions — peak decimation would under-read transients, so it is NOT permitted. Measure meter share via no-op variant; adopt only an exact-preserving reduction (e.g. skip `vprod`/L·R lane where the consumer folds L/R only, or narrower tail), else REJECT + record (precedent 039-R4a/R6)
- [ ] T010 [US3] Verify meter tolerances + `mix-16` delta (depends on T009)

---

## Phase 5: US4 — Voice parallelism spike (P2, gated)

**Goal**: Adopt only on ≥ 1.2x measured win.

- [ ] T011 [US4] Spike: voice-shard render with serial ordered sum + thresholds (`>= 2` voices, `>= 16` frames) in one heavy synth (Aurora first: `src/native/nota.engine/src/devices/instruments/WavetableSynth.cpp`), serial fallback + nested-dispatch guard
- [ ] T012 [US4] Measure `aurora-supersaw-2x`/`instrument-05`: adopt (wire remaining paths + null `< -120 dB`) or REVERT + record in `research.md` (depends on T011)

---

## Phase 6: US5 — Port cache + subspans (P2)

**Goal**: Zero lookup/alloc/copy waste on sends/returns/device I/O.

- [ ] T013 [US5] Message-thread port source lists + prepare-time raw pointers; `[offset, offset+nframes)` in-place processing with clear-outs-once semantics in send/return/device paths
- [ ] T014 [US5] Verify: `mix-32`, `effect-NN (x8)`, null `< -120 dB` (depends on T013)

---

## Phase 7: US6 — Packed MIDI + coalesced rebuilds (P3)

**Goal**: Flat MIDI store; soft/hard rebuild flags.

- [ ] T015 [US6] Flat preallocated MIDI event buffer for `RenderScratch::blockMidi` (`Engine.h`, `Engine_Render.cpp` `computeBlockMidi`); `clear`-keeps-capacity, swap-based sort/filter
- [ ] T016 [US6] Soft (latencies) vs hard (build+prune) rebuild coalescing around `inheritGraphLevel()` (max one hard rebuild per block)
- [ ] T017 [US6] Verify: MIDI-burst test, RTSan/`NOTA_RT_SAFE` clean, smoke green (depends on T015–T016)

---

## Phase 8: Polish & PR Gates

- [ ] T018 [P] `CHANGELOG.md [Unreleased]` entry (prune, mixer fast paths, meters, voice decision, ports, MIDI/rebuild)
- [ ] T019 Run `quickstart.md` comparison; verify SC-001..SC-005
- [ ] T020 Run `./scripts/build.sh` green (native + managed + smoke 100%)
- [ ] T021 Verify SPDX headers kept; C ABI diff empty; `LICENSES/third-party.md` unchanged; zero Zrythm source copied (`grep` for Zrythm identifiers)
- [ ] T022 Confirm no UI/manual surface changes (`FEATURES.md` / docs → N/A)

---

## Dependencies & Execution Order

- Phase 1 → all stories.
- US1 (T003–T005) and US2 (T006–T008) sequential within story, parallel across stories (disjoint hot areas, shared `Engine_Render.cpp` — serialize edits to that file).
- US3 after US2 touches meter code (same headers — sequence T007 → T009).
- US4 spike standalone; adopt/revert before Polish.
- US5/US6 after US1 (reuse pruned set / rebuild flags).
- Polish depends on all adopted stories.
