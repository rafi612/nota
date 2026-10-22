# Quickstart: Validating DSP Engine, Mixer & Synthesizer Optimizations

**Feature**: `037-dsp-synth-engine-perf` | **Date**: 2026-10-22

All commands run from the `nota/` repository root.

---

## 1. Build and Run `nota_perf` Benchmarks (Before & After)

```sh
cmake -G Ninja -S tests/perf -B build/perf-tools -DCMAKE_BUILD_TYPE=Release
cmake --build build/perf-tools --target nota_perf nota_idle_cpu

# Key scenarios to compare before and after:
./build/perf-tools/nota_perf run silent-32 2
./build/perf-tools/nota_perf run sparse-32 2
./build/perf-tools/nota_perf run aurora-supersaw-2x 5
./build/perf-tools/nota_perf run instrument-05 5
./build/perf-tools/nota_perf run instrument-02 5
./build/perf-tools/nota_perf run instrument-16 5
./build/perf-tools/nota_perf run instrument-06 5
./build/perf-tools/nota_perf run mix-16 5

# Idle CPU check:
./build/perf-tools/nota_idle_cpu 5 3
```

---

## 2. Verify Sound Preservation (Aurora Null Tests)

```sh
# 1. Before DSP edits, dump reference renders if comparing across builds:
dotnet run --project tests/Nota.SmokeTest -c Release -- --aurora-null-dump /private/var/folders/p4/xjm5c5k57zs9smkm2r756pw80000gn/T/opencode/aurora-ref

# 2. After DSP edits, verify determinism and < -120 dB null match:
dotnet run --project tests/Nota.SmokeTest -c Release -- --aurora-null
dotnet run --project tests/Nota.SmokeTest -c Release -- --aurora-null-compare /private/var/folders/p4/xjm5c5k57zs9smkm2r756pw80000gn/T/opencode/aurora-ref
```

---

## 3. Full Build & Smoke Test Gate

```sh
./scripts/build.sh
```

Expected outcome:
- Native `nota_engine` and `nota-scanworker` compile with zero warnings.
- Managed `.NET 10` solution builds cleanly.
- `Nota.SmokeTest` (including `DspSilenceBypassTests`, `DspOptimizationTests`, `RenderPoolTests`, `PanLawTests`, `StereoMeasureTests`, and `AuroraNullTests`) passes 100% green.
