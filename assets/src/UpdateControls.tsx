import { useEffect, useLayoutEffect, useState } from "react";
import { createPortal } from "react-dom";
import ConfigAlert from "./components/ConfigAlert";
import { Button } from "./components/ui/button";
import { Checkbox } from "./components/ui/checkbox";
import { Label } from "./components/ui/label";
import { onHostMessage, postMessage, type UpdateState } from "./lib/bridge";

interface UpdateControlsProps {
  version: string;
  onSave: () => void;
  onRequestClose: () => void;
  closePrompt: boolean;
  onAlertChange: (open: boolean) => void;
}

// Download progress only rerenders the footer and its alert, not every device row.
export default function UpdateControls({ version, onSave, onRequestClose, closePrompt, onAlertChange }: UpdateControlsProps) {
  const [update, setUpdate] = useState<UpdateState>({ status: "", busy: false, installing: false, canInstall: false, automatic: false });
  const [stopping, setStopping] = useState(false);
  const [reopenSettings, setReopenSettings] = useState(false);

  useEffect(() => {
    const unsubscribe = onHostMessage((message) => {
      if (message.type !== "update") return;
      setUpdate(message);
      if (!message.busy) setStopping(false);
    });
    postMessage({ action: "getUpdateState" });
    return unsubscribe;
  }, []);

  const showAlert = !update.busy && !!update.status && !update.status.startsWith("Update check cancelled");
  useLayoutEffect(() => { onAlertChange(showAlert); }, [showAlert, onAlertChange]);
  useEffect(() => {
    if (showAlert) setReopenSettings(false);
  }, [showAlert]);

  const dismissUpdate = (action = "dismissUpdate") => {
    setReopenSettings(false);
    setUpdate((current) => ({ ...current, status: "", canInstall: false }));
    postMessage({ action });
  };
  const installUpdate = () => {
    setUpdate((current) => ({ ...current, busy: true, installing: true, status: "Starting update…" }));
    postMessage({ action: "installUpdate", reopenSettings });
    setReopenSettings(false);
  };
  const sameVersion = update.canInstall && update.currentVersion === update.remoteVersion;
  const alertTitle = update.status.startsWith("Update failed") ? "Update failed" : update.canInstall
    ? sameVersion ? "You're up to date" : "Update available"
    : update.status.startsWith("Successfully updated") ? "Update complete"
      : update.status.startsWith("Update failed") ? "Update failed" : "No update available";
  const alertMessage = update.status.startsWith("Update failed") ? update.status : update.canInstall
    ? sameVersion ? "The remote build matches your current version. You can force a reinstall if needed." : "A newer version is ready to install."
    : update.remoteVersion ? "The remote build is older than your current version." : update.status;

  return (
    <>
      <div className="flex flex-wrap items-center justify-between gap-3 pt-1">
        <span className="select-none whitespace-nowrap text-[11px] leading-none tabular-nums text-neutral-400" title="Application version">v{version}</span>
        <div className="ml-auto flex items-center gap-2">
          <Button variant={update.busy ? "destructive" : "outline"} size="sm" className="min-w-[5rem]" disabled={stopping || update.installing}
            aria-label={update.installing ? "Starting update" : update.busy ? "Stop update check and download" : undefined}
            title={update.installing ? "Starting update…" : update.busy ? `${update.status} — Click to stop update check and download` : undefined}
            onClick={() => {
              if (update.busy) {
                setStopping(true);
                postMessage({ action: "cancelUpdate" });
              } else {
                setUpdate((current) => ({ ...current, busy: true, canInstall: false, downloadPercent: null, status: "Checking for updates…" }));
                postMessage({ action: "checkUpdate" });
              }
            }}>
            <span className="tabular-nums">{update.installing ? "Starting..." : stopping ? "Stopping..." : update.busy
              ? update.downloadPercent != null ? `Checking (${update.downloadPercent}%)...` : "Checking..."
              : "Update"}</span>
          </Button>
          <Button variant="outline" size="sm" className="min-w-[5rem]" disabled={update.installing} onClick={onRequestClose}>Cancel</Button>
          <Button size="sm" className="min-w-[5rem]" disabled={update.installing} onClick={onSave}>Save</Button>
        </div>
      </div>

      {/* Keep the alert outside the inert form; pending edits take precedence. */}
      {showAlert && !closePrompt && createPortal(
        <ConfigAlert id="update-alert" title={alertTitle} message={alertMessage} onEscape={() => dismissUpdate()}>
          {update.currentVersion && update.remoteVersion && (
            <dl className="grid grid-cols-[1fr_auto] gap-x-4 gap-y-1 rounded-md border border-neutral-200 bg-neutral-50 px-3 py-2 text-xs">
              <dt className="text-neutral-500">Current version</dt><dd className="font-medium tabular-nums text-neutral-900">{update.currentVersion}</dd>
              <dt className="text-neutral-500">Remote version</dt><dd className="font-medium tabular-nums text-neutral-900">{update.remoteVersion}</dd>
            </dl>
          )}
          {update.canInstall && (
            <Label className="flex cursor-pointer items-center gap-2 leading-snug">
              <Checkbox checked={reopenSettings} onChange={(event) => setReopenSettings(event.target.checked)} />
              Reopen settings after update
            </Label>
          )}
          <div className="flex justify-end gap-2">
            {update.canInstall && update.automatic && <Button variant="outline" size="sm" onClick={() => dismissUpdate("ignoreUpdate")}>Ignore this version</Button>}
            {update.canInstall && <Button variant="outline" size="sm" autoFocus onClick={() => dismissUpdate()}>Cancel</Button>}
            <Button size="sm" autoFocus={!update.canInstall} onClick={() => update.canInstall ? installUpdate() : dismissUpdate()}>
              {update.canInstall ? sameVersion ? "Force update" : "Update" : "OK"}
            </Button>
          </div>
        </ConfigAlert>, document.body)}
    </>
  );
}
