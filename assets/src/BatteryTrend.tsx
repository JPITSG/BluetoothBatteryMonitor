import type { BatteryReading, OfflinePeriod } from "./lib/bridge";

// Sized to sit beside a device name inside the 420px dialog.
const WIDTH = 136;
const HEIGHT = 28;
// Half-pixel inset keeps the 0% and 100% hairlines crisp and the 2px line inside the plot.
const INSET = 3.5;
const MINUTE = 60 * 1000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;
const LINE = "#2a78d6";
// A light red, drawn over the battery area so offline time has one colour
// whether or not the line runs through it.
const OFFLINE = "#fecaca";
// Shading fills the plot between the 100% and 0% hairlines.
const BAND_TOP = 4;
const BAND_HEIGHT = 20;
// Offline periods at least this many pixels wide get their own tooltip.
const HOVER_WIDTH = 3;

const clockFormat = new Intl.DateTimeFormat(undefined, { hour: "numeric", minute: "2-digit" });
const hourFormat = new Intl.DateTimeFormat(undefined, { hour: "numeric" });
const dayFormat = new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric" });
const sinceFormat = new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" });

interface Point {
  time: number;
  level: number;
}

interface Gap {
  from: number;
  to: number;
  open: boolean;
  unmonitored: boolean;
}

interface Props {
  readings?: BatteryReading[] | null;
  online: boolean;
  batteryLevel: number | null;
  now: number;
  offline?: OfflinePeriod[] | null;
  connectionHistoryStart?: string | null;
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

function formatDuration(milliseconds: number) {
  const minutes = Math.round(milliseconds / MINUTE);
  if (minutes < 1) return "under a minute";
  if (minutes < 60) return `${minutes} min`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return minutes % 60 ? `${hours} h ${minutes % 60} min` : `${hours} h`;
  return hours % 24 ? `${Math.floor(hours / 24)} d ${hours % 24} h` : `${hours / 24} d`;
}

function formatRange(from: number, to: number) {
  const sameDay = new Date(from).toDateString() === new Date(to).toDateString();
  return `${sinceFormat.format(from)} – ${(sameDay ? clockFormat : sinceFormat).format(to)}`;
}

function describeGap(gap: Gap, now: number) {
  const cause = gap.unmonitored ? "Not monitored (PC asleep or off, or app closed)" : "Disconnected";
  return gap.open
    ? `${cause} since ${sinceFormat.format(gap.from)} · ${formatDuration(now - gap.from)}`
    : `${cause} · ${formatRange(gap.from, gap.to)} · ${formatDuration(gap.to - gap.from)}`;
}

// How often and how long the device was offline within the graph. Periods
// before connection changes were first recorded are unknown, not connected.
function describeOffline(gaps: Gap[], start: number, end: number, historyStart: number | null) {
  if (historyStart === null || historyStart >= end) return null;
  const covered = Math.max(start, historyStart);
  let count = 0;
  let total = 0;
  for (const gap of gaps) {
    const from = Math.max(gap.from, covered);
    const to = Math.min(gap.to, end);
    if (to > from) {
      count++;
      total += to - from;
    }
  }
  const since = historyStart > start ? ` since ${sinceFormat.format(historyStart)}` : "";
  const summary = count === 0 ? `No disconnections${since}`
    : `offline ${count === 1 ? "once" : `${count} times`}, ${formatDuration(total)} in total${since}`;
  const open = gaps.find((gap) => gap.open);
  if (!open) return summary.charAt(0).toUpperCase() + summary.slice(1);
  const current = `${open.unmonitored ? "Not monitored" : "Disconnected"} since ${sinceFormat.format(open.from)}`;
  return count > 1 ? `${current} · ${summary}` : current;
}

// The current discharge since the last charge ended, or the current charge
// since it began: how quickly the battery is falling or rising, and when the
// device was offline.
export default function BatteryTrend({ readings, online, batteryLevel, now, offline, connectionHistoryStart }: Props) {
  const points: Point[] = (readings ?? [])
    .map((reading) => ({ time: Date.parse(reading.observedAt), level: reading.percentage }))
    .filter((point) => Number.isFinite(point.time) && Number.isInteger(point.level) && point.level > 0 && point.level <= 100)
    .sort((left, right) => left.time - right.time);
  // A connected percentage stays current until the next change is logged, so
  // the line continues at that level to the present. Nothing is known about a
  // disconnected device after its last reading.
  const live = online && batteryLevel !== null && batteryLevel > 0;
  const last = points[points.length - 1];
  if (last && live && now > last.time) points.push({ time: now, level: batteryLevel });
  if (points.length < 2) return null;
  const first = points[0];
  const end = points[points.length - 1];
  const span = end.time - first.time;
  if (span <= 0) return null;

  const gaps: Gap[] = (offline ?? []).flatMap((period) => {
    const from = Date.parse(period.from);
    const open = period.to == null;
    const to = open ? now : Date.parse(period.to as string);
    return Number.isFinite(from) && Number.isFinite(to) && to > from
      ? [{ from, to, open, unmonitored: period.reason === "unmonitored" }]
      : [];
  }).sort((left, right) => left.from - right.from);
  // A disconnection after the last reading extends the time axis so it shows.
  const domainEnd = Math.max(end.time, ...gaps.map((gap) => gap.to));
  const domain = domainEnd - first.time;
  const x = (time: number) => ((time - first.time) / domain) * WIDTH;
  const y = (level: number) => INSET + ((100 - level) / 100) * (HEIGHT - 2 * INSET);

  const bands = gaps.flatMap((gap) => {
    const left = Math.max(0, x(gap.from));
    const right = Math.min(WIDTH, x(gap.to));
    return right > left ? [{ gap, left, right }] : [];
  });
  // Draw only recorded intervals. Blurring short gaps across nearby columns
  // smeared red into connected time, especially on long histories. Keep
  // subpixel boundaries; one compound path also avoids thousands of SVG nodes.
  const offlinePath = bands.map(({ left, right }) =>
    `M${left} ${BAND_TOP}H${right}V${BAND_TOP + BAND_HEIGHT}H${left}Z`).join(" ");

  const lineEnd = x(end.time);
  const line = points.map((point, index) => `${index === 0 ? "M" : "L"}${x(point.time).toFixed(1)} ${y(point.level).toFixed(1)}`).join(" ");
  const format = domain < 3 * HOUR ? clockFormat : domain < DAY ? hourFormat : dayFormat;
  const labels = [first.time, first.time + domain / 2, domainEnd].map((time) => format.format(time));
  const historyStart = connectionHistoryStart ? Date.parse(connectionHistoryStart) : NaN;
  const description = [describe(first, end, span),
    describeOffline(gaps, first.time, domainEnd, Number.isFinite(historyStart) ? historyStart : null)]
    .filter(Boolean).join("\n");

  return (
    <div className="shrink-0 w-[136px]" title={description}>
      <svg role="img" aria-label={description} width={WIDTH} height={HEIGHT} viewBox={`0 0 ${WIDTH} ${HEIGHT}`} className="block overflow-visible">
        <line x1={0} y1={y(100)} x2={WIDTH} y2={y(100)} stroke="#e5e5e5" />
        <line x1={0} y1={y(0)} x2={WIDTH} y2={y(0)} stroke="#e5e5e5" />
        <path d={`${line} L${lineEnd.toFixed(1)} ${y(0)} L0 ${y(0)} Z`} fill={LINE} fillOpacity={0.1} />
        {offlinePath && <path d={offlinePath} fill={OFFLINE} data-offline="" />}
        <path d={line} fill="none" stroke={LINE} strokeWidth={2} strokeLinejoin="round" strokeLinecap="round" />
        <circle cx={lineEnd} cy={y(end.level)} r={4.5} fill="#fff" />
        {/* A filled dot is the current level; a ring is the last level known before going offline. */}
        {live
          ? <circle cx={lineEnd} cy={y(end.level)} r={3} fill={LINE} />
          : <circle cx={lineEnd} cy={y(end.level)} r={2.75} fill="#fff" stroke={LINE} strokeWidth={1.5} />}
        {bands.map(({ gap, left, right }) => right - left >= HOVER_WIDTH && (
          <rect key={`${gap.from}-${gap.to}`} x={left} y={BAND_TOP} width={right - left} height={BAND_HEIGHT} fill="transparent" data-gap="">
            <title>{describeGap(gap, now)}</title>
          </rect>
        ))}
      </svg>
      <div aria-hidden="true" className="grid grid-cols-3 text-[10px] leading-3 text-neutral-500 whitespace-nowrap">
        <span>{labels[0]}</span>
        <span className="text-center">{labels[1]}</span>
        <span className="text-right">{labels[2]}</span>
      </div>
    </div>
  );
}
