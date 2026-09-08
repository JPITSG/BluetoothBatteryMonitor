const dateFormat = new Intl.DateTimeFormat(undefined, {
  month: "short", day: "numeric", hour: "numeric", minute: "2-digit",
});
const olderDateFormat = new Intl.DateTimeFormat(undefined, {
  year: "numeric", month: "short", day: "numeric", hour: "numeric", minute: "2-digit",
});

export default function LastCharged({ timestamp }: { timestamp?: string | null }) {
  const parsed = timestamp ? new Date(timestamp) : null;
  const date = parsed && !Number.isNaN(parsed.getTime()) ? parsed : null;
  const formatter = date?.getFullYear() === new Date().getFullYear() ? dateFormat : olderDateFormat;
  return (
    <span className="block text-neutral-500 text-[11px] leading-snug mt-0.5"
      title={date
        ? `Detected a battery increase of at least 5 percentage points. Observed ${date.toLocaleString()}.`
        : "Waiting for a battery increase of at least 5 percentage points."}>
      Last charged · {date ? <time dateTime={timestamp ?? undefined}>{formatter.format(date)}</time> : "Collecting data"}
    </span>
  );
}
