---
description: "Task list for Feature 037: DSP Engine, Mixer & Synthesizer Performance Completion"
---

# Tasks: DSP Engine, Mixer & Synthesizer Performance Completion

**Input**: Design documents from `/specs/037-dsp-synth-engine-perf/` (`spec.md`, `plan.md`, `research.md`, `data-model.md`, `contracts/`, `quickstart.md`)

**Prerequisites**: `plan.md` (required), `spec.md` (required for user stories), `research.md`, `data-model.md`, `contracts/`

**Tests**: Included — `AuroraNullTests`, `DspSilenceBypassTests`, and `nota_perf` benchmark verification are required by `FR-001..FR-009` and `SC-001..SC-005`.

**Organization**: Tasks are grouped by user story (`US1`..`US4`) so each phase is independently testable, verifiable against null tests, and shippable as its own commit/PR.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (`[US1]`, `[US2]`, `[US3]`, `[US4]`)

---

## Phase 1: Setup & Baseline Measurement

**Purpose**: Build benchmark tools and record pre-change baseline metrics and reference renders so every subsequent phase is quantitatively proven.

- [x] T001 Build `nota_perf` and `nota_idle_cpu` in `tests/perf/CMakeLists.txt` (`cmake -S tests/perf -B build/perf-tools -DCMAKE_BUILD_TYPE=Release && cmake --build build/perf-tools`)
- [x] T002 Record baseline `nota_perf` timings (`silent-32`, `sparse-32`, `aurora-supersaw-2x`, `instrument-02`, `instrument-05`, `instrument-06`, `instrument-14`, `instrument-15`, `instrument-16`, `mix-16`) and dump baseline Aurora reference renders via `dotnet run --project tests/Nota.SmokeTest -c Release -- --aurora-null-dump`

---

## Phase 2: User Story 1 - Idle and Silent Instrument Tracks Consume Near-Zero CPU (Priority: P1) 🎯 MVP

**Goal**: Complete the `isIdle()` auto-sleep contract across all 8 synthesizers that were missing the early-return in `render()` and across `RackInstrument` / `DrumRack` chains, eliminating wasted per-sample synthesis and internal FX processing on silent tracks.

**Independent Test**: Run `nota_perf run silent-32` and `sparse-32`, and run `DspSilenceBypassTests` in `tests/Nota.SmokeTest` asserting that Aurora, Volt, Pentad, and Rack instruments sleep after tail decay and wake sample-accurately (`< -120 dB` residual) on note-on.

### Tests for User Story 1

- [x] T003 [US1] Extend `tests/Nota.SmokeTest/DspSilenceBypassTests.cs` with sleep/wake transparency and tail ring-out assertions for `WavetableSynth` (Aurora), `VoltSynth`, `Pentad`, and `RackInstrument`

### Implementation for User Story 1

- [x] T004 [P] [US1] Add `if (idle_)` early-return in `WavetableSynth::render()` (advancing `lfoPhase1_`, `lfoPhase2_`, `chorusPh_`, and `chorusW_` in $O(1)$ time) and wake on active-voice `noteExpression()` in `src/native/nota.engine/src/devices/instruments/WavetableSynth.cpp`
- [x] T005 [P] [US1] Add `if (idle_)` early-return in `VoltSynth::render()` (advancing `lfoPhase1_`, `lfoPhase2_`, `vibPhase_`) and wake on `noteExpression()` in `src/native/nota.engine/src/devices/instruments/VoltSynth.cpp`
- [x] T006 [P] [US1] Add `if (idle_)` early-return in `Pentad::render()` and `Consort::render()` (when `!seqRun_`) and wake on `noteExpression()` in `src/native/nota.engine/src/devices/instruments/Pentad.cpp` and `src/native/nota.engine/src/devices/instruments/Consort.cpp`
- [x] T007 [P] [US1] Add `if (idle_)` early-return in `OperatorSynth::render()`, `FluxSynth::render()`, `BassSynth::render()`, and `Synth::render()` in `src/native/nota.engine/src/devices/instruments/OperatorSynth.cpp`, `src/native/nota.engine/src/devices/instruments/FluxSynth.cpp`, `src/native/nota.engine/src/devices/instruments/BassSynth.cpp`, and `src/native/nota.engine/src/devices/instruments/Synth.cpp`
- [x] T008 [P] [US1] Skip idle chains (where `c.instrument->isIdle()` and all non-bypassed `c.devices` report `isIdle()`) in `RackInstrument::render()` and `DrumRack::render()` in `src/native/nota.engine/src/devices/racks/RackInstrument.cpp` and `src/native/nota.engine/src/devices/racks/DrumRack.cpp`
- [x] T009 [US1] Verify `DspSilenceBypassTests`, `AuroraNullTests` (`< -120 dB`), and `nota_perf run silent-32` / `sparse-32` improvements (depends on T003–T008)

**Checkpoint**: User Story 1 complete — silent instrument and rack tracks consume near-zero CPU and wake sample-accurately.

---

## Phase 3: User Story 2 - Nota Aurora Heavy Polyphonic Patch Optimization (Priority: P1)

**Goal**: Accelerate `WavetableSynth::readMorph`, `oscStack`, `chorus`, and silent hangover blocks while keeping all factory Aurora presets bit-identical (`< -120 dB` residual).

**Independent Test**: Run `AuroraNullTests` (`--aurora-null` and `--aurora-null-compare` against the baseline dump) asserting `< -120 dB` residual on all factory presets, and measure `nota_perf run aurora-supersaw-2x` and `instrument-05`.

### Implementation for User Story 2

- [x] T010 [US2] Replace `std::floor(x)` with non-negative integer cast in `WavetableSynth::readMorph()` in `src/native/nota.engine/src/devices/instruments/WavetableSynth.h` and hoist `mode == 0` (Warp Off), `mode == 4` (Fold), and reciprocal divisors for `mode == 2, 3` out of the per-unison loop in `WavetableSynth::oscStack()` in `src/native/nota.engine/src/devices/instruments/WavetableSynth.cpp`
- [x] T011 [US2] Convert `WavetableSynth::chorusBuf_` to a power-of-two size with bitmask wrapping (`chorusMask_`) and replace the per-tap `std::sin` loop in `WavetableSynth::chorus()` with a single trigonometric evaluation per sample using quarter-turn identities ($\{s, c, -s, -c\}$) in `src/native/nota.engine/src/devices/instruments/WavetableSynth.h` and `src/native/nota.engine/src/devices/instruments/WavetableSynth.cpp` (depends on T010)
- [x] T012 [US2] Add fast path in `WavetableSynth::render()` when no voices are active (`live == 0` at block start) and both Chorus and Reverb are inactive (`!(chorusOn && chAmt > 1e-4f) && !(reverbOn && rvAmt > 1e-4f)`), advancing LFO phases and `quietSamples_` in $O(1)$ without running `frames` empty iterations in `src/native/nota.engine/src/devices/instruments/WavetableSynth.cpp` (depends on T011)
- [x] T013 [US2] Run `AuroraNullTests` (`< -120 dB`) and `nota_perf run aurora-supersaw-2x` / `instrument-05` to verify bit-identical sound and measured speedup (depends on T010–T012)

**Checkpoint**: User Story 2 complete — Nota Aurora runs significantly faster with bit-identical output across all factory presets.

---

## Phase 4: User Story 3 - Vectorized SIMD Modal Resonators & Paired Stereo SVF (Priority: P2)

**Goal**: Vectorize the 16-mode resonator banks in `PhysicalSynth` and `Keys`, and process paired Mid+Side / L+R channels simultaneously in `WavetableSynth` and `VoltSynth` stereo SVF filters.

**Independent Test**: Run `nota_perf run instrument-02` (Physical), `instrument-16` (Keys), `instrument-05` (Aurora), `instrument-06` (Volt), and verify `< -90 dB` null residual and full smoke test pass.

### Implementation for User Story 3

- [x] T014 [P] [US3] Align modal arrays (`alignas(16)`) in `PhysicalSynth::Bank` and vectorize `PhysicalSynth::bank()` and `PhysicalSynth::bankSerial()` using 2-wide `double` SIMD (`float64x2_t` on NEON / `__m128d` on SSE2) in `src/native/nota.engine/src/devices/instruments/PhysicalSynth.h` and `src/native/nota.engine/src/devices/instruments/PhysicalSynth.cpp`
- [x] T015 [P] [US3] Align modal arrays (`alignas(16)`) in `Keys::Voice` and vectorize the modal resonator loop in `Keys::renderVoice()` using 4-wide `float` SIMD (`float32x4_t` on NEON / `__m128` on SSE) in `src/native/nota.engine/src/devices/instruments/Keys.h` and `src/native/nota.engine/src/devices/instruments/Keys.cpp`
- [x] T016 [P] [US3] ~~Implement paired 2-channel `filterPairStereo` / `svfStereo`~~ — REVERTED after measurement (see `research.md` R4): paired 2-wide `double` SVF measures 1.2–2.1× slower than 2× scalar `svf` on ARM64 (latency-bound feedback chain + compiler FMA contraction of the scalar form); `aurora-supersaw-2x` regressed ~18.6 → ~19.6 ms/s with it in place and recovered to ~18.2 after the revert. Scalar `svf`/`filterPair` traversals kept.
- [x] T017 [P] [US3] ~~Implement paired 2-channel `svfStereo` in VoltSynth~~ — REVERTED for the same reason as T016 (same kernel, same measurement; the default Volt patch does not use the stereo path anyway). Scalar `svf` traversals kept.
- [x] T018 [US3] Verify `AuroraNullTests` (`< -120 dB`) and benchmark `instrument-02`, `instrument-16`, `instrument-05`, and `instrument-06` with `nota_perf` (depends on T014–T017)

**Checkpoint**: User Story 3 complete — modal synthesis and stereo unison SVF filters execute via hardware SIMD.

---

## Phase 5: User Story 4 - Vectorized Mixer Metering, Convolution `cmac` & Proactive Multi-Core Pool (Priority: P2)

**Goal**: Replace scalar metering and master-bus loops in `Engine_Render.cpp` with single-pass SIMD routines from `AudioBufferUtils.h`, vectorize `cmac` in `Convolution.cpp` and `Chamber.cpp`, engage `rtParallel_` proactively at 65% of the buffer period, and unify $O(1)$ parameter ID lookup across synths.

**Independent Test**: Run `nota_perf` on `mix-8`, `mix-16`, `mix-32`, `effect-20` (Chamber), and run `RenderPoolTests`, `PanLawTests`, `StereoMeasureTests`, and `DspOptimizationTests` in `tests/Nota.SmokeTest`.

### Implementation for User Story 4

- [x] T019 [P] [US4] Wire `processMasterBus` and single-pass SIMD metering (`scaleStereoMeter`, `addStereoScaledMeter`, `addStereoScaledMeterMap` when `!tapRec`) into `Engine::processBlock()` and `Engine::mixGraph()`, and lower the live `rtParallel_` activation threshold to `0.65 * periodNs` (when `m >= 2 && frames >= 128`) in `src/native/nota.engine/src/engine/Engine_Render.cpp`
- [x] T020 [P] [US4] Vectorize `cmac()` complex frequency-domain multiply-accumulate using ARM NEON (`vmlaq_f32` / `vmlsq_f32`) and x86 SSE/FMA in `src/native/nota.engine/src/dsp/primitives/Convolution.cpp` and `src/native/nota.engine/src/devices/effects/Chamber.cpp`
- [x] T021 [P] [US4] Replace linear scan in `pluginParamIndexOfId()` with a static `std::unordered_map<std::string, int32_t>` in `src/native/nota.engine/src/devices/instruments/VoltSynth.cpp`, `src/native/nota.engine/src/devices/instruments/Pentad.cpp`, `src/native/nota.engine/src/devices/instruments/OperatorSynth.cpp`, `src/native/nota.engine/src/devices/instruments/FluxSynth.cpp`, and `src/native/nota.engine/src/devices/instruments/BassSynth.cpp`

**Checkpoint**: User Story 4 complete — mixer summing/metering, convolution `cmac`, parameter lookup, and multi-core pool activation are fully optimized.

---

## Phase 6: Polish & Cross-Cutting Concerns (PR Gates)

**Purpose**: Final validation, documentation, and Nota compliance checks.

- [x] T022 [P] Add `CHANGELOG.md` `[Unreleased]` entry summarizing synthesizer auto-sleep completion, Aurora hot-loop/chorus optimizations, SIMD modal resonators & stereo SVF, and mixer/convolution SIMD improvements
- [x] T023 Run `specs/037-dsp-synth-engine-perf/quickstart.md` benchmark comparison and verify measurable improvements across `silent-32`, `aurora-supersaw-2x`, `instrument-02`, `instrument-05`, `instrument-16`, and `mix-16`
- [x] T024 Run `./scripts/build.sh` until native + managed + `tests/Nota.SmokeTest` pass 100% green
- [x] T025 Verify AGPL-3.0-only SPDX two-line header on every modified/new file
- [x] T026 Confirm no new third-party dependencies (`LICENSES/third-party.md` unchanged → N/A) and no UI/manual surface changes (`FEATURES.md` / `nota-docs` → N/A)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 (Setup & Baseline)**: No dependencies — start immediately.
- **Phase 2 (US1 - Auto-Sleep Completion, P1 MVP)**: Depends on Phase 1. Tasks `T004`–`T008` touch disjoint files and can run in parallel.
- **Phase 3 (US2 - Aurora Hot-Loop & Chorus, P1)**: Depends on `T004` (same file `WavetableSynth.cpp`); tasks `T010`→`T011`→`T012` run sequentially with `AuroraNullTests` verification in `T013`.
- **Phase 4 (US3 - SIMD Resonators & Paired SVF, P2)**: `T014` (`PhysicalSynth`) and `T015` (`Keys`) can run in parallel with Phase 3; `T016` (`WavetableSynth`) runs after `T012`; `T017` (`VoltSynth`) runs after `T005`.
- **Phase 5 (US4 - Mixer SIMD, Convolution `cmac` & Pool, P2)**: `T019` (`Engine_Render.cpp`) and `T020` (`Convolution.cpp`/`Chamber.cpp`) are independent of Phases 2–4 and can run in parallel; `T021` runs after `T005`–`T007`.
- **Phase 6 (Polish & PR Gates)**: Depends on Phases 2–5.

### Parallel Opportunities

```bash
# Phase 2 parallel batch (disjoint instrument/rack files):
Task T004: "WavetableSynth idle_ early-return in WavetableSynth.cpp"
Task T005: "VoltSynth idle_ early-return in VoltSynth.cpp"
Task T006: "Pentad & Consort idle_ early-return in Pentad.cpp, Consort.cpp"
Task T007: "Operator, Flux, Bass, Synth idle_ early-return"
Task T008: "RackInstrument & DrumRack idle chain skip"

# Phase 4 & 5 parallel batch (disjoint files):
Task T014: "PhysicalSynth 2-wide double SIMD modal bank in PhysicalSynth.cpp"
Task T015: "Keys 4-wide float SIMD modal bank in Keys.cpp"
Task T019: "Engine_Render.cpp processMasterBus, SIMD meters, proactive rtParallel_"
Task T020: "Convolution.cpp & Chamber.cpp SIMD cmac()"
```

---

## Implementation Strategy

### MVP First (User Story 1 + User Story 2)
1. Complete Phase 1 (baseline measurements).
2. Complete Phase 2 (US1: auto-sleep completion across synths and racks) → validate with `DspSilenceBypassTests` and `silent-32`.
3. Complete Phase 3 (US2: Aurora `readMorph`, `oscStack`, and `chorus` bit-identical optimizations) → validate with `AuroraNullTests` (`< -120 dB`) and `aurora-supersaw-2x`.
4. Proceed to Phase 4 (US3: SIMD resonators & paired stereo SVF) and Phase 5 (US4: SIMD mixer & convolution `cmac`), validating with `./scripts/build.sh` at each checkpoint.
