import { useEffect, useRef, useState } from "react";
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
  const [stopping, setStopping] = useState(false);
  const alertRef = useRef<HTMLDialogElement>(null);
  const updateButtonRef = useRef<HTMLButtonElement>(null);
  const [selected, setSelected] = useState<Set<string>>(
    () => new Set(devices.filter((d) => d.isConfigured).map((d) => d.name))
  );

  useEffect(() => {
    const receive = (event: MessageEvent<UpdateState>) => {
      setUpdate(event.data);
      if (!event.data.busy) setStopping(false);
    };
    window.chrome?.webview?.addEventListener("message", receive);
    postMessage({ action: "getUpdateState" });
    return () => window.chrome?.webview?.removeEventListener("message", receive);
  }, []);

  const showAlert = !update.busy && !!update.status && !update.status.startsWith("Update check cancelled");
  useEffect(() => {
    const dialog = alertRef.current;
    if (showAlert && dialog && !dialog.open) dialog.showModal();
    else if (!showAlert && dialog?.open) {
      dialog.close();
      updateButtonRef.current?.focus();
    }
  }, [showAlert]);

  const dismissUpdate = () => postMessage({ action: "dismissUpdate" });
  const sameVersion = update.canInstall && update.currentVersion === update.remoteVersion;
  const alertTitle = update.status.startsWith("Update failed") ? "Update failed" : update.canInstall
    ? sameVersion ? "You're up to date" : "Update available"
    : update.status.startsWith("Successfully updated") ? "Update complete"
      : update.status.startsWith("Update failed") ? "Update failed" : "No update available";
  const alertMessage = update.status.startsWith("Update failed") ? update.status : update.canInstall
    ? sameVersion ? "The remote build matches your current version. You can force a reinstall if needed." : "A newer version is ready to install."
    : update.remoteVersion ? "The remote build is older than your current version." : update.status;

  const toggle = (name: string) => {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(name)) next.delete(name);
      else next.add(name);
      return next;
    });
  };

  return (
    <div className="p-5 flex flex-col gap-4 max-w-md mx-auto text-xs">
      <section aria-labelledby="monitor-devices" className="space-y-2">
        <h2 id="monitor-devices" className="text-xs font-semibold text-neutral-800">Monitor devices</h2>
        <div className="rounded-md border border-neutral-200 p-1">
          {devices.length === 0 ? (
            <p className="text-neutral-500 px-3 py-4 text-center">No paired Bluetooth devices found.</p>
          ) : devices.map((device) => (
            <label key={device.name} className="flex items-center gap-2.5 px-2 py-2 rounded hover:bg-neutral-50 cursor-pointer select-none">
              <input type="checkbox" checked={selected.has(device.name)} onChange={() => toggle(device.name)} className="h-3.5 w-3.5 shrink-0 rounded border-neutral-300 accent-neutral-900 cursor-pointer" />
              <span className="min-w-0 break-words">{device.name}</span>
            </label>
          ))}
        </div>
      </section>

      <label className="flex items-start gap-2 cursor-pointer">
        <input type="checkbox" checked={automatic} className="mt-0.5 h-3.5 w-3.5 shrink-0 rounded border-neutral-300 accent-neutral-900" onChange={(event) => {
          setAutomatic(event.target.checked);
          postMessage({ action: "autoUpdate", enabled: event.target.checked });
        }} />
        <span className="space-y-0.5">
          <span className="block font-medium">Automatically check for updates</span>
          <span className="block text-neutral-500 text-[11px] leading-snug">Checks at startup, whenever this dialog opens, and every 60 minutes. Prompts only when a newer version is available.</span>
        </span>
      </label>

      <div className="flex items-center justify-between gap-3 pt-3">
        <span className="select-none whitespace-nowrap text-[11px] leading-none tabular-nums text-neutral-400" title="Application version">v{version}</span>
        <div className="flex items-center gap-2 min-w-0">
          <Button ref={updateButtonRef} variant={update.busy ? "destructive" : "outline"} size="sm" className="min-w-[5rem] max-w-[10rem]" disabled={stopping}
            aria-label={update.busy ? "Stop update check and download" : undefined}
            title={update.busy ? `${update.status} — Click to stop update check and download` : undefined}
            onClick={() => {
              if (update.busy) setStopping(true);
              postMessage({ action: update.busy ? "cancelUpdate" : "checkUpdate" });
            }}>
            <span className="truncate">{stopping ? "Stopping..." : update.busy ? "Checking..." : "Update"}</span>
          </Button>
          <Button variant="outline" size="sm" className="min-w-[5rem]" onClick={closeDialog}>Cancel</Button>
          <Button size="sm" className="min-w-[5rem]" onClick={() => saveDevices(Array.from(selected))}>Save</Button>
        </div>
      </div>

      <dialog ref={alertRef} aria-labelledby="update-alert-title" aria-describedby="update-alert-message"
        className="w-[calc(100%-2rem)] max-w-sm max-h-[calc(100%-2rem)] space-y-3 rounded-lg border border-neutral-200 bg-white p-4 shadow-xl backdrop:bg-black/35"
        onCancel={(event) => { event.preventDefault(); dismissUpdate(); }}>
        <div className="space-y-1">
          <h2 id="update-alert-title" className="text-sm font-semibold">{alertTitle}</h2>
          <p id="update-alert-message" className="text-xs leading-relaxed text-neutral-600">{alertMessage}</p>
        </div>
        {update.currentVersion && update.remoteVersion && (
          <dl className="grid grid-cols-[1fr_auto] gap-x-4 gap-y-1 rounded-md border border-neutral-200 bg-neutral-50 px-3 py-2 text-xs">
            <dt className="text-neutral-500">Current version</dt><dd className="font-medium tabular-nums text-neutral-900">{update.currentVersion}</dd>
            <dt className="text-neutral-500">Remote version</dt><dd className="font-medium tabular-nums text-neutral-900">{update.remoteVersion}</dd>
          </dl>
        )}
        <div className="flex justify-end gap-2">
          {update.canInstall && update.automatic && <Button variant="outline" size="sm" onClick={() => postMessage({ action: "ignoreUpdate" })}>Ignore this version</Button>}
          {update.canInstall && <Button variant="outline" size="sm" autoFocus onClick={dismissUpdate}>Cancel</Button>}
          <Button size="sm" autoFocus={!update.canInstall} onClick={() => update.canInstall ? postMessage({ action: "installUpdate" }) : dismissUpdate()}>
            {update.canInstall ? sameVersion ? "Force update" : "Update" : "OK"}
          </Button>
        </div>
      </dialog>
    </div>
  );
}
