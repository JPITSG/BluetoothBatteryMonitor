import { useEffect, useState } from "react";
import { Checkbox } from "./components/ui/checkbox";
import { Label } from "./components/ui/label";
import { saveDevices, onHostMessage, postMessage, type InitData } from "./lib/bridge";
import UpdateControls from "./UpdateControls";
import LastCharged from "./LastCharged";
import BatteryTrend from "./BatteryTrend";

export default function ConfigView({ devices, version, autoCheck, startWithWindows, loadingDevices, deviceError, deviceStatuses }: InitData) {
  const [automatic, setAutomatic] = useState(autoCheck);
  const [startup, setStartup] = useState(startWithWindows);
  const [statuses, setStatuses] = useState(deviceStatuses);
  // Trend graphs extend a connected device's level to the present.
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const unsubscribe = onHostMessage((message) => {
      if (message.type === "deviceStatus") {
        setStatuses(message.deviceStatuses);
        setNow(Date.now());
      }
      if (message.type === "startWithWindows") setStartup(message.enabled);
    });
    postMessage({ action: "getDeviceStatus" });
    const clock = setInterval(() => setNow(Date.now()), 60_000);
    return () => {
      unsubscribe();
      clearInterval(clock);
    };
  }, []);
  const [selected, setSelected] = useState<Set<string>>(
    () => new Set(devices.filter((device) => device.isConfigured).map((device) => device.name))
  );
  const statusByName = new Map(statuses.map((status) => [status.name.toLowerCase(), status]));
  // Discovery cannot undo edits made while it was running, including a selected
  // cached device which Windows temporarily leaves out of its refreshed list.
  const visibleDevices = [...devices];
  for (const name of selected) {
    if (!visibleDevices.some((device) => device.name === name)) visibleDevices.push({ name, isConfigured: true });
  }
  // Keep checked devices first, with connected devices leading that group.
  // Preserve alphabetical order within each group, including unchecked devices.
  visibleDevices.sort((left, right) =>
    Number(selected.has(right.name)) - Number(selected.has(left.name)) ||
    (selected.has(left.name) && selected.has(right.name)
      ? Number(statusByName.get(right.name.toLowerCase())?.online ?? false) -
        Number(statusByName.get(left.name.toLowerCase())?.online ?? false)
      : 0) ||
    left.name.localeCompare(right.name, undefined, { sensitivity: "base" })
  );
  const toggle = (name: string) => {
    setSelected((current) => {
      const next = new Set(current);
      if (next.has(name)) next.delete(name);
      else next.add(name);
      return next;
    });
  };

  return (
    <div className="p-4 space-y-3 text-xs">
      <div className="space-y-3">
        <section aria-labelledby="monitored-devices" className="space-y-1">
          <h2 id="monitored-devices" className="text-xs font-medium leading-none">Monitored</h2>
          <div className="rounded-md border border-neutral-200 p-2 space-y-3" aria-busy={loadingDevices}>
            {visibleDevices.length === 0 ? (
              <p className="text-neutral-500 py-1 text-center">{loadingDevices ? "Finding paired Bluetooth devices…" : "No paired Bluetooth devices found."}</p>
            ) : visibleDevices.map((device) => {
              const status = statusByName.get(device.name.toLowerCase());
              return (
                // Only the checkbox toggles monitoring; the name, status and graph are not click targets.
                <div key={device.name} data-device={device.name} className="flex items-start gap-2">
                  {/* Segoe UI glyphs sit low in the 16px line box; 1px centres the box on the name. */}
                  <Checkbox className="mt-px" aria-label={device.name} checked={selected.has(device.name)} onChange={() => toggle(device.name)} />
                  <span className="min-w-0 flex-1 break-words leading-4">{device.name}{status && (
                    <span className={status.online ? "text-green-700" : "text-red-600"}>
                      {status.online ? ` · Connected · ${status.batteryLevel === null ? "Battery unknown" : `${status.batteryLevel}%`}` : " · Disconnected"}
                    </span>
                  )}{status && <LastCharged timestamp={status.lastChargedAt} />}</span>
                  {status && <BatteryTrend readings={status.trend} online={status.online} batteryLevel={status.batteryLevel} now={now}
                    offline={status.offline} connectionHistoryStart={status.connectionHistoryStart} />}
                </div>
              );
            })}
          </div>
          {loadingDevices && visibleDevices.length > 0 && <p role="status" className="text-neutral-500 text-[11px] leading-snug">Refreshing devices…</p>}
          {deviceError && <p role="status" className="text-neutral-500 text-[11px] leading-snug">{deviceError}</p>}
        </section>

        {/* Both options apply immediately; Save stores the device selection. */}
        <div className="flex items-start gap-2 pt-1">
          <Checkbox id="startWithWindows" checked={startup} className="mt-0.5" onChange={(event) => {
            setStartup(event.target.checked);
            postMessage({ action: "startWithWindows", enabled: event.target.checked });
          }} />
          <div className="space-y-0.5">
            <Label htmlFor="startWithWindows" className="cursor-pointer">Start with Windows</Label>
            <p className="text-neutral-500 text-[11px] leading-snug">Launches in the tray when you sign in to Windows.</p>
          </div>
        </div>

        <div className="flex items-start gap-2">
          <Checkbox id="autoCheckForUpdates" checked={automatic} className="mt-0.5" onChange={(event) => {
            setAutomatic(event.target.checked);
            postMessage({ action: "autoUpdate", enabled: event.target.checked });
          }} />
          <div className="space-y-0.5">
            <Label htmlFor="autoCheckForUpdates" className="cursor-pointer">Automatically check for updates</Label>
            <p className="text-neutral-500 text-[11px] leading-snug">Checks at startup, whenever this dialog opens, and every 60 minutes. Prompts only when a newer version is available.</p>
          </div>
        </div>
      </div>

      <UpdateControls version={version} onSave={() => saveDevices(Array.from(selected))} />
    </div>
  );
}
