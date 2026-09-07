/**
 * The engine, heard rather than seen.
 *
 * Synthesised, not sampled: the admin UI ships no audio files and is not about to start, and an
 * engine is one of the few sounds that is honestly periodic - so a handful of oscillators say it
 * better than a loop would, and they can follow the throttle continuously instead of crossfading
 * between two recordings.
 *
 * What makes it read as an engine rather than a tone:
 *   - the firing frequency, and its second and third harmonics, as sawtooths. A four-stroke four
 *     fires twice a revolution, so the fundamental is rpm/30 - about 27 Hz idling and 97 flat out,
 *     which is the register a small aero engine sits in. The revs are the throttle AND the airspeed:
 *     the propeller is fixed pitch, so it unloads as the aeroplane speeds up and revs higher for the
 *     same lever - which is why a dive sounds like a dive and a climb sounds like work, and why an
 *     engine at idle still turns over on the airflow alone;
 *   - two of them detuned by a fraction of a percent, which beats slowly and is what keeps the
 *     sound from being a synthesiser note;
 *   - a low-passed noise bed for the exhaust, opening with the throttle;
 *   - a second noise bed, band-passed and driven by airspeed rather than throttle, for the air over
 *     the canopy - it is what makes a dive sound like a dive with the engine at idle;
 *   - and, because nothing mechanical is even, three kinds of unsteadiness: a slow amplitude wobble,
 *     a drifting random walk in the revs, and a faster shiver on top of it. Together they are the
 *     difference between an engine and an organ note - a synthesiser holds a pitch, an engine never
 *     quite does.
 *
 * It is deliberately quiet: this is a database's admin UI, and the aeroplane is a joke hidden behind
 * a keystroke. Full throttle at the master gain below is about as loud as a laptop fan.
 */

/** The loudest the whole thing ever gets. Low on purpose - see above. */
const MASTER = 0.055;

export interface EngineSound {
  /**
   * The lever (0..1) and the airspeed in m/s - the revs come from both. `stopped` silences it
   * without tearing it down, for an aeroplane held still in mid air or lying in a field.
   */
  update(throttle: number, airspeed: number, stopped: boolean): void;
  stop(): void;
}

/**
 * Starts the engine. Returns null where there is no audio to be had - a browser without WebAudio, or
 * one that will not let a page make a sound yet; fun mode is entered with a keystroke, which counts
 * as the gesture browsers ask for, so in practice this succeeds.
 */
export function startEngineSound(): EngineSound | null {
  const Ctor = window.AudioContext ?? (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
  if (!Ctor) return null;
  let ctx: AudioContext;
  try {
    ctx = new Ctor();
  } catch {
    return null;
  }
  void ctx.resume();

  const now = () => ctx.currentTime;
  const master = ctx.createGain();
  master.gain.value = 0;
  master.connect(ctx.destination);

  // ---- the engine itself: three harmonics, one of them a detuned pair ----
  const engine = ctx.createGain();
  engine.gain.value = 0.9;
  // the cabin, more or less: everything above a few hundred hertz belongs to the exhaust and the air
  const body = ctx.createBiquadFilter();
  body.type = "lowpass";
  body.frequency.value = 620;
  body.Q.value = 0.7;
  engine.connect(body);
  body.connect(master);

  const harmonics: { osc: OscillatorNode; mul: number; detune: number }[] = [];
  const addHarmonic = (mul: number, level: number, detune: number, type: OscillatorType = "sawtooth") => {
    const osc = ctx.createOscillator();
    osc.type = type;
    const gain = ctx.createGain();
    gain.gain.value = level;
    osc.connect(gain);
    gain.connect(engine);
    osc.start();
    harmonics.push({ osc, mul, detune });
  };
  addHarmonic(1, 0.5, 0);
  addHarmonic(1, 0.34, 7); // the beat against the first: seven cents of detune, a slow throb
  addHarmonic(2, 0.22, 0);
  addHarmonic(3, 0.1, 0, "triangle");

  // ---- the noise beds: one exhaust, one airflow ----
  const noiseBuffer = ctx.createBuffer(1, ctx.sampleRate * 2, ctx.sampleRate);
  const data = noiseBuffer.getChannelData(0);
  for (let i = 0; i < data.length; i++) data[i] = Math.random() * 2 - 1;

  const bed = (type: BiquadFilterType, frequency: number, q: number) => {
    const src = ctx.createBufferSource();
    src.buffer = noiseBuffer;
    src.loop = true;
    const filter = ctx.createBiquadFilter();
    filter.type = type;
    filter.frequency.value = frequency;
    filter.Q.value = q;
    const gain = ctx.createGain();
    gain.gain.value = 0;
    src.connect(filter);
    filter.connect(gain);
    gain.connect(master);
    src.start();
    return { src, filter, gain };
  };
  const exhaust = bed("lowpass", 340, 1.1);
  const air = bed("bandpass", 900, 0.6);

  // the wobble: a shallow amplitude drift over the whole thing, in two rates that do not divide into
  // each other, so the pattern never comes round audibly. Shallow is the point - see the note on
  // depth by the random walk below.
  const wobbles = [
    { hz: 5.7, depth: 0.026 },
    { hz: 1.31, depth: 0.018 },
  ].map(({ hz, depth }) => {
    const osc = ctx.createOscillator();
    osc.type = "sine";
    osc.frequency.value = hz;
    const gain = ctx.createGain();
    gain.gain.value = depth;
    osc.connect(gain);
    gain.connect(engine.gain);
    osc.start();
    return osc;
  });

  let alive = true;
  // The unsteadiness. `walk` is a random target the revs drift towards, re-aimed a few times a
  // second, which is the slow hunting of an engine that is not governed; `shiver` is a fast, small
  // tremor over the top of it. Both are held here rather than as audio nodes, because the update
  // below already runs every frame and a value computed there can be glided to like any other.
  let walk = 0;
  let walkTarget = 0;
  let reaim = 0;
  let shiver = 0;
  let last = now();

  return {
    update(throttle, airspeed, stopped) {
      if (!alive) return;
      const t = now();
      const dt = Math.max(0, Math.min(0.25, t - last));
      last = t;
      const ease = 0.09; // the time constant every value is glided over, so nothing steps
      const set = (param: AudioParam, value: number) => param.setTargetAtTime(value, t, ease);
      if (stopped) {
        set(master.gain, 0);
        return;
      }
      // ---- the roughness ----
      // Idling is where an engine is least even, so the drift is widest with the throttle closed.
      // The depths here are deliberately small - under a percent of the revs at cruise. An engine
      // that wanders audibly does not sound more real, it sounds like a tape with a stretched hub;
      // what makes it read as machinery is that the unsteadiness is THERE, not that it is large.
      const roughness = 0.007 + 0.009 * (1 - throttle);
      reaim -= dt;
      if (reaim <= 0) {
        reaim = 0.18 + Math.random() * 0.5;
        walkTarget = (Math.random() * 2 - 1) * roughness;
      }
      walk += (walkTarget - walk) * Math.min(1, dt * 4.5);
      shiver = shiver * 0.86 + (Math.random() * 2 - 1) * 0.0016;

      // ---- the revs: what the lever asks for, and what the air is doing to the propeller ----
      // A fixed-pitch propeller has no governor: the lever sets the power, and the speed the
      // aeroplane is going decides how much of it the propeller can absorb. So the revs are both,
      // and the airspeed's share grows with the throttle - a windmilling propeller at idle turns
      // over quietly, an open throttle in a dive is the loudest the engine ever gets.
      const power = Math.max(0, Math.min(1, throttle));
      const fast = Math.max(0, Math.min(1, (airspeed - 15) / 55));
      const rpm = (800 + 1500 * power + 600 * fast * (0.5 + 0.5 * power)) * (1 + walk + shiver);
      const f = rpm / 30;
      const revs = Math.max(0, Math.min(1, (rpm - 800) / 2000)); // 0 at idle, 1 flat out and fast
      for (const h of harmonics) {
        set(h.osc.frequency, f * h.mul);
        // the harmonics wander apart a shade rather than staying locked, which is what stops the
        // stack of them reading as one sawtooth - a few cents, not a bent note
        h.osc.detune.value = h.detune + walk * 240 * h.mul;
      }
      // The engine gets louder AND brighter as it turns faster: an engine under load is not the same
      // sound turned up, it is more of the harmonics that were always there. Brightness follows the
      // revs rather than the lever, so the dive brightens too.
      set(body.frequency, 480 + 900 * revs);
      set(exhaust.gain.gain, 0.1 + 0.24 * revs);
      set(exhaust.filter.frequency, 260 + 420 * revs);
      // the air over the canopy answers to speed alone, which is what carries a glide with the
      // throttle shut: the engine goes quiet and the airflow does not
      const v = Math.max(0, Math.min(1, (airspeed - 12) / 60));
      set(air.gain.gain, 0.05 + 0.3 * v * v);
      set(air.filter.frequency, 700 + 1500 * v);
      // and the loudness takes a little from the speed as well as from the lever, and breathes with
      // the roughness by a fraction of what the pitch does
      set(master.gain, MASTER * (0.5 + 0.36 * power + 0.14 * fast) * (1 + walk * 1.4));
    },
    stop() {
      if (!alive) return;
      alive = false;
      // fade before tearing anything down, so leaving fun mode does not end in a click
      master.gain.setTargetAtTime(0, now(), 0.05);
      const end = now() + 0.35;
      for (const h of harmonics) h.osc.stop(end);
      for (const w of wobbles) w.stop(end);
      exhaust.src.stop(end);
      air.src.stop(end);
      window.setTimeout(() => void ctx.close(), 500);
    },
  };
}
