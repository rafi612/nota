---

description: "Task list for Feature 039: Aurora Optimizations Inspired by Vital DSP"
---

# Tasks: Aurora Optimizations Inspired by Vital DSP

**Input**: Design documents from `/specs/039-vital-aurora-ideas/` (`spec.md`, `plan.md`, `research.md`, `data-model.md`, `contracts/`, `quickstart.md`)

**Prerequisites**: `plan.md` (required), `spec.md` (required for user stories), `research.md`, `data-model.md`, `contracts/`

**Tests**: Included — `AuroraNullTests` per-tier gates, `DspSilenceBypassTests` re-verification, `nota_perf` A/B, aliasing render for US4.

**Organization**: Tasks are grouped by user story (`US1`..`US5`) so each phase is independently testable and shippable.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (`[US1]`, `[US2]`, `[US3]`, `[US4]`, `[US5]`)

---

## Phase 1: Setup & Baseline Measurement

**Purpose**: Record pre-change benchmarks and reference renders so every phase is quantitatively proven.

- [x] T001 Build `nota_perf` (`cmake -S tests/perf -B /tmp/nota-perf-tools -DNOTA_ENGINE_DIR=$PWD/src/native/nota.engine -DNOTA_ENGINE_BUILD_DIR=$PWD/src/native/nota.engine/build`) and record medians-of-3 for `aurora-supersaw-2x`, `instrument-05`, `silent-32`, `sparse-32`, `mix-16`
- [x] T002 Dump baseline Aurora reference renders (`dotnet run --project tests/Nota.SmokeTest -c Release -- --aurora-null-dump /tmp/aurora-ref-039`) for cross-build `< -120 dB` comparison

---

## Phase 2: User Story 1 - Chorus and LFOs cost no per-sample transcendentals (Priority: P1) 🎯 MVP

**Goal**: Replace per-sample `std::sin`/`std::cos` in `chorus()` and LFO sine evaluation with per-block re-anchored complex rotation, plus bit-identical skip of inaudible LFOs.

**Independent Test**: `AuroraNullTests` (`--aurora-null` and `--aurora-null-compare`, `< -120 dB` on all 10 presets), `DspSilenceBypassTests` (`maxdiff 0.0`), `nota_perf` A/B.

### Implementation for User Story 1

- [x] T003 [US1] `WavetableSynth::chorus()`: seed tap phasors with one `sin`+`cos` per block, advance per sample by complex rotation (`s' = s·cd − c·sd`, `c' = s·cd + c·sd`); extend the T004 idle path to advance rotators by `frames · δ` in `src/native/nota.engine/src/devices/instruments/WavetableSynth.cpp`
- [x] T004 [US1] LFO sine via per-block re-anchored rotator (sine shape only; tri/sqr/S&H paths untouched) and skip evaluation with exact `0.0` when `lfoDepth == 0` (old path produced exact ±0.0, provably output-identical through all uses), in `src/native/nota.engine/src/devices/instruments/WavetableSynth.cpp`
- [x] T005 [US1] Verify `AuroraNullTests` (`< -120 dB`), `DspSilenceBypassTests`, and `nota_perf` (`aurora-supersaw-2x`, `instrument-05`) improvement (depends on T003–T004)

**Checkpoint**: US1 complete — zero per-sample transcendentals in Aurora modulation, bit-identical output.

---

## Phase 3: User Story 2 - Tan lookup table for filter coefficients (Priority: P1)

**Goal**: Remove `std::tan` from the audio thread in `setSvf` via a process-static 2048-entry table.

**Independent Test**: `AuroraNullTests` `< -120 dB` (Catmull fallback documented at `< -90 dB`); `nota_perf` A/B.

### Implementation for User Story 2

- [x] T006 [US2] Add process-static 2048-entry `tan` table (built once, read-only) with linear interpolation in `WavetableSynth::setSvf()` in `src/native/nota.engine/src/devices/instruments/WavetableSynth.h` and `src/native/nota.engine/src/devices/instruments/WavetableSynth.cpp`
- [x] T007 [US2] Verify null gate; if linear fails `< -120 dB`, switch to Catmull and record the `< -90 dB` gate explicitly (depends on T006)

**Checkpoint**: US2 complete — no `tan` on the audio thread.

---

## Phase 4: User Story 3 - Branchless integer oscillator phases (Priority: P2)

**Goal**: `uint32` unison phase accumulators (free overflow wrap, shift-out index) in `oscStack`.

**Independent Test**: `AuroraNullTests` with the US3 gate `< -90 dB`; determinism self-null stays −240 dBFS; `nota_perf` A/B.

### Implementation for User Story 3

- [x] T008 [US3] `uint64_t` unison phases (NOT `uint32_t`: 32-bit increment truncation would drift ~1e-5 cycles over a 2.5 s render and fail the gate) in `src/native/nota.engine/src/devices/instruments/WavetableSynth.h` and `src/native/nota.engine/src/devices/instruments/WavetableSynth.cpp` (adapting `readMorph`, `warpRead`, `oscStack`, note-on phase init)
- [x] T009 [US3] Verify `< -90 dB` null, determinism, and `nota_perf` (depends on T008)

**Checkpoint**: US3 complete — branchless phase advance.

---

## Phase 5: User Story 4 - Band-limited mip wavetables (Priority: P2, sound-improving)

**Goal**: Per-voice, per-block mip level selection (2048/1024/512/256) cutting aliasing ≥ 10 dB with no perf regression.

**Independent Test**: Aliasing-meter render (high bright note) before/after; `nota_perf`; blind A/B sign-off in PR; full smoke green.

### Implementation for User Story 4

- [x] T010 [US4] ~~Build mip table set once (construction + `setSampleRate`) and select per voice per block~~ — REVERTED after measurement (see `research.md` R6): per-call level selection measured +11% on `aurora-supersaw-2x` (mixed L0/L1 table streams defeat the prefetcher + select-branch cost); block-uniform levels still cost +2.5–3% with zero benefit on the flagship bench (low voices force L0 for all). Reverted to the T008 code; aliasing idea kept in research for a zero-overhead selection design.
- [x] T011 [US4] ~~Verify aliasing −10 dB, no `nota_perf` regression, full smoke green~~ — NOT APPLICABLE after the T010 revert (no mip code ships; `AuroraNullTests` `< -120 dB` re-verified on the reverted tree, full smoke green in T015)

**Checkpoint**: US4 complete — less aliasing, same-or-better speed.

---

## Phase 6: User Story 5 - Unison batching spike (Priority: P3, investigational)

**Goal**: Measure, don't guess — isolated ubench decides whether 4-wide batching is adopted.

### Implementation for User Story 5

- [x] T012 [US5] Write isolated microbenchmark (same `-O3` flags) for scalar vs 4-wide unison `oscStack` reads; adopt only on ≥ 1.2× ARM64 win, else record rejection in `research.md` (precedent: 037-R4)

**Checkpoint**: US5 complete — decision recorded with numbers.

---

## Phase 7: Polish & Cross-Cutting Concerns (PR Gates)

**Purpose**: Final validation, documentation, and Nota compliance checks.

- [x] T013 [P] Add `CHANGELOG.md` `[Unreleased]` entry (rotator modulation, tan LUT, integer phases, mip aliasing fix, batching decision)
- [x] T014 Run `specs/039-vital-aurora-ideas/quickstart.md` comparison and verify SC-001..SC-004
- [x] T015 Run `./scripts/build.sh` until native + managed + `tests/Nota.SmokeTest` pass 100% green
- [x] T016 Verify AGPL-3.0-only SPDX two-line header on every modified/new file; confirm zero Vital code copied (`grep` for Vital identifiers) and no new dependencies
- [x] T017 Confirm no UI/manual surface changes (`FEATURES.md` / `nota-docs` → N/A)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 (Setup & Baseline)**: No dependencies — start immediately.
- **Phase 2 (US1 - Rotators, P1 MVP)**: Depends on Phase 1. T003 (chorus) and T004 (LFO) touch the same file but disjoint functions — sequential to keep null-gate blame clear (chorus first).
- **Phase 3 (US2 - Tan LUT, P1)**: Independent of US1 (different function) — can run in parallel, verify together in T007.
- **Phase 4 (US3 - Integer phase, P2)**: Depends on US1 (touches `oscStack`/`readMorph` phasor inputs); runs after T005 green.
- **Phase 5 (US4 - Mips, P2)**: Depends on US3 (table geometry + phase addressing); needs the aliasing meter first.
- **Phase 6 (US5 - Spike, P3)**: Independent — can run any time; decision gates any follow-up batching work (not in this feature).
- **Phase 7 (Polish & PR Gates)**: Depends on Phases 2–6.

### Parallel Opportunities

```bash
# After Phase 1:
Task T003: "chorus rotation in WavetableSynth.cpp"
Task T006: "tan LUT in WavetableSynth.cpp (disjoint function)"
Task T012: "unison batching spike (throwaway ubench, no repo changes)"
```

---

## Implementation Strategy

### MVP First (User Story 1)

1. Complete Phase 1 (baseline + reference dump).
2. Complete Phase 2 (US1: chorus rotation → LFO rotation + inaudible skip) → validate with `AuroraNullTests` (`< -120 dB`) and `aurora-supersaw-2x`.
3. Proceed to Phase 3 (US2: tan LUT), Phase 4 (US3: integer phase), Phase 5 (US4: mips with aliasing gate), Phase 6 (US5: spike), validating with `./scripts/build.sh` at each checkpoint.
