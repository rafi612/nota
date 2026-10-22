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

- [ ] T003 [US1] Extract single `leafAudible()` predicate shared by build-time prune and per-block guard in `src/native/nota.engine/src/engine/Engine_Render.cpp` (no logic duplication)
- [ ] T004 [US1] Build message-thread pruned/audible set on graph publish (`Graph.h/.cpp`, `Engine.h`) covering groups/returns; keep `feeding`/`keying`/`capturing`/`frozenActive` audible
- [ ] T005 [US1] Verify: `sparse-32`, solo-1-of-32, `RenderPoolTests`, full smoke; null `< -120 dB` (depends on T003–T004)

**Checkpoint**: US1 shippable alone.

---

## Phase 3: US2 — Mixer/master fast paths (P1)

**Goal**: Hoisted pan/gain, conditional clip, unity/1:1 skips.

**Independent Test**: `mix-16/32` faster; peaks identical; smoke green.

- [ ] T006 [US2] Gate `processMasterBus` clip on measured `peak > 1.0` in `src/native/nota.engine/src/dsp/math/AudioBufferUtils.h` + call site in `src/native/nota.engine/src/engine/Engine_Render.cpp`
- [ ] T007 [US2] Hoist pan/gain + unity-multiply skip + 1:1 copy-without-clear in static `PanStage`/leaf/group/return paths in `src/native/nota.engine/src/engine/Engine_Render.cpp` (keep `tapRec`/frozen on slow path)
- [ ] T008 [US2] Verify: `mix-16`, `mix-32`, meter tests, null `< -120 dB` (depends on T006–T007)

---

## Phase 4: US3 — Decimated meters (P2)

**Goal**: 4x-decimated smoothing + unrolled peak loop.

- [ ] T009 [US3] Decimate `StereoMeterAccumulator`/meter passes (`kMeterDecim = 4`) in `src/native/nota.engine/src/dsp/math/SimdAudio.h` (+ `AudioBufferUtils.h` if shared), keep FTZ/anti-denormal path, no `isfinite` on RT
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
