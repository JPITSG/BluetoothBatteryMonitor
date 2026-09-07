import { useEffect, useState } from "react";
import { onHostMessage, postMessage, type DeviceStatus } from "./lib/bridge";

export default function DeviceStatusList({ initialStatuses }: { initialStatuses: DeviceStatus[] }) {
  const [statuses, setStatuses] = useState(initialStatuses);

  useEffect(() => {
    const unsubscribe = onHostMessage((message) => {
      if (message.type === "deviceStatus") setStatuses(message.deviceStatuses);
    });
    // Pick up any connection change between the initial snapshot and mounting.
    postMessage({ action: "getDeviceStatus" });
    return unsubscribe;
  }, []);

  if (statuses.length === 0) {
    return <p className="text-[11px] leading-snug text-neutral-500">No devices are being monitored.</p>;
  }

  return (
    <dl aria-label="Monitored device status" className="space-y-1 text-[11px] leading-snug">
      {statuses.map((device) => (
        <div key={device.name} className="flex items-baseline justify-between gap-3">
          <dt className="min-w-0 break-words text-neutral-600">{device.name}</dt>
          <dd className={`shrink-0 whitespace-nowrap tabular-nums ${device.online ? "text-green-700" : "text-neutral-500"}`}>
            {device.online ? `Online · ${device.batteryLevel === null ? "Battery unknown" : `${device.batteryLevel}%`}` : "Offline"}
          </dd>
        </div>
      ))}
    </dl>
  );
}
