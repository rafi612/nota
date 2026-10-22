# Implementation Plan: Aurora Optimizations Inspired by Vital DSP

**Branch**: `039-vital-aurora-ideas` | **Date**: 2026-10-22 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/039-vital-aurora-ideas/spec.md`

## Summary

Port five Vital DSP techniques to Nota Aurora as clean-room re-implementations
(zero Vital code), in dependency order: (1) phasor-rotation chorus/LFO replacing
all per-sample `sin`/`cos` (P1, `< -120 dB`); (2) process-static `tan` lookup for
`setSvf` (P1, `< -120 dB`); (3) `uint32` unison phases (P2, `< -90 dB`);
(4) per-voice mip-mapped wavetables cutting aliasing ≥ 10 dB (P2, deliberate
sound improvement, aliasing gate); (5) a measurement spike for 4-wide unison
batching with a ≥ 1.2× adoption bar (P3). Paired-SVF-style ideas stay excluded
per the 037-R4 measurement.

## Nota: Read Before Planning

- **Engine work** → [`ARCHITECTURE.md`](../../ARCHITECTURE.md): AR-4 lock-free queue, AR-5 immutable snapshots, AR-6 audio-thread restrictions, AR-7 pure C ABI, AR-9 `Transport` is the only source of musical time.
- **Build / packaging** → [`BUILD.md`](../../BUILD.md).
- **Anything** → [`CONTRIBUTING.md`](../../CONTRIBUTING.md): licensing (AGPL-3.0-only headers; no Vital code — GPL-3.0-or-later — enters the repo), dependency policy, PR expectations.

## Technical Context

**Language/Version**: C++20 (native engine in `src/native/nota.engine/`), C# / .NET 10 (`tests/Nota.SmokeTest`).

**Primary Dependencies**: None new. Uses existing `<arm_neon.h>` / `<immintrin.h>`
via `src/dsp/math/Simd.h` only where measured faster; rotators and tables are
scalar by design (they remove transcendentals, which dominate).

**Storage**: N/A in the product sense; in-memory DSP state only (rotator pairs,
one 2048-float tan table, mip table set built once at construction).

**Audio-Thread Impact** *(required — state explicitly)*: Strictly **AR-6**:
rotator seeding/rotation is register/stack arithmetic; the tan table and mip
tables are built once outside the audio thread (construction / `setSampleRate` /
first-use on a non-audio thread) and read-only afterwards; integer phases and
mip selection are branchless arithmetic. No allocs, locks, or I/O added.

**Testing**: `tests/perf/nota_perf` (`aurora-supersaw-2x`, `instrument-05`,
`silent-32`, `sparse-32`, `mix-16`); `tests/Nota.SmokeTest` (`AuroraNullTests`
`< -120 dB` / `< -90 dB` per tier, `DspSilenceBypassTests`) via
`scripts/build.sh`; aliasing-meter render for US4.

**Target Platform**: macOS 13+ (universal `arm64` + `x86_64`), Linux (`x86_64`,
`aarch64`), Windows 10/11 (`x64`, `arm64`).

**Project Type**: Real-time DAW native engine shared library (`libnota_engine`).

**Performance Goals**:
- ≥ 5% further `aurora-supersaw-2x` reduction on top of the 037 baseline (US1+US2).
- No scenario regresses beyond run-to-run noise (medians of 3).
- US4: aliasing energy −10 dB or better on high bright notes.

**Constraints**:
- Tier-1 null `< -120 dB`; Tier-2 (integer phase) `< -90 dB`; US4 aliasing gate.
- Zero Vital code; `LICENSES/third-party.md` unchanged; C ABI untouched.

**Scale/Scope**: 2 C++ engine files (`WavetableSynth.h/.cpp`) + smoke-test
extensions + `CHANGELOG.md`.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- **I. Real-Time Audio Safety (AR-6)**: PASS — register/stack arithmetic and
  read-only tables; nothing allocated or locked on the audio thread.
- **II. Boundary Discipline (AR-7, JUCE)**: PASS — `nota_engine.h` and
  `pluginhost/` untouched.
- **III. Licensing Hygiene**: PASS — clean-room re-implementation, no Vital code
  copied, no new dependency; every touched file keeps its AGPL-3.0-only header.
- **IV. Verification Gate**: PASS — `AuroraNullTests` + `DspSilenceBypassTests`
  + `nota_perf` A/B at every checkpoint; `./scripts/build.sh` green.
- **V. One Logical Change per PR**: PASS — phases map to US1..US5, each shippable.
- **VI. Documentation Stays in Sync**: PASS — `CHANGELOG.md` `[Unreleased]`
  entry planned; engine-only, no UI/manual surface.
- **VII. Spec-First**: PASS — spec, research, data-model, contracts, quickstart,
  plan, tasks completed.
- **VIII. Safety Rules Never Weakened**: PASS — no exceptions requested.

## Project Structure

### Documentation (this feature)

```text
specs/039-vital-aurora-ideas/
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
└── devices/
    └── instruments/
        ├── WavetableSynth.h       # rotator state, tan LUT decl, uint32 phases, mip decl
        └── WavetableSynth.cpp     # chorus/LFO rotation, inaudible-LFO skip, tan LUT,
                                   #   integer phases, mip selection + tables

tests/
└── Nota.SmokeTest/
    ├── AuroraNullTests.cs         # unchanged (gate reused per tier)
    └── DspSilenceBypassTests.cs   # unchanged (sleep/wake re-verified)
```

**Structure Decision**: Aurora-only optimization inside the existing modular C++20
translation unit plus the existing smoke-test harness; no new binaries, flags, or
benchmark scenarios (the US4 aliasing check is a throwaway render, not a gate).

## Complexity Tracking

> No Constitution Check violations — nothing to justify. Deliberate sound change
> (US4 mips) is fenced by an aliasing gate + reviewer A/B sign-off, not a null.
