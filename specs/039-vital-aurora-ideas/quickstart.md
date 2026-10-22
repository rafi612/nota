# Quickstart: Validating Vital-inspired Aurora Optimizations

**Feature**: `039-vital-aurora-ideas` | **Date**: 2026-10-22

All commands run from the `nota/` repository root.

---

## 1. Baseline (before any DSP change)

```sh
cmake -S tests/perf -B /tmp/nota-perf-tools -DNOTA_ENGINE_DIR=$PWD/src/native/nota.engine -DNOTA_ENGINE_BUILD_DIR=$PWD/src/native/nota.engine/build -DCMAKE_BUILD_TYPE=Release
cmake --build /tmp/nota-perf-tools
for s in aurora-supersaw-2x instrument-05 silent-32 sparse-32 mix-16; do /tmp/nota-perf-tools/nota_perf run "$s" 2; done
# median of 3 runs each; record wall_ms_per_s
dotnet run --project tests/Nota.SmokeTest -c Release -- --aurora-null-dump /tmp/aurora-ref-039
```

## 2. Verify after each user story

```sh
dotnet run --project tests/Nota.SmokeTest -c Release -- --aurora-null          # determinism, -120 dB self-null
dotnet run --project tests/Nota.SmokeTest -c Release -- --aurora-null-compare /tmp/aurora-ref-039   # cross-build gate
for s in aurora-supersaw-2x instrument-05; do /tmp/nota-perf-tools/nota_perf run "$s" 3; done
```

## 3. Aliasing check (US4 mips only)

Render a high sine-sweep note through a bright wavetable before/after; compare
harmonic vs inharmonic energy ratio (≥ 10 dB improvement expected).

## 4. Full gate

```sh
./scripts/build.sh
```

Native + managed + `Nota.SmokeTest` (incl. `AuroraNullTests`,
`DspSilenceBypassTests`) must pass 100% green.
