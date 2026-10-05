import { useEffect, useRef } from "react";
import { IconPower } from "@tabler/icons-react";

/**
 * How a database stands, as one of the four colours it is shown in. A class of the same name
 * (`power-on`, `power-off`, `power-busy`, `power-bad`) sets `--power` on whatever carries it, so the
 * orb, the word beside it and the tile around it all take the colour from one place.
 */
export type PowerTone = "on" | "off" | "busy" | "bad";

export function powerTone(state: string): PowerTone {
  if (state === "Open") return "on";
  if (state === "Error") return "bad";
  if (state === "Opening" || state === "Closing") return "busy";
  return "off"; // Closed, Disposed
}

interface PowerOrbProps {
  /** the container's state word: Open, Closed, Opening, Closing, Error, Disposed */
  state: string;
  /** in pixels: the dashboard's is the big one, the database list's and the header's smaller */
  size?: number;
  /** 0..100 while it opens; without one the lit part of the ring turns, as a wait nobody has measured */
  progress?: number | null;
  /** given, the orb is the database's power button rather than a picture of it */
  onClick?: () => void;
  disabled?: boolean;
  title?: string;
}

/** one turn of the ring while an open has no estimate yet: calm, it is a wait and not an alarm */
const turnMs = 1400;

/**
 * The power symbol in a lit ring - a device's power button, which says whether it is on by the light
 * round it before anything is read. The ring is the state: all of it lit in the state's colour when
 * the database is up (or has failed), a dim track when it is off, and while it opens the part of the
 * ring that is lit is how far the open has come.
 *
 * The lit part is always drawn, empty or not, so that a change of state is the ring filling or
 * emptying (and the colours fading, in the css) rather than a different picture replacing it. The
 * turning of an unmeasured wait is a script animation rather than a css one for the same reason:
 * when the wait ends, the arc finishes the turn it is on and comes to rest at the top, where the
 * ring fills from, instead of snapping back there.
 */
export function PowerOrb({ state, size = 44, progress, onClick, disabled, title }: PowerOrbProps) {
  const tone = powerTone(state);
  const stroke = size >= 40 ? 2.5 : size >= 26 ? 2 : 1.6;
  const centre = size / 2;
  const radius = centre - stroke / 2 - 0.5;
  const circumference = 2 * Math.PI * radius;
  const measured = tone === "busy" && progress != null && progress > 0;
  const waiting = tone === "busy" && !measured;
  // how much of the ring is lit; none at all when off
  const lit = tone === "on" || tone === "bad" ? 1 : measured ? Math.min(1, progress! / 100) : waiting ? 0.28 : 0;
  const ring = useRef<SVGSVGElement>(null);

  useEffect(() => {
    const svg = ring.current;
    if (!svg || !waiting || typeof svg.animate !== "function") return;
    if (window.matchMedia("(prefers-reduced-motion: reduce)").matches) return;
    const turning = svg.animate([{ transform: "rotate(0deg)" }, { transform: "rotate(360deg)" }], { duration: turnMs, iterations: Infinity });
    return () => {
      const at = typeof turning.currentTime === "number" ? turning.currentTime : 0;
      const angle = ((at % turnMs) / turnMs) * 360;
      turning.cancel();
      if (!svg.isConnected) return; // gone with the page, nothing to bring to rest
      // an ease-out cubic starts at three times its average speed, so a duration of three times the
      // rest of the turn at the turning speed carries on at exactly the speed it was going and
      // slows from there - no jolt where the turning ends and the settling begins
      svg.animate([{ transform: `rotate(${angle}deg)` }, { transform: "rotate(360deg)" }], {
        duration: Math.max(250, ((360 - angle) / 360) * turnMs * 3),
        easing: "cubic-bezier(0.33, 1, 0.68, 1)",
      });
    };
  }, [waiting]);

  const className = "power-orb power-" + tone + (onClick ? " power-switch" : "");
  const gap = Math.max(1.5, size * 0.06);
  const body = (
    <>
      <svg ref={ring} className="power-ring" width={size} height={size} viewBox={`0 0 ${size} ${size}`} aria-hidden="true">
        <circle className="power-track" cx={centre} cy={centre} r={radius} strokeWidth={stroke} />
        {/* a dash the length of the ring and a gap twice that: an offset of the whole length shows
            only the gap, so an empty ring draws nothing at all */}
        <circle
          className="power-arc"
          cx={centre}
          cy={centre}
          r={radius}
          strokeWidth={stroke}
          strokeDasharray={`${circumference} ${circumference * 2}`}
          strokeDashoffset={circumference * (1 - lit)}
          transform={`rotate(-90 ${centre} ${centre})`}
        />
      </svg>
      <span className="power-core" style={{ inset: stroke + gap }}>
        <IconPower size={Math.round(size * 0.42)} stroke={size >= 40 ? 2.2 : 2} />
      </span>
    </>
  );
  const style = { width: size, height: size };
  if (onClick) {
    return (
      <button type="button" className={className} style={style} onClick={onClick} disabled={disabled} title={title} aria-label={title}>
        {body}
      </button>
    );
  }
  return (
    <span className={className} style={style} title={title ?? state} role="img" aria-label={title ?? state}>
      {body}
    </span>
  );
}
