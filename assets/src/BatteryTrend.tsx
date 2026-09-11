import type { BatteryReading } from "./lib/bridge";

// Sized to sit beside a device name inside the 420px dialog.
const WIDTH = 136;
const HEIGHT = 28;
// Half-pixel inset keeps the 0% and 100% hairlines crisp and the 2px line inside the plot.
const INSET = 3.5;
const HOUR = 60 * 60 * 1000;
const DAY = 24 * HOUR;
const LINE = "#2a78d6";

const clockFormat = new Intl.DateTimeFormat(undefined, { hour: "numeric", minute: "2-digit" });
const hourFormat = new Intl.DateTimeFormat(undefined, { hour: "numeric" });
const dayFormat = new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric" });
const sinceFormat = new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" });

interface Point {
  time: number;
  level: number;
}

interface Props {
  readings?: BatteryReading[] | null;
  online: boolean;
  batteryLevel: number | null;
  now: number;
}

function describe(first: Point, end: Point, span: number) {
  const change = end.level - first.level;
  const since = sinceFormat.format(first.time);
  if (change === 0) return `Steady at ${end.level}% since ${since}`;
  const perHour = Math.abs(change) / (span / HOUR);
  const rate = perHour >= 1
    ? `${perHour.toLocaleString(undefined, { maximumFractionDigits: perHour >= 10 ? 0 : 1 })}% per hour`
    : `${(perHour * 24).toLocaleString(undefined, { maximumFractionDigits: perHour * 24 >= 10 ? 0 : 1 })}% per day`;
  return `${change > 0 ? "Charging" : "Discharging"} ${first.level}% → ${end.level}% since ${since} · about ${rate}`;
}

// The current discharge since the last charge ended, or the current charge
// since it began: how quickly the battery is falling or rising.
export default function BatteryTrend({ readings, online, batteryLevel, now }: Props) {
  const points: Point[] = (readings ?? [])
    .map((reading) => ({ time: Date.parse(reading.observedAt), level: reading.percentage }))
    .filter((point) => Number.isFinite(point.time) && Number.isInteger(point.level) && point.level > 0 && point.level <= 100)
    .sort((left, right) => left.time - right.time);
  // A connected percentage stays current until the next change is logged, so
  // the line continues at that level to the present. Nothing is known about a
  // disconnected device after its last reading.
  const last = points[points.length - 1];
  if (last && online && batteryLevel !== null && batteryLevel > 0 && now > last.time) points.push({ time: now, level: batteryLevel });
  if (points.length < 2) return null;
  const first = points[0];
  const end = points[points.length - 1];
  const span = end.time - first.time;
  if (span <= 0) return null;

  const x = (time: number) => ((time - first.time) / span) * WIDTH;
  const y = (level: number) => INSET + ((100 - level) / 100) * (HEIGHT - 2 * INSET);
  const line = points.map((point, index) => `${index === 0 ? "M" : "L"}${x(point.time).toFixed(1)} ${y(point.level).toFixed(1)}`).join(" ");
  const format = span < 3 * HOUR ? clockFormat : span < DAY ? hourFormat : dayFormat;
  const labels = [first.time, first.time + span / 2, end.time].map((time) => format.format(time));
  const description = describe(first, end, span);

  return (
    <div className="shrink-0 w-[136px]" title={description}>
      <svg role="img" aria-label={description} width={WIDTH} height={HEIGHT} viewBox={`0 0 ${WIDTH} ${HEIGHT}`} className="block overflow-visible">
        <line x1={0} y1={y(100)} x2={WIDTH} y2={y(100)} stroke="#e5e5e5" />
        <line x1={0} y1={y(0)} x2={WIDTH} y2={y(0)} stroke="#e5e5e5" />
        <path d={`${line} L${WIDTH} ${y(0)} L0 ${y(0)} Z`} fill={LINE} fillOpacity={0.1} />
        <path d={line} fill="none" stroke={LINE} strokeWidth={2} strokeLinejoin="round" strokeLinecap="round" />
        <circle cx={WIDTH} cy={y(end.level)} r={4.5} fill="#fff" />
        <circle cx={WIDTH} cy={y(end.level)} r={3} fill={LINE} />
      </svg>
      <div aria-hidden="true" className="grid grid-cols-3 text-[10px] leading-3 text-neutral-500 whitespace-nowrap">
        <span>{labels[0]}</span>
        <span className="text-center">{labels[1]}</span>
        <span className="text-right">{labels[2]}</span>
      </div>
    </div>
  );
}
