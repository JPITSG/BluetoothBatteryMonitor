const dateFormat = new Intl.DateTimeFormat(undefined, {
  month: "short", day: "numeric", hour: "numeric", minute: "2-digit",
});
const olderDateFormat = new Intl.DateTimeFormat(undefined, {
  year: "numeric", month: "short", day: "numeric", hour: "numeric", minute: "2-digit",
});

export default function LastCharged({ timestamp }: { timestamp?: string | null }) {
  if (!timestamp) return null;
  const date = new Date(timestamp);
  if (Number.isNaN(date.getTime())) return null;
  const formatter = date.getFullYear() === new Date().getFullYear() ? dateFormat : olderDateFormat;
  return (
    <span className="block text-neutral-500 text-[11px] leading-snug mt-0.5"
      title={`Detected a battery increase of at least 5 percentage points. Observed ${date.toLocaleString()}.`}>
      Last charged · <time dateTime={timestamp}>{formatter.format(date)}</time>
    </span>
  );
}
