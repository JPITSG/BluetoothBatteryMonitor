import { useEffect, useState } from "react";
import { Checkbox } from "./components/ui/checkbox";
import { Label } from "./components/ui/label";
import { saveDevices, onHostMessage, postMessage, type InitData } from "./lib/bridge";
import UpdateControls from "./UpdateControls";

export default function ConfigView({ devices, version, autoCheck, loadingDevices, deviceError, deviceStatuses }: InitData) {
  const [automatic, setAutomatic] = useState(autoCheck);
  const [statuses, setStatuses] = useState(deviceStatuses);
  useEffect(() => {
    const unsubscribe = onHostMessage((message) => {
      if (message.type === "deviceStatus") setStatuses(message.deviceStatuses);
    });
    postMessage({ action: "getDeviceStatus" });
    return unsubscribe;
  }, []);
  const [selected, setSelected] = useState<Set<string>>(
    () => new Set(devices.filter((device) => device.isConfigured).map((device) => device.name))
  );
  // Discovery cannot undo edits made while it was running, including a selected
  // cached device which Windows temporarily leaves out of its refreshed list.
  const visibleDevices = [...devices];
  for (const name of selected) {
    if (!visibleDevices.some((device) => device.name === name)) visibleDevices.push({ name, isConfigured: true });
  }
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
        <section aria-labelledby="monitor-devices" className="space-y-1">
          <h2 id="monitor-devices" className="text-xs font-medium leading-none">Monitor devices</h2>
          <div className="rounded-md border border-neutral-200 p-2 space-y-3" aria-busy={loadingDevices}>
            {visibleDevices.length === 0 ? (
              <p className="text-neutral-500 py-1 text-center">{loadingDevices ? "Finding paired Bluetooth devices…" : "No paired Bluetooth devices found."}</p>
            ) : visibleDevices.map((device) => {
              const status = statuses.find((item) => item.name.toLowerCase() === device.name.toLowerCase());
              return (
                <label key={device.name} className="flex items-start gap-2 cursor-pointer select-none">
                  <Checkbox checked={selected.has(device.name)} onChange={() => toggle(device.name)} />
                  <span className="min-w-0 break-words leading-4">{device.name}{status && (
                    <span className={status.online ? "text-green-700" : "text-neutral-500"}>
                      {status.online ? ` · Connected · ${status.batteryLevel === null ? "Battery unknown" : `${status.batteryLevel}%`}` : " · Disconnected"}
                    </span>
                  )}</span>
                </label>
              );
            })}
          </div>
          {loadingDevices && visibleDevices.length > 0 && <p role="status" className="text-neutral-500 text-[11px] leading-snug">Refreshing devices…</p>}
          {deviceError && <p role="status" className="text-neutral-500 text-[11px] leading-snug">{deviceError}</p>}
        </section>

        <div className="flex items-start gap-2 pt-1">
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
