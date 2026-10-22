# Contracts: Internal DSP & Benchmark Verification

**Feature**: `037-dsp-synth-engine-perf` | **Date**: 2026-10-22

The public C ABI (`include/nota/nota_engine.h`) is **unchanged** (AR-7). All contracts below govern internal engine interfaces and CLI benchmark/smoke-test verification.

---

## 1. `Instrument::isIdle()` & Rack Chain Sleep Contract

1. **Precondition**: `inst.isIdle() == true` iff `activeVoices == 0`, internal sequencers/hold latches are inactive, and internal output peak has remained $\le 1.0 \times 10^{-5}$ ($-100\text{ dBFS}$) for at least `flightSamples + kIdleSamples`.
2. **Postcondition on `inst.render(out, frames)` when `isIdle() == true`**:
   - Does not write to `out[0 .. frames*2 - 1]`.
   - Advances internal free-running phase accumulators in $O(1)$ time.
   - Executes in $< 50\text{ ns}$ per call.
3. **Wake guarantee**:
   - Any call to `noteOn`, `noteOff`, `allNotesOff`, `pluginParamSet`, `controller`, `noteExpression` (when voices are active or `pitch < 0`), `setSampleRate`, or `setState` sets `idle_ = false` and `quietSamples_ = 0` before the next sample is rendered.

---

## 2. Null-Test Sound Preservation Contract

- **Tier-1 changes** (auto-sleep fast-paths, `WavetableSynth` `readMorph` integer cast, `oscStack` mode hoisting, power-of-two chorus mask + trig identity, `PhysicalSynth` 2-wide `double` SIMD, `WavetableSynth`/`VoltSynth` 2-wide `double` paired SVF, `Engine_Render` SIMD metering):
  - Offline render residual against the pre-change engine MUST be **`< -120 dB`** (bit-identical or floating-point association equivalent within 1 ULP).
- **Tier-2 changes** (`Keys` 4-wide `float` SIMD modal sum order, `Convolution`/`Chamber` 4-wide `float` `cmac` FMA):
  - Offline render residual against the pre-change engine MUST be **`< -90 dB`**.

---

## 3. `nota_perf` Benchmark Scenarios Contract

Existing CLI (`tests/perf/nota_perf`) scenarios used to gate this feature:
- `silent-32`: 32 tracks (Volt + EQ-8 + Compressor + Delay), no clips.
- `sparse-32`: 32 tracks, 1 in 4 playing.
- `aurora-supersaw-2x`: 2 tracks of Nota Aurora with factory Super Saw preset, 6-voice chords.
- `instrument-02`: Nota Physical, 1 track polyphonic passage.
- `instrument-05`: Nota Aurora, 1 track polyphonic passage.
- `instrument-06`: Nota Volt, 1 track polyphonic passage.
- `instrument-14`: Nota Pentad, 1 track polyphonic passage.
- `instrument-15`: Nota Consort, 1 track polyphonic passage.
- `instrument-16`: Nota Keys, 1 track polyphonic passage.
- `mix-8`, `mix-16`, `mix-32`: multi-track stress mixes.
