# MicMate DSP chain — correctness & audio-quality audit

> **归档说明**：这是一次独立 DSP 审计的原始报告（当时项目**没有版本控制**，
> 报告留在工作区根目录 `_dsp_audit_probe\` 下）。它记录并实测了 15 项问题，
> 其中第 1 项（`DenoiseEffect.Read` 对非 480 倍数的块长抛异常）触发了随后一次
> 未完成的改写、并使主工程编译不过——本轮修复的起点正是这份报告。
>
> 修复现状见 README §13。**注意**：报告第 7 项中"RMS 窗口读了增益前样本"的判断有误，
> 见 README §13 与 `Dsp/LoudnessBalanceEffect.cs` 里的说明（改成量增益后样本反而会让
> 环路增益变成 2、稳态只加到一半，实测 −26.8 dBFS 而非目标 −20 dBFS）。

Scope: `MicMate/Dsp/*` effect chain (48 kHz / mono / float32, 480-sample frames).
Method: every finding below was reproduced by compiling the **unmodified** `MicMate/Dsp`
sources against the same NAudio 3.1 assemblies the app uses, driving them with synthetic
signals, and measuring level / DC / sample-step / latency. `dotnet build MicMate.csproj
-t:Rebuild` passes with **0 warnings, 0 errors**, so nothing here is a compile-time issue.

Harness: `D:\DSH\_dsp_audit_probe\` (Probe.cs … Probe5.cs). No app file was modified.

---

## Severity summary

| # | Severity | File | One-line |
|---|----------|------|----------|
| 1 | **critical** | `DenoiseEffect.cs:83-95` | `Read` throws `ArgumentOutOfRangeException` for any block length that is not a multiple of 480 → effect dropped / audio-thread exception |
| 2 | **critical** | `LoudnessBalanceEffect.cs:87-110` | peak protection does not work: +32.5 dBFS output, 23 590 samples above full scale on a step from −40 to 0 dBFS |
| 3 | **major** | `PitchDetector.cs:94-102` | no pitch below ~130 Hz; octave error at 1 kHz (1000 Hz → 500.4 Hz); up to +42 cents systematic sharp bias |
| 4 | **major** | `HardTuneEffect.cs:148-152` + `PitchShifter.cs:23-24` | ~40 ms silence / 10 ms-repeat dropout when the shifter engages, then level jump; ~28–40 ms uncompensated latency |
| 5 | **major** | `PitchShifter.cs:23-44` | wet amplitude response is not flat: 0.72× … 1.32× across 40 Hz–8 kHz; DC amplified 2.34× (7.4 dB) |
| 6 | **major** | `ToneStyleEffect.cs:37-55` | preset switch is **not** click-free: measured sample step 0.62 on a 0.3-amplitude tone |
| 7 | **major** | `LoudnessBalanceEffect.cs:74-88` | +11…+23 dB gain on silence → mic noise boosted after every pause; RMS window reads post-gain samples (pumping) |
| 8 | **minor-major** | whole chain | no DC blocking anywhere; `HardTuneEffect` passes 0.5 DC as 0.563 (1.13×) |
| 9 | **minor** | `IAudioEffect.cs:72-85`, `OutputStage.cs` | `DynamicChain.Reset` resets only `NAudio.Effects.AudioEffect` → gate/EQ/loudness filters, RMS window, denoise envelope never cleared |
| 10 | **minor** | `IAudioEffect.cs:55-66` | on any effect exception the chain re-emits the pre-effect block for the whole block length and downstream modules process it |
| 11 | **minor** | `RingModulatorEffect.cs:39-46` | carrier `(1−depth)+depth·sin` is unipolar → AM + DC, not ring modulation; input-tracked carrier FM adds wobble |
| 12 | **minor** | `DenoiseEffect.cs:46-53` | `SetModel` disposes the old model while the audio thread may be inside it; also resets `_pending` in the middle of a frame |
| 13 | **minor** | `MegaphoneDistortionEffect.cs:75,152,162` | `new Random(20260922)` inside the effect: identical noise sequence on every run |
| 14 | **minor** | `PitchDetector.cs:119-131` | `LastClarity` is never assigned on the success path (always 0 when a pitch *is* found) |
| 15 | **info** | `HardTuneEffect.cs:20-28` | class doc says pitch shift is disabled by default; `EnablePitchShift` is `true` (doc/code mismatch only) |

---

## 1. CRITICAL — `DenoiseEffect.Read` throws for any block length that is not a multiple of 480

**File/lines:** `Dsp/DenoiseEffect.cs:75-98` (fault at `:93`).

```csharp
83  var offset = 0;
84  while (offset < buffer.Length)
85  {
86      var copy = Math.Min(FrameSize - _pending, buffer.Length - offset);
87      buffer.Slice(offset, copy).CopyTo(_frame.AsSpan(_pending));
88      _pending += copy;
89      offset += copy;
90
91      if (_pending < FrameSize) break;
92
93      ProcessFrame(buffer.Slice(offset - FrameSize, FrameSize));
94      _pending = 0;
95  }
```

**What the code does.** The only `break` sits inside `if (_pending < FrameSize)`. When a
frame happens to complete *exactly* at the end of the caller's buffer, `offset` advances to
`buffer.Length` with `_pending == 0` and the loop exits by the `while` condition — fine.
But when a *previous* block left `_pending = R > 0` and the next block is large enough to
finish a frame and start another, the second `Slice(offset - FrameSize, …)` computes a
negative start index.

Worked example (720-sample blocks, `R = 240` carried in): pass 1 copies 240 → `_pending = 480`
→ `ProcessFrame(Slice(0))`, `_pending = 0`, `offset = 240`; pass 2 copies 480 → `_pending = 480`
→ `ProcessFrame(Slice(240 − 480)) = Slice(−240)` → throw.

**Reproduced** (real class, real NAudio):

```
block   480: OK          block   720: ArgumentOutOfRangeException
block   960: OK          block   360: ArgumentOutOfRangeException
block   240: ArgumentOutOfRangeException
block   160: ArgumentOutOfRangeException
block   512: ArgumentOutOfRangeException
block   100: ArgumentOutOfRangeException
block   144: ArgumentOutOfRangeException
block  4096: ArgumentOutOfRangeException
block  8192: ArgumentOutOfRangeException
single-sample blocks x1000: ArgumentOutOfRangeException
variable sizes in one stream (480,240,480,240…): ArgumentOutOfRangeException at sample 720
```

**Audible failure.** The effect block length is whatever the audio engine asks for, and that
is the WASAPI period, not 480: `WasapiPlayer.Read(2·N)` → `MonoToStereoProvider` (scratch 4096)
→ `SoftLimiterEffect` → `OutputBus` → `MicMixer` → `DynamicChain`, and every one of those
passes the *requested* length straight through to `effect.Read`. `DynamicChain` guards each
effect with `try/catch` (`IAudioEffect.cs:55-66`), so on a 144/240/360/512/720-sample period
you get, per audio block:

* `Log.Error("效果模块 AI 降噪 处理失败，已跳过")` — a log write on the audio thread, every block
  (reproduced: 7 consecutive errors), and
* **no AI denoise at all** — the user's configured noise suppression silently never runs,
  so background noise/hiss is passed to the call/stream at full level.

If the same path is ever driven from `MicMixer.Read` without the chain's guard, the exception
escapes `Read` into the NAudio playback loop and **kills audio** (that is exactly the failure
mode `AudioEngine.cs:213-219` documents for the limiter). Note the app already ships a
"low-latency" mode whose period is *not* 480 (e.g. 144 samples = 3 ms), and the app's own
`AudioDiagnostics`/`TuneProbe` harnesses only ever use 480 — which is why this was never seen.

**Fix.** Flush full frames and only `break` when nothing more can be consumed, e.g.

```csharp
var offset = 0;
while (offset < buffer.Length)
{
    var copy = Math.Min(FrameSize - _pending, buffer.Length - offset);
    buffer.Slice(offset, copy).CopyTo(_frame.AsSpan(_pending));
    _pending += copy; offset += copy;
    if (_pending == FrameSize)
    {
        ProcessFrame(buffer.Slice(offset - FrameSize, FrameSize));
        _pending = 0;
    }
}
```
(and reset `_pending` in `SetModel`/add a `Reset()`).

---

## 2. CRITICAL — `LoudnessBalanceEffect` peak protection does not protect anything

**File/lines:** `Dsp/LoudnessBalanceEffect.cs:87-110`.

```csharp
 87  var desiredDb = Math.Clamp(target - CurrentRmsDb, MinGainDb, MaxGainDb);
 88  _gain.SetTarget(AudioMath.DbToLinear(desiredDb));
...
106  var peakDb = AudioMath.LinearToDb(MathF.Max(_peak, MathF.Abs(sample)));
107  if (peakDb > PeakCeilingDb)
108      _gain.SetTarget(MathF.Min(_gain.Current, _gain.Current * AudioMath.DbToLinear(PeakCeilingDb - peakDb)));
109
110  var g = _gain.Next();
111  CurrentGainDb = AudioMath.LinearToDb(g);
112  buffer[i] = sample * g;
```

**What the code does.** Lines 107-108 only *move the target*; the applied gain is still the
exponentially smoothed `_gain.Next()` at line 110. The smoother's time constant comes from the
user-facing Speed control: `5000·0.1^((speed-1)/9)` ms → **5000 ms at Speed 1 and 502 ms at
Speed 10** (`:57-60`). So a 26 dB over-target is applied over hundreds of ms while the
protection is "asking" for a smaller target. `_peak` is additionally a *trailing* 10 ms window
(`:91-104`) that does not yet contain the loud sample, and the whole structure is feed-forward
with no look-ahead.

**Reproduced** (`TargetLufs = -20`, `Speed = 5`, 440 Hz sine stepping from −40 dBFS to 0 dBFS
at 0.5 s; the class documents "peak above −1 dBFS immediately reduces gain"):

| step to | max output | in dBFS | samples > 1.0 | samples > −1 dBFS | peak gain reached |
|---|---|---|---|---|---|
| 10.0 | 41.98 | **+32.5** | 23 590 | 23 627 | +10.3 dB |
| 3.0 | 12.60 | +22.0 | 22 658 | 22 806 | +10.8 dB |
| 1.4 | 5.88 | +15.4 | 21 250 | 21 549 | +11.6 dB |
| 1.0 | 4.20 | +12.5 | 20 258 | 20 675 | +12.2 dB |

**Audible failure.** With any realistic level jump (loud laugh, plosive, someone leaning into
the mic, or simply unmuting) the balancer overshoots by 12–26 dB for ~0.4 s. The output
limiter does eventually clamp it — measured peak entering the limiter **4.2004 (+12.5 dBFS)**,
after the limiter **0.8907 (−1.0 dBFS)** with **13.2 dB** of gain reduction — so the user does
not get digital wrap, but they get ~0.4 s of hard-squashed, pumping, distorted audio and a
limiter that then has to release over 60 ms: a loud "ducking/splat" on every transient. The
class comment ("峰值超过 −1 dBFS 时立即降低增益防削波") is not true of the code.

**Fix.** Apply protection instantly rather than through the slow smoother — keep a separate
fast limiter stage inside the effect (or bypass the smoother for reductions:
`if (target < current) current = target;`), take the peak from a *look-ahead* buffer, and size
the smoother for peaks (<10 ms) independently of the loudness Speed.

---

## 3. MAJOR — `PitchDetector`: dead below ~130 Hz, octave errors above ~900 Hz, sharp bias

**File/lines:** `Dsp/PitchDetector.cs:92-131` (lag picker `:92-102`, sub-sample refine `:107-117`).

```csharp
 92  var threshold = bestCorrelation * 0.95f;
 94  for (var lag = _maxLag; lag >= _minLag + 1; lag--)   // longest lag that clears the threshold wins
```

Because the windowed autocorrelation envelope decays monotonically with lag (fewer overlapping
samples), the *longest* lag that still clears `0.95·peak` is systematically a few samples
**shorter** than the true period; the loop also keeps overwriting `chosen`, so it ends on the
largest qualifying lag. Measured on pure 480-sample-fed frames (`windowSize: 1024`):

| input | detected | error |
|---|---|---|
| 70 Hz | **none** | detection failure |
| 82 Hz | **none** | detection failure |
| 110 Hz | **none** | detection failure |
| 150 Hz | 153.6 Hz | +41.6 cents |
| 196 Hz | 199.3 Hz | +28.5 cents |
| 220 Hz | 222.9 Hz | +22.7 cents |
| **228 Hz** | **230.9 Hz** | **+22.1 cents** |
| 261.6 Hz | 264.1 Hz | +16.2 cents |
| 440 Hz | 441.6 Hz | +6.5 cents |
| 1000 Hz | **500.4 Hz** | **−1198.8 cents (octave error)** |

**Audible failure.**
* **Male/deep voices and low notes never tune.** `wanted` is 0 whenever `detected == 0`
  (`HardTuneEffect.cs:139`), so the effect silently degrades to "EQ + comb" for anything below
  roughly F3. A singer on a low note hears no tuning at all while higher notes snap — an
  inconsistent, "sometimes it works" effect.
* **High notes get snapped to the wrong note.** At ~1 kHz the detector reports half the
  frequency, so the effect computes a target one octave too low and the shifter drives the
  voice *down* an octave — a very audible wrong-note artefact.
* The +16…+42 cent bias means the hard-tune "snap" is not to the intended scale degree: the
  measured input pitch is already sharp, so the snapped result lands off-pitch even when it
  picks the right note.

**Fix.** Pick the *shortest* lag that is a true local maximum at a normalised-correlation
threshold (or take the peak of the *unwindowed* normalised autocorrelation / use a
centre-clipping or YIN-style difference function), and raise `maxLag` — with
`windowSize: 1024`, `_maxLag = min(512, sr/minFrequency)` is capped at 512, so 70 Hz
(686 samples) is arithmetically unreachable (`:25`, `:44`).

---

## 4. MAJOR — hard-tune pitch shifter: onset dropout, level jump, 28–40 ms uncompensated latency

**File/lines:** `Dsp/HardTuneEffect.cs:148-152`, `Dsp/PitchShifter.cs:23-24,35-45`.

```csharp
148  if (EnablePitchShift && _shifter != null && Math.Abs(_appliedSemitones) > 0.01)
151      _shifter.Process(buffer);
```
```csharp
23  private const int FftFrameSize = 2048;
24  private const int Oversampling = 8;
44  _shifter.PitchShift(factor, buffer.Length, FftFrameSize, Oversampling, _sampleRate, buffer);
```

**Reproduced.** Feeding a steady 228 Hz voice-like tone (0.28 + 0.10·2nd harmonic, constant
input frame RMS ≈ 0.2095) through `CreativeEffect(Robot, Amount = 88)`:

```
per-frame RMS (frames 0-12, 10 ms each):
  0.2456  0.0088  0.0002  0.0018  0.0067  0.2283  0.3223  0.3095  0.3233  0.3038 …
samples below 0.01 in the first 100 ms: 1954 / 4800
```
Enabling robot mid-stream on a steady 300 Hz tone: `0.3200 0.0017 0.0003 0.0017 0.0077 0.3270
0.4049 …`.

So when the shifter kicks in, **frames 1–4 (≈40 ms) are essentially silent**, after which the
level jumps from 0.2095 to ~0.31 (≈+3.4 dB) and overshoots. Cause: `SmbPitchShifter` is
zero-padded, so its output accumulator must fill a 2048-sample window before it produces
full-amplitude audio.

Onset delay measured two ways: first sample >0.05 = **28.3 ms** after the input starts;
AM-envelope correlation = **38.5 ms** (upper bound, the envelope is time-scaled by the pitch
factor). First 480 output samples are exactly silent (RMS 0.0000).

**Audible failure.** Every time the shifter engages — switching to 电音, the retune amount
crossing ±0.01 semitones (`HardTuneEffect.cs:148`), or the smoothed `_appliedSemitones`
crossing that threshold as it ramps — the user hears a ~40 ms hole followed by a click/level
bump. With `RetuneSpeed` low (gentle tuning) `_appliedSemitones` crosses the threshold
repeatedly, so the dropout can repeat. Latency of ~28–40 ms is uncompensated anywhere in the
chain, so monitoring a live voice is audibly out of sync (and `LatencySamples` is never
consulted — NAudio exposes `PitchShiftEffect.LatencySamples`; the app bypasses that type).

**Fix.** Use NAudio's own `NAudio.Effects.PitchShiftEffect` (it reports `LatencySamples` and
handles normalization), lower `FftFrameSize` (e.g. 1024 or 512) to cut latency, prime the
shifter with silence after `Reset()`, and crossfade dry→wet over ~50 ms instead of hard
switching on a 0.01 threshold.

---

## 5. MAJOR — pitch-shifter wet gain is frequency dependent; DC is amplified 2.34×

**File/lines:** `Dsp/PitchShifter.cs:35-45` (input gain is never normalised).

**Reproduced** — steady 0.3-amplitude sine (input RMS 0.2121), `Semitones = 4` (factor 1.26),
measured on the settled second half of 1 s:

```
in    40 Hz -> gain 1.251
in   100 Hz -> gain 1.306
in   200 Hz -> gain 1.316
in   440 Hz -> gain 0.724
in  1000 Hz -> gain 0.928
in  2000 Hz -> gain 1.312
in  4000 Hz -> gain 1.280
in   8000 Hz -> gain 1.083
DC     0.3  -> mean gain 2.3424   (0.5 DC -> 1.1712)
```

A further direct check isolates the shifter: `0.5 DC → PitchShifter only = mean 1.17118
(gain 2.34)`, while `0.5 DC → timbre EQ only = 0.50000 (gain 1.000)`.

**Audible failure.** A ~5.2 dB spread between 0.72× (around A4) and 1.32× (100–200 Hz,
2–4 kHz) means the hard-tune effect colours and level-shifts the voice depending on which note
is sung — a comb-like tonal wobble that changes as the melody moves. Boosting everything below
~250 Hz by 2.4 dB while cutting 440 Hz by 2.8 dB is clearly audible as "boxy then thin"
depending on the note. The 2.34× DC gain means any DC offset from the mic/preamp
(see finding 8) is amplified and then sits in the limiter, eating headroom.

**Fix.** Normalise the wet path (measure/apply a gain trim, or use
`NAudio.Effects.PitchShiftEffect`), and put a DC blocker before/after the shifter.

---

## 6. MAJOR — `ToneStyleEffect` preset switching is not click-free

**File/lines:** `Dsp/ToneStyleEffect.cs:37-55`, comment at `:8` claims "切换预设时点击无爆音".

```csharp
43  for (var i = 0; i < _bands.Length && i < preset.Length; i++)
44  { _bands[i].Type = preset[i].Type; … _bands[i].GainDb = preset[i].GainDb; … }
52  _equalizer.Update();
```

**Reproduced.** 6 kHz tone at 0.3 amplitude, preset switched `Sharp` → `Bright` at 0.5 s
(the two presets differ by ~+9.2 vs +5.2 dB at 6 kHz and by ~3 dB at 3 kHz):

```
maxStep near the switch = 0.6224      (interior maxStep of the settled signal = 0.0000)
```

0.62 peak-to-peak across one sample on a 0.3-amplitude tone is a full-scale **click**.

The EQ class does implement a crossfade (`CrossfadingBiQuadFilter`), but the three band
objects in `_bands` are mutated in place in a loop and only then published with one
`Update()`; between the first and last band write the filter set is internally inconsistent,
and the abrupt gain/phase change at a 6 kHz peak is not ramped by enough to hide it.

**Audible failure.** A sharp "tick"/"pop" every time the user changes 音色风格 while talking
or while music is playing. Also worth noting: `Read` returns early for `Natural`
(`:62`), so switching *to* Natural drops the EQ instantly with no crossfade at all.

**Fix.** Build the new band set, assign atomically, and let the EQ crossfade over a fixed
10–20 ms; or ramp the effect's output gain over the switch. Don't mutate the live band
objects one at a time.

---

## 7. MAJOR — loudness balancer boosts silence by up to +23 dB; RMS window reads post-gain audio

**File/lines:** `Dsp/LoudnessBalanceEffect.cs:74-88`.

**Reproduced:**
```
after 3 s of digital silence: gain = +22.3 dB, rms = -100.0 dBFS
then -62 dBFS noise: output peak 0.0114, gain still +23.1 dB, output rising 0.0074 → 0.0081
steady -26 dBFS tone over 4 s: gain range 5.1 … 8.4 dB (3.3 dB of slow pumping)
```

**Audible failure.** After any pause in speech the balancer has wound itself up to +22 dB of
gain; when the user starts talking again (or when there is any mic self-noise, fan noise,
keyboard click), that noise is amplified by more than 20 dB above its real level — the classic
"noise rush / breathing" artefact, and the first syllable after every pause is over-loud until
the loop settles. The 3.3 dB hunt on a steady tone is audible as slow pumping.

Secondary defect at `:75-84`: the RMS window is filled with **pre-gain** input (`sample`) while
the output is `sample * g`, so the loop measures a signal that does not include its own gain —
the correction is therefore never the one the output needs, which is part of why the loop
overshoots/undershoots (target −20 dBFS ended at −22.4 dBFS with +8.4 dB gain).

**Fix.** Add a noise floor / gate-hold so the gain freezes (or decays to unity) below a
minimum input level; feed the RMS detector from a *post-gain* or explicit target-domain signal;
consider using NAudio's `AutomaticGainControlEffect` (has `UseVoiceDetection`) instead.

---

## 8. MINOR-MAJOR — no DC blocking anywhere in the chain

**Files/lines:** `Dsp/IAudioEffect.cs` chain order (`AudioEngine.cs:504-507`), `Dsp/HardTuneEffect.cs:164-166`.

**Reproduced:** `CreativeEffect(Robot)` on constant 0.5 input → output mean **0.56302**
(1.126× DC gain); 0.5 DC through the shifter alone → **1.17118**.

Nothing in NoiseGate → Denoise → Loudness → Tone → Creative → Gain is a high-pass;
`SpectralDenoiseModel` has a 120 Hz Butterworth HPF (`:92-98`) but only inside the denoise
module, and the denoise module can be switched off or fail (finding 1). NAudio ships
`DcBlockerEffect` and it is unused. The `MegaphoneDistortionEffect` also quantises whatever DC
reaches it into steps (`:138`), and `RingModulatorEffect` adds its own DC term (finding 11).

**Audible failure.** Any DC offset from the interface/preamp (common on USB mics and on
cheap motherboard inputs) is amplified by the hard-tune/limiter path, permanently eats
headroom, produces a thump when effects are switched, and makes the quantiser in the
megaphone produce a static offset instead of hiss.

**Fix.** Add one `NAudio.Effects.DcBlockerEffect` (or a first-order HPF at ~20 Hz) early in
the chain, before Loudness.

---

## 9. MINOR — `Reset()` never reaches most effects, so switching/restarting carries stale state

**File/lines:** `Dsp/IAudioEffect.cs:72-85`.

```csharp
74  foreach (var effect in _effects)
76      switch (effect)
78          case NAudio.Effects.AudioEffect native: native.Reset(); break;
81          case LoudnessBalanceEffect _: break;
```

`NoiseGateEffect`, `ToneStyleEffect`, `DenoiseEffect` and `GainEffect` wrap
`NAudio.Effects.*` **by composition**, not inheritance, so they do not match
`case NAudio.Effects.AudioEffect` and are silently skipped. Their inner filter state, gate
hold/ramps, denoise envelope and RMS window therefore persist. `Chain.Reset()` is only called
from `AudioEngine.Dispose()` (`AudioEngine.cs:619`); `Stop()`/`Start()` and
`Reconfigure()` (`:594-604`) do not reset. `HardTuneEffect.Reset`/`SpectralDenoiseModel.Reset`
exist but are never called from the app.

**Audible failure.** Stopping and restarting the engine (or switching input devices) can start
the new stream with the old gate open, the old denoise envelope, and a loudness gain still at
+20 dB — so the first block after restart can be gated open on silence or pumped loud.

**Fix.** Define `Reset()` on `IAudioEffect` (or a marker interface) and implement it in each
wrapper by delegating to its inner NAudio effect; call `Chain.Reset()` from `StopCore()`.

---

## 10. MINOR — an effect exception makes the chain emit pre-effect audio for the whole block

**File/lines:** `Dsp/IAudioEffect.cs:52-68`.

`_scratch` is filled with the raw source (`:53`) and only overwritten by the effect loop; if an
effect throws, `_scratch` still holds the *unprocessed input*, which is copied back to the
output at `:68`. Effects already applied earlier in the loop are discarded, and every effect
after the failing one processes dry audio instead.

**Reproduced** (throwing effect in a `DynamicChain`): 5 consecutive `Log.Error` lines, output
equal to the input. This compounds finding 1: when denoise throws, the user not only loses
denoise but the tone/EQ/creative/gain stages all run on the wrong signal for that block.

**Fix.** Keep a separate "processed so far" buffer and only copy back what has actually been
through the chain; log once per second rather than per block.

---

## 11. MINOR — `RingModulatorEffect` is amplitude modulation with a DC term, plus a wobbling carrier

**File/lines:** `Dsp/RingModulatorEffect.cs:38-46`.

```csharp
38  var frequency = Frequency;
39  if (TrackInput) frequency *= 1f + Math.Clamp(MathF.Abs(input) * 4f, 0f, 0.35f);
44  var carrier = MathF.Sin((float)(_phase * 2.0 * Math.PI));
45  var wet = input * ((1f - depth) + depth * carrier);
```

The carrier term `(1−depth) + depth·sin` with `Depth = 0.85` spans **0.15 … 1.00** — unipolar,
so the operation is AM with a DC component, not ring modulation (which needs a bipolar
`depth·sin` carrier). Measured: constant 0.5 input → output mean **0.159** (DC passes;
`0.5 × 0.32 = 0.16` as predicted), and the maximum |out|/|in| is 1.00× so it cannot by itself
clip. The input-tracking FM (`:39`) modulates the carrier frequency by up to 35 % with the
audio envelope, which is envelope-driven instantaneous frequency modulation — pitch wobble
that will sound like unstable, "seasick" detuning on sustained notes rather than the intended
mechanical tone.

**Fix.** Use `wet = input * depth * carrier + input * (1 - depth)` only if AM is really wanted,
or make the carrier bipolar for true ring mod; low-pass the tracking envelope and reduce the
range (≤5 %) to remove the wobble.

---

## 12. MINOR — `DenoiseEffect.SetModel` disposes the model the audio thread may be using

**File/lines:** `Dsp/DenoiseEffect.cs:46-53`.

```csharp
49  _model = model;
50  _pending = 0;
51  if (!ReferenceEquals(old, model)) old.Dispose();
```

`SetModel` is called from the UI/config thread while `Read` can be inside `_model.Process`
(`:116`). Disposing an ONNX `InferenceSession` under a running inference is a use-after-dispose
race (`OnnxWaveformDenoiseModel.Dispose` → `_session.Dispose()`). Clearing `_pending`
mid-frame also mixes the tail of the old model's frame with the new model's output for one
10 ms frame.

**Fix.** Defer disposal (queue the old model and dispose it after the next audio block), and
flush the partial frame (process or discard it) rather than mixing.

---

## 13–15. MINOR — smaller items

* **`MegaphoneDistortionEffect.cs:75,152,162`** — `private readonly Random _random = new(20260922)`
  is per-instance and deterministic. Every run of the app produces bit-identical "background
  hiss" and "rocket roar". Perceptually it is a fixed, recognisable noise pattern; it also
  prevents any A/B testing of the effect from being meaningful. Use a shared static random or
  a session-seeded one.
* **`PitchDetector.cs:119-131`** — `LastClarity` is set only on the failure paths (`:62`, `:80`);
  when a pitch *is* returned it keeps its previous value (measured `LastClarity = 0.000` for a
  perfectly detected 440 Hz). Any future confidence gating or the diagnostic UI would see
  "0 clarity" for good detections. Assign `LastClarity = bestCorrelation` before returning.
* **`HardTuneEffect.cs:20-28,68`** — the class doc says the shifter is disabled by default
  ("默认不启用变调"), but `EnablePitchShift` defaults to `true` and `CreativeEffect` never
  turns it off. Documentation-only mismatch, but it is the reason the shifter's defects
  (findings 4–5) reach users.
* **`HardTuneEffect.cs:103`** — `if (_dryBuffer.Length < length) return;` silently bypasses the
  whole effect (including the timbre EQ) for any block longer than `frameSize * 8 = 3840`
  samples. Same class of silent-bypass as finding 1; worth making the buffer grow instead.
* **`IAudioEffect.cs:38` / `LoudnessBalanceEffect.cs`** — no `Reset()`/`Dispose()` on the
  wrapper effects, and `LoudnessBalanceEffect` never snaps `_gain` back to unity when
  `Speed` changes, so a large accumulated gain survives a parameter change.

---

## What is correct (checked, no defect found)

* **`ToneStyleEffect` EQ maths** — measured gain vs preset matches the documented curves:
  300 Hz `Bright −4.0 dB / Warm +3.3 dB`; 3 kHz `Sharp +6.4 dB / Deep −4.4 dB`;
  6 kHz `Sharp +9.2 dB / Warm −6.5 dB`. All presets use exactly 3 bands, so the
  `i < _bands.Length && i < preset.Length` copy loop never leaves a stale band.
* **`SpectralDenoiseModel`** — the RBJ biquad state is continuous across `Process` calls
  (20 × 480 samples bit-identical to 1 × 9600), `MathF.Max(envelope, 1e-9f)` guards the
  `Log10`, the gain is guarded with `float.IsFinite` (`:115`), and digital silence stays
  exactly 0 with no NaN. Frame-boundary step on a 440 Hz tone (0.0172) equals the input's own
  natural step, i.e. no added discontinuity.
* **`NoiseGateEffect`** — behaves as a 50:1 downward expander with `RangeDb = -80`: a
  −40 dBFS tone is passed at 0 dB gain with a −55 dBFS threshold and attenuated −80 dB with a
  −30 dBFS threshold; noise at −70 dBFS is attenuated −80 dB; the largest sample step over a
  noise→burst→noise run was 0.0063 (≈ a −20 dBFS tone's own natural step), so the 1 ms attack /
  40 ms hold / 150 ms release produce no clicks. Note the naming: this is *not* a hard mute,
  it is a gentle expander, which matches the class doc.
* **`MegaphoneDistortionEffect`** — the `MathF.Log(level + 1e-9)` / `MathF.Pow(overshoot, k)`
  compressor is guarded (`:105`, `:122`), the final mix is clamped (`:181`), silence in gives
  silence out, and +3.5 dBFS input does not produce non-finite values.
* **`LoudnessBalanceEffect`** — no divide-by-zero on silence (`MathF.Max(1, _windowFilled)`,
  `AudioMath.DbToLinear` floors at −100 dB) and single-sample buffers are safe for 1000
  iterations with no non-finite output. Its *internal* gain staging is the problem
  (finding 2), not its arithmetic.
* **`HardTuneEffect.PushAnalysis` / `SnapToMidi`** — the ring-buffer copy-out is correct
  (`_analysisRing[(_analysisWrite + i) % Length]`, no overlapping-array-copy bug), and the
  hysteresis logic does resist flipping on ±cents jitter within 0.12 semitone.
* **Project builds clean**: `dotnet build MicMate.csproj -t:Rebuild` → 0 warnings, 0 errors
  (so the suspicious-looking `EqualizerBand.LowShelf(120f, -3f)` call is valid — confirmed by
  reflection: the 2-arg overload exists and sets `ShelfSlope = 1`).

---

## Optimization suggestions (clearly separate from defects — not bugs)

1. **Replace the hand-rolled `PitchShifter` with `NAudio.Effects.PitchShiftEffect`.** It uses
   the same `SmbPitchShifter` core but reports `LatencySamples`, keeps per-channel state in
   `float[][] scratch` (no per-call allocation of the internal `float[]`), and handles
   `Configure`/`Reset` properly. That removes findings 4 and 5's root cause.
2. **Replace `PitchDetector` with an existing pitch tracker.** NWaves 0.9.5 is already a
   dependency and ships YIN/pitch estimators (`NWaves.FeatureExtractors`), which do not have
   the long-lag bias or the 1024-window octave problem. As a stopgap: centre-clip, then take
   the *shortest* qualifying local maximum.
3. **Replace `LoudnessBalanceEffect` with `NAudio.Effects.AutomaticGainControlEffect`**
   (`TargetDb`, `MaxGainDb`, `MinGainDb`, `AttackMs`, `ReleaseMs`, `RmsWindowMs`,
   `UseVoiceDetection`) — its `UseVoiceDetection` directly addresses the silence-boost
   artefact in finding 7.
4. **Use `NAudio.Effects.EffectChain` + `ParameterDispatchQueue`** instead of the custom
   `DynamicChain`. `EffectChain` publishes edits atomically (same idea as the current code),
   *and* gives you `LatencySamples` (sum of the chain's latency) for delay compensation, and
   `Reset()` that actually resets every effect. `ParameterDispatchQueue` moves UI-thread
   parameter writes onto the audio thread, eliminating the
   `DenoiseEffect.SetModel`-style races.
5. **Consider `NAudio.Effects.NoiseSuppressionEffect`** (`Aggressiveness`, `SpectralFloor`,
   `FrameSize`) as a second built-in denoise backend — it reports `LatencySamples` and would
   not have the 480-multiple constraint that causes finding 1.
6. **Add a `FrameBuffer`/block-size-agnostic adapter** in front of any effect that needs a
   fixed frame size, so no effect can ever be handed a block length it cannot handle. This is
   the single highest-value structural change: findings 1, 12 and the `_dryBuffer` bypass in
   finding 15 all come from "the effect assumes 480".
7. **Allocate once, never in `Process`.** `DenoiseEffect.Read` is allocation-free, but
   `PitchShifter.Process` lets `SmbPitchShifter` work on its internal `float[]` converted from
   a `Span`; check for per-call array conversion on the audio path (the `float[]` overload
   `PitchShift(float, long, long, long, float, float[])` suggests one exists) and prefer the
   `Span` overload throughout.
8. **Instrument the chain with a single boundary-discontinuity meter** (max |x[n] − x[n−1]|
   per block, plus a NaN/Inf counter) exposed in `AudioDiagnostics`. Every defect in this
   report except 2, 3 and 7 would have shown up immediately as a spike; the app already has
   the `AudioDiagnostics` harness to hang it off.

