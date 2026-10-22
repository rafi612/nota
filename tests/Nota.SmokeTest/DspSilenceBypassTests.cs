// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// Per-device auto-sleep is black-box transparent: after a long silent soak (long enough
// for any idle sleep to engage — the test asserts transparency either way, never an
// internal state) the first sounding block is finite and click-free, and the output
// converges to a never-slept reference aligned at note-on. Tails ring out and end on
// their own, frozen loops never sleep, and a bypassed device passes dry unchanged.

using Nota.Application;
using Nota.Infrastructure;

namespace Nota.SmokeTest;

internal static class DspSilenceBypassTests
{
    private const int Block = 512;
    private const int Utility = 4, Reverb = 2, Delay = 3;
    private const int Freeze = 11;

    private static float[] Render(NotaEngine e, int blocks)
    {
        var all = new float[blocks * Block * 2];
        var b = new float[Block * 2];
        for (int i = 0; i < blocks; i++) { e.RenderOffline(b, Block); Array.Copy(b, 0, all, i * Block * 2, Block * 2); }
        return all;
    }
    private static double Rms(float[] st)
    {
        double s = 0;
        foreach (var v in st) s += v * (double)v;
        return Math.Sqrt(s / Math.Max(1, st.Length));
    }
    private static double RmsRange(float[] st, int fromBlock, int toBlock)
    {
        double s = 0; int n = 0;
        for (int i = fromBlock * Block * 2; i < toBlock * Block * 2 && i < st.Length; i++) { s += st[i] * (double)st[i]; n++; }
        return Math.Sqrt(s / Math.Max(1, n));
    }
    private static double PeakRange(float[] st, int fromBlock, int toBlock)
    {
        double p = 0;
        for (int i = fromBlock * Block * 2; i < toBlock * Block * 2 && i < st.Length; i++) p = Math.Max(p, Math.Abs(st[i]));
        return p;
    }
    private static double MaxDiff(float[] a, float[] b)
    {
        double d = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++) d = Math.Max(d, Math.Abs(a[i] - b[i]));
        return d;
    }
    private static bool Finite(float[] st)
    {
        foreach (var v in st) if (!float.IsFinite(v) || Math.Abs(v) > 8f) return false;
        return true;
    }
    private static double MaxStep(float[] st, int blocks)
    {
        double m = 0;
        int n = Math.Min(st.Length, blocks * Block * 2);
        for (int i = 2; i < n; i++) m = Math.Max(m, Math.Abs(st[i] - st[i - 2]));
        return m;
    }

    private static void AddDevice(NotaEngine e, int t, int kind)
    {
        int dev = e.AddBuiltinDevice(t, kind);
        if (kind == Delay && dev >= 0)
        {
            // Free ms, linked 100 ms both channels, moderate feedback; dry kept.
            e.DeviceSetParam(t, dev, 0, 0f);   // Sync off
            e.DeviceSetParam(t, dev, 1, 0.05f);
            e.DeviceSetParam(t, dev, 2, 0.05f);
            e.DeviceSetParam(t, dev, 5, 1f);   // Link L/R
            e.DeviceSetParam(t, dev, 6, 0.5f); // Feedback
            // WowDepth off for the convergence assertion below: the WOW LFO
            // free-runs, so two runs that reach the note via different silent
            // histories can never share its phase (modulated reads would differ
            // forever). Sleep/wake integrity is what we assert — finite,
            // click-free, converging output on a deterministic loop.
            e.DeviceSetParam(t, dev, 10, 0f);  // WowDepth off
        }
    }

    // Post-note audio: `silentBlocks` of silence first (sleep soaks in), then note-on.
    private static float[] SleptTake(int kind, int silentBlocks, int soundBlocks)
    {
        using var e = new NotaEngine();
        int t = e.AddInstrumentTrack();
        AddDevice(e, t, kind);
        Render(e, silentBlocks);
        e.TrackNoteOn(t, 60, 0.8f);
        var sound = Render(e, soundBlocks);
        e.TrackNoteOff(t, 60);
        return sound;
    }
    private static float[] FreshTake(int kind, int soundBlocks)
    {
        using var e = new NotaEngine();
        int t = e.AddInstrumentTrack();
        AddDevice(e, t, kind);
        e.TrackNoteOn(t, 60, 0.8f);
        var sound = Render(e, soundBlocks);
        e.TrackNoteOff(t, 60);
        return sound;
    }

    // ---- instrument sleep/wake transparency (specs/037 US1) --------------------------
    // Built-in synth kinds under test here.
    private const int Aurora = 5, Volt = 6, PentadKind = 14, Rack = 3;

    private static void SetInstParams(NotaEngine e, int t, (string Id, float V)[] ps)
    {
        foreach (var (id, v) in ps)
        {
            int n = e.PluginParamCount(t, -1);
            for (int i = 0; i < n; i++)
                if (e.PluginParamId(t, -1, i) == id) { e.PluginParamSet(t, -1, i, v); break; }
        }
    }

    // Deterministic setups: free-running modulation and FX tails off, so a slept
    // run must null exactly against a never-slept reference (phases still advance
    // while asleep, but at zero depth they cannot affect the output).
    private static readonly (string Id, float V)[] AuroraStill = new[]
    {
        ("lfo1depth", 0f), ("lfo2depth", 0f), ("fxchorus", 0f), ("fxreverb", 0f), ("fxdrive", 0f),
    };
    private static readonly (string Id, float V)[] VoltStill = new[]
    {
        ("lfo1depth", 0f), ("lfo2depth", 0f), ("vibamt", 0f),
    };
    private static readonly (string Id, float V)[] PentadStill = new[]
    {
        ("lfoamt", 0f),
    };

    // Post-note audio on a built-in instrument: silence soaks in first (the idle
    // hangover engages — 64 blocks clears the 16384-sample hangover with margin),
    // then note-on.
    private static float[] SleptTakeInst(int kind, (string Id, float V)[] setup, int silentBlocks, int soundBlocks)
    {
        using var e = new NotaEngine();
        int t = e.AddInstrumentTrack();
        e.SetTrackBuiltinInstrument(t, kind);
        SetInstParams(e, t, setup);
        Render(e, silentBlocks);
        e.TrackNoteOn(t, 60, 0.8f);
        var sound = Render(e, soundBlocks);
        e.TrackNoteOff(t, 60);
        return sound;
    }
    private static float[] FreshTakeInst(int kind, (string Id, float V)[] setup, int soundBlocks)
    {
        using var e = new NotaEngine();
        int t = e.AddInstrumentTrack();
        e.SetTrackBuiltinInstrument(t, kind);
        SetInstParams(e, t, setup);
        e.TrackNoteOn(t, 60, 0.8f);
        var sound = Render(e, soundBlocks);
        e.TrackNoteOff(t, 60);
        return sound;
    }

    public static IEnumerable<(bool, string)> Run()
    {
        // ---- sleep/wake transparency: Utility (stateless gain) -------------------------
        {
            var slept = SleptTake(Utility, 40, 16);
            var fresh = FreshTake(Utility, 16);
            double diff = MaxDiff(slept, fresh), rms = Rms(slept), step = MaxStep(slept, 2);
            yield return (Finite(slept) && rms > 0.01,
                $"Utility wakes sounding and finite after 40 silent blocks (rms {rms:F4})");
            yield return (step < 0.5,
                $"Utility wakes without a click (max step in first 2 blocks {step:F4})");
            // Silent input leaves no state behind in a gain stage, so wake must be exact.
            yield return (diff < 1e-6,
                $"Utility output after the silent soak matches the never-slept reference (maxdiff {diff:E1})");
        }

        // ---- sleep/wake transparency: Delay (feedback loop) ----------------------------
        {
            // 48 silent blocks: the loop's flight time (~100 ms) plus the idle
            // hangover, so sleep genuinely engages before the note (with 40 it
            // would not yet have engaged and the test would prove nothing).
            var slept = SleptTake(Delay, 48, 24);
            var fresh = FreshTake(Delay, 24);
            double diff = MaxDiff(slept, fresh), rms = Rms(slept), step = MaxStep(slept, 2);
            yield return (Finite(slept) && rms > 0.01,
                $"Delay wakes sounding and finite after 40 silent blocks (rms {rms:F4})");
            yield return (step < 0.5,
                $"Delay wakes without a click (max step in first 2 blocks {step:F4})");
            // Both runs meet the note with a quiescent loop, so they converge tightly; the
            // tolerance covers denormal flushing after the long silence, while a dropped or
            // wiped loop state would differ by orders of magnitude more.
            yield return (diff < 1e-4,
                $"Delay output after the silent soak converges to the never-slept reference (maxdiff {diff:E1})");
        }

        // ---- sleep/wake transparency: Reverb (time-varying diffusion) ------------------
        {
            var slept = SleptTake(Reverb, 40, 24);
            var fresh = FreshTake(Reverb, 24);
            double rs = Rms(slept), rf = Rms(fresh), step = MaxStep(slept, 2);
            double rel = Math.Abs(rs - rf) / Math.Max(1e-9, rf);
            yield return (Finite(slept) && rs > 0.01,
                $"Reverb wakes sounding and finite after 40 silent blocks (rms {rs:F4})");
            yield return (step < 0.5,
                $"Reverb wakes without a click (max step in first 2 blocks {step:F4})");
            // Envelope comparison only: modulation phase advances during the silent soak, so
            // sample-exact equality is not asserted — but the tail energy must converge.
            yield return (rel < 0.15,
                $"Reverb tail energy after the silent soak matches the never-slept reference (rms {rs:F4} vs {rf:F4})");
        }

        // ---- tail ring-out: repeats decay away on their own -----------------------------
        using (var e = new NotaEngine())
        {
            int t = e.AddInstrumentTrack();
            int dd = e.AddBuiltinDevice(t, Delay);
            e.DeviceSetParam(t, dd, 0, 0f);    // Sync off
            e.DeviceSetParam(t, dd, 1, 0.05f); // ~100 ms
            e.DeviceSetParam(t, dd, 2, 0.05f);
            e.DeviceSetParam(t, dd, 5, 1f);
            e.DeviceSetParam(t, dd, 6, 0.4f);  // Feedback: ~10 repeats to -80 dB
            e.DeviceSetParam(t, dd, 22, 1f);   // Wet only: the tail is all that is left
            e.TrackNoteOn(t, 60, 0.9f);
            Render(e, 12);
            e.TrackNoteOff(t, 60);
            var tail = Render(e, 120);
            double early = RmsRange(tail, 0, 8), late = PeakRange(tail, 112, 120);
            yield return (Finite(tail) && early > 1e-3,
                $"delay tail rings out after note-off instead of being cut (early rms {early:F4})");
            yield return (late < 1e-4,
                $"delay tail decays below 1e-4 within 120 blocks (late peak {late:E1})");
        }

        // ---- frozen loop holds: frozen tails never sleep --------------------------------
        using (var e = new NotaEngine())
        {
            int t = e.AddInstrumentTrack();
            int dd = e.AddBuiltinDevice(t, Delay);
            e.DeviceSetParam(t, dd, 0, 0f);
            e.DeviceSetParam(t, dd, 1, 0.05f);
            e.DeviceSetParam(t, dd, 2, 0.05f);
            e.DeviceSetParam(t, dd, 5, 1f);
            e.DeviceSetParam(t, dd, 6, 0.5f);
            e.TrackNoteOn(t, 60, 0.9f);
            Render(e, 16);
            e.DeviceSetParam(t, dd, Freeze, 1f);
            e.TrackNoteOff(t, 60);
            var held = Render(e, 48); // silence in; any auto-sleep would engage here
            double early = RmsRange(held, 0, 8), late = RmsRange(held, 40, 48);
            yield return (Finite(held) && late > 1e-4 && late > early * 0.3,
                $"frozen delay loop keeps sounding through 48 silent blocks (early {early:F4}, late {late:F4})");
            e.DeviceSetParam(t, dd, Freeze, 0f);
        }

        // ---- bypass transparency: bypassed device passes dry unchanged ------------------
        {
            float[] bypassed;
            using (var e = new NotaEngine())
            {
                int t = e.AddInstrumentTrack();
                int dd = e.AddBuiltinDevice(t, Delay);
                e.DeviceSetParam(t, dd, 0, 0f);
                e.DeviceSetParam(t, dd, 1, 0.05f);
                e.DeviceSetParam(t, dd, 2, 0.05f);
                e.DeviceSetParam(t, dd, 5, 1f);
                e.DeviceSetParam(t, dd, 6, 0.6f);
                e.DeviceSetParam(t, dd, 22, 1f); // wet only: any leak would show
                e.SetDeviceBypassed(t, dd, true);
                e.TrackNoteOn(t, 60, 0.8f);
                bypassed = Render(e, 16);
                e.TrackNoteOff(t, 60);
            }
            float[] dry;
            using (var e = new NotaEngine())
            {
                int t = e.AddInstrumentTrack();
                e.TrackNoteOn(t, 60, 0.8f);
                dry = Render(e, 16);
                e.TrackNoteOff(t, 60);
            }
            // Steady-state comparison: the first two blocks are skipped to allow any
            // bypass ramp, after which the dry path must be bit-transparent.
            int skip = 2 * Block * 2;
            var bTail = bypassed.Skip(skip).ToArray();
            var dTail = dry.Skip(skip).ToArray();
            double diff = MaxDiff(bTail, dTail), rms = Rms(bTail);
            yield return (rms > 0.01,
                $"bypassed delay passes the dry signal instead of muting (rms {rms:F4})");
            yield return (diff < 1e-6,
                $"bypassed delay output equals the no-device render (maxdiff {diff:E1})");
        }

        // ---- instrument sleep/wake transparency: Aurora / Volt / Pentad ----------------
        // 64 silent blocks engage the idle hangover; the deterministic setups above
        // make the wake-up exactly reproducible (free-running phases advance while
        // asleep but sit at zero depth, so they cannot colour the output).
        foreach (var (name, kind, setup) in new[]
            { ("Aurora", Aurora, AuroraStill), ("Volt", Volt, VoltStill), ("Pentad", PentadKind, PentadStill) })
        {
            var slept = SleptTakeInst(kind, setup, 64, 16);
            var fresh = FreshTakeInst(kind, setup, 16);
            double diff = MaxDiff(slept, fresh), rms = Rms(slept), step = MaxStep(slept, 2);
            yield return (Finite(slept) && rms > 0.01,
                $"{name} wakes sounding and finite after 64 silent blocks (rms {rms:F4})");
            yield return (step < 0.5,
                $"{name} wakes without a click (max step in first 2 blocks {step:F4})");
            yield return (diff < 1e-6,
                $"{name} output after the silent soak matches the never-slept reference (maxdiff {diff:E1})");
        }

        // ---- instrument tail settles into silence (sleep engages, no stuck voice) -----
        {
            using var e = new NotaEngine();
            int t = e.AddInstrumentTrack();
            e.SetTrackBuiltinInstrument(t, Aurora);
            SetInstParams(e, t, AuroraStill);
            e.TrackNoteOn(t, 60, 0.8f);
            Render(e, 16);
            e.TrackNoteOff(t, 60);
            var tail = Render(e, 200);
            double late = PeakRange(tail, 192, 200);
            yield return (Finite(tail) && late < 1e-4,
                $"Aurora tail decays below 1e-4 within 200 blocks (late peak {late:E1})");
        }

        // ---- rack with an idle chain renders silence without errors -------------------
        {
            using var e = new NotaEngine();
            int t = e.AddInstrumentTrack();
            e.SetTrackBuiltinInstrument(t, Rack);
            var silence = Render(e, 64);
            yield return (Finite(silence) && PeakRange(silence, 0, 64) < 1e-6,
                "Instrument Rack stays silent and finite through 64 silent blocks");
        }
    }
}
