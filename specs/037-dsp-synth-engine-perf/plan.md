# Implementation Plan: DSP Engine, Mixer & Synthesizer Performance Completion

**Branch**: `037-dsp-synth-engine-perf` | **Date**: 2026-10-22 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/037-dsp-synth-engine-perf/spec.md`

## Summary

Complete the real-time DSP optimization of Nota's C++20 audio engine and built-in synthesizers across four self-contained, verifiable increments:
1. **Auto-Sleep Contract Completion (P1 / MVP)**: Wire the missing `if (idle_)` fast-paths in all 8 synthesizers (`WavetableSynth`, `VoltSynth`, `Pentad`, `Consort`, `OperatorSynth`, `FluxSynth`, `BassSynth`, `Synth`) and skip idle chains in `RackInstrument` and `DrumRack`, eliminating ~95–100% of wasted CPU on silent instrument tracks while preserving free-running LFO/delay phases.
2. **Nota Aurora Hot-Loop & Chorus Optimization (P1)**: Eliminate `std::floor` in `readMorph`, hoist `warpRead` mode dispatch out of the unison loop in `oscStack`, convert `chorusBuf_` to a power-of-two bitmask ring, and replace 4 per-sample `std::sin` calls in `WavetableSynth::chorus` with a single trig evaluation + quarter-wave identities (100% bit-identical, `< -120 dB`).
3. **SIMD Modal Resonators (P2)**: Vectorize the 16-mode resonator banks in `PhysicalSynth` (2-wide `double` SIMD) and the 24-mode banks in `Keys` (4-wide `float` SIMD, zero-padded tail). *Paired stereo SVF for `WavetableSynth`/`VoltSynth` was implemented and then reverted — see `research.md` R4: on ARM64 the paired step measures 1.2–2.1× slower than scalar (latency-bound feedback chain + compiler FMA contraction of the scalar form).*
4. **SIMD Mixer Metering, Convolution `cmac` & Proactive Multi-Core Pool (P2)**: Connect `AudioBufferUtils.h` single-pass SIMD routines (`processMasterBus`, `scaleStereoMeter`, `addStereoScaledMeterMap`) in `Engine_Render.cpp`, vectorize `cmac` in `Convolution.cpp`/`Chamber.cpp`, engage `rtParallel_` proactively at 65% of the buffer period (`kRtEngageLoad = 0.65`), and unify $O(1)$ hash-map parameter lookup in `VoltSynth`, `Pentad`, `OperatorSynth`, `FluxSynth`, and `BassSynth`.

## Nota: Read Before Planning

- **Engine work** → [`ARCHITECTURE.md`](../../ARCHITECTURE.md): AR-4 lock-free queue, AR-5 immutable snapshots, AR-6 audio-thread restrictions, AR-7 pure C ABI, AR-9 `Transport` is the only source of musical time.
- **Build / packaging** → [`BUILD.md`](../../BUILD.md).
- **Anything** → [`CONTRIBUTING.md`](../../CONTRIBUTING.md): licensing (AGPL-3.0-only headers), dependency policy, PR expectations.

## Technical Context

**Language/Version**: C++20 (native engine in `src/native/nota.engine/`), C# / .NET 10 (`tests/Nota.SmokeTest`).

**Primary Dependencies**: None new. Uses existing `<arm_neon.h>` / `<immintrin.h>` via `src/dsp/math/Simd.h`, `SimdAudio.h`, `AudioBufferUtils.h`, and `FastMath.h`.

**Storage**: N/A (in-memory real-time DSP only).

**Audio-Thread Impact** *(required — state explicitly)*: Strictly adheres to **AR-6** — zero heap allocations, zero locks, and zero blocking I/O on the audio thread. `WavetableSynth::chorusBuf_` power-of-two sizing occurs only in the constructor and `setSampleRate()`. Static `unordered_map` initialization in `pluginParamIndexOfId()` happens on the control/UI thread during preset/automation lookup, never on the audio thread.

**Testing**: `tests/perf/nota_perf` and `tests/perf/nota_idle_cpu` for performance benchmarks; `tests/Nota.SmokeTest` (`DspSilenceBypassTests`, `AuroraNullTests`, `RenderPoolTests`, `PanLawTests`, `StereoMeasureTests`) via `scripts/build.sh`.

**Target Platform**: macOS 13+ (universal `arm64` + `x86_64`), Linux (`x86_64`, `aarch64`), Windows 10/11 (`x64`, `arm64`).

**Project Type**: Real-time DAW native engine shared library (`libnota_engine`).

**Performance Goals**:
- $\ge 70\%$ reduction in render time on silent instrument tracks (`silent-32`) once tails settle.
- $15\%\text{–}25\%$ reduction in `aurora-supersaw-2x` and `instrument-05` render time on top of `036-aurora-perf` Tier-1.
- $30\%\text{–}50\%$ reduction in `instrument-02` (Physical) and `instrument-16` (Keys) render time.
- Zero cold-start overload stutters when starting multi-track playback (`rtParallel_` engages at 65% load).

**Constraints**:
- Tier-1 optimizations bit-identical (`< -120 dB` residual in `AuroraNullTests`).
- SIMD modal/convolution optimizations `< -90 dB` residual.
- 100% preservation of `include/nota/nota_engine.h` C ABI (AR-7).

**Scale/Scope**: 14 C++ engine files + 1 smoke test file (`DspSilenceBypassTests.cs`) + `CHANGELOG.md`.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- **I. Real-Time Audio Safety (AR-6)**: PASS — all audio-thread changes are pure register/stack arithmetic and SIMD intrinsics; zero allocations or locks on the audio thread.
- **II. Boundary Discipline (AR-7, JUCE)**: PASS — `include/nota/nota_engine.h` and `pluginhost/` are untouched.
- **III. Licensing Hygiene**: PASS — all files carry the two-line `AGPL-3.0-only` SPDX header; zero new dependencies.
- **IV. Verification Gate**: PASS — extends `tests/Nota.SmokeTest/DspSilenceBypassTests.cs` and verifies with `scripts/build.sh` and `nota_perf`.
- **V. One Logical Change per PR**: PASS — structured into 4 independent user-story phases that can ship as separate PRs or commits.
- **VI. Documentation Stays in Sync**: PASS — `CHANGELOG.md` `[Unreleased]` entry planned; invisible optimization requires no UI/manual changes.
- **VII. Spec-First**: PASS — `spec.md`, `research.md`, `data-model.md`, `contracts/`, `quickstart.md`, `plan.md`, and `tasks.md` completed.
- **VIII. Safety Rules Never Weakened**: PASS — no exceptions requested.

## Project Structure

### Documentation (this feature)

```text
specs/037-dsp-synth-engine-perf/
├── spec.md
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── checklists/
│   └── requirements.md
├── contracts/
│   └── dsp-perf-contracts.md
└── tasks.md
```

### Source Code Touched (repository root)

```text
src/native/nota.engine/src/
├── devices/
│   ├── instruments/
│   │   ├── WavetableSynth.h       # readMorph floor removal, chorusMask_, paired SVF decl
│   │   ├── WavetableSynth.cpp     # idle_ early-return, oscStack mode hoist, power-of-2 chorus + 1x sincos, paired SVF
│   │   ├── VoltSynth.h            # paired stereo SVF decl
│   │   ├── VoltSynth.cpp          # idle_ early-return, paired stereo SVF, O(1) pluginParamIndexOfId
│   │   ├── Pentad.cpp             # idle_ early-return, O(1) pluginParamIndexOfId
│   │   ├── Consort.cpp            # idle_ early-return (when !seqRun_)
│   │   ├── OperatorSynth.cpp      # idle_ early-return, O(1) pluginParamIndexOfId
│   │   ├── FluxSynth.cpp          # idle_ early-return, O(1) pluginParamIndexOfId
│   │   ├── BassSynth.cpp          # idle_ early-return, O(1) pluginParamIndexOfId
│   │   ├── Synth.cpp              # idle_ early-return
│   │   ├── PhysicalSynth.h        # alignas(16) on Bank modal arrays
│   │   ├── PhysicalSynth.cpp      # 2-wide double SIMD in bank() and bankSerial()
│   │   ├── Keys.h                 # alignas(16) on Voice modal arrays
│   │   └── Keys.cpp               # 4-wide float SIMD in renderVoice() modal loop
│   ├── racks/
│   │   ├── RackInstrument.cpp     # skip idle chains (instrument + devices all idle)
│   │   └── DrumRack.cpp           # skip idle pad chains (instrument + devices + decay VCA idle)
│   └── effects/
│       └── Chamber.cpp            # SIMD vectorized cmac()
├── dsp/
│   └── primitives/
│       └── Convolution.cpp        # SIMD vectorized cmac()
└── engine/
    └── Engine_Render.cpp          # processMasterBus, scaleStereoMeter/addStereoScaledMeterMap, proactive rtParallel_ (65% load)

tests/
└── Nota.SmokeTest/
    └── DspSilenceBypassTests.cs   # + instrument & rack auto-sleep/wake transparency tests
```

**Structure Decision**: Engine-only optimization inside existing modular C++20 translation units (`src/devices/`, `src/dsp/`, `src/engine/`) and existing smoke-test harness (`tests/Nota.SmokeTest/`).

## Complexity Tracking

> No Constitution Check violations — nothing to justify.
