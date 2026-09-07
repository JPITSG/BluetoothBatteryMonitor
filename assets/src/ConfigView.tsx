import { useEffect, useState } from "react";
import { Button } from "./components/ui/button";
import { saveDevices, closeDialog, postMessage, type UpdateState, type DeviceEntry } from "./lib/bridge";

interface ConfigViewProps {
  devices: DeviceEntry[];
  version: string;
  autoCheck: boolean;
}

export default function ConfigView({ devices, version, autoCheck }: ConfigViewProps) {
  const [automatic, setAutomatic] = useState(autoCheck);
  const [update, setUpdate] = useState<UpdateState>({ status: "", busy: false, canInstall: false, automatic: false });
  useEffect(() => {
    const receive = (event: MessageEvent<UpdateState>) => setUpdate(event.data);
    window.chrome?.webview?.addEventListener("message", receive);
    postMessage({ action: "getUpdateState" });
    return () => window.chrome?.webview?.removeEventListener("message", receive);
  }, []);
  const [selected, setSelected] = useState<Set<string>>(
    () => new Set(devices.filter((d) => d.isConfigured).map((d) => d.name))
  );

  const toggle = (name: string) => {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(name)) {
        next.delete(name);
      } else {
        next.add(name);
      }
      return next;
    });
  };

  const handleSave = () => {
    saveDevices(Array.from(selected));
  };

  return (
    <div className="p-5 flex flex-col gap-3 max-w-md mx-auto text-xs">
      {devices.length === 0 ? (
        <div className="text-neutral-500 py-4 text-center">
          No paired Bluetooth devices found.
        </div>
      ) : (
        <div className="flex flex-col gap-1 max-h-[240px] overflow-y-auto">
          {devices.map((device) => (
            <label
              key={device.name}
              className="flex items-center gap-2.5 px-2 py-1.5 rounded hover:bg-neutral-50 cursor-pointer select-none"
            >
              <input
                type="checkbox"
                checked={selected.has(device.name)}
                onChange={() => toggle(device.name)}
                className="h-3.5 w-3.5 rounded border-neutral-300 accent-neutral-900 cursor-pointer"
              />
              <span className="truncate">{device.name}</span>
            </label>
          ))}
        </div>
      )}

      <div className="border-t pt-3 flex flex-col gap-2">
        <label className="flex items-center gap-2">
          <input type="checkbox" checked={automatic} onChange={(event) => {
            setAutomatic(event.target.checked);
            postMessage({ action: "autoUpdate", enabled: event.target.checked });
          }} />
          Automatically check for updates
        </label>
        <Button variant="outline" size="sm" onClick={() => postMessage({ action: update.busy ? "cancelUpdate" : "checkUpdate" })}>
          {update.busy ? "Cancel update check" : "Check for updates"}
        </Button>
        {update.status && <p role="status" className="text-neutral-600 break-words">{update.status}</p>}
        {update.canInstall && <div className="flex gap-2">
          <Button size="sm" onClick={() => postMessage({ action: "installUpdate" })}>
            {update.status.includes("You're up to date") ? "Force update" : "Install update"}
          </Button>
          {update.automatic && <Button variant="outline" size="sm" onClick={() => postMessage({ action: "ignoreUpdate" })}>Ignore this version</Button>}
        </div>}
      </div>
      <div className="flex justify-end items-center gap-2 pt-1">
        <span className="text-neutral-400 mr-auto">v{version}</span>
        <Button
          variant="outline"
          size="sm"
          className="min-w-[5rem]"
          onClick={() => closeDialog()}
        >
          Cancel
        </Button>
        <Button size="sm" className="min-w-[5rem]" onClick={handleSave}>
          Save
        </Button>
      </div>
    </div>
  );
}
