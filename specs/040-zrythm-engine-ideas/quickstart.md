# Quickstart: 040-zrythm-engine-ideas

## Baseline (before any change)

```bash
cmake -S tests/perf -B /tmp/nota-perf-tools \
  -DNOTA_ENGINE_DIR=$PWD/src/native/nota.engine \
  -DNOTA_ENGINE_BUILD_DIR=$PWD/src/native/nota.engine/build
cmake --build /tmp/nota-perf-tools
for s in silent-32 sparse-32 mix-16 mix-32 aurora-supersaw-2x instrument-05 instrument-02; do
  for i in 1 2 3; do /tmp/nota-perf-tools/nota_perf run "$s" 2; done
done
```

## Per-story verification

- **US1**: solo-1-of-32 + `sparse-32` improve; null vs unpruned `< -120 dB`;
  `RenderPoolTests` + full smoke green.
- **US2**: `mix-16/32` improve; `--aurora-null-compare` + meter tests green.
- **US3**: meter tolerance tests green; `mix-16` delta recorded.
- **US4**: `aurora-supersaw-2x` ≥ 1.2x to adopt, else revert + record.
- **US5/US6**: `mix-32` / `effect-NN`, MIDI-burst test; RTSan clean.

## Gate

```bash
./scripts/build.sh   # native + managed + smoke 100% green
```

Perf-gate: ≤ 1% instr, ≤ 15% wall per scenario; idle CPU cap holds.
