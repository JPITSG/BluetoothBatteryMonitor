export interface DeviceEntry {
  name: string;
  isConfigured: boolean;
}

export interface BatteryReading {
  observedAt: string;
  percentage: number;
}

// A stretch when the device was not seen connected, oldest first.
export interface OfflinePeriod {
  from: string;
  // Null while the device is still offline.
  to?: string | null;
  // "unmonitored" when nothing was monitored at all: the PC was asleep or
  // off, or the app was closed.
  reason: "disconnected" | "unmonitored";
}

export interface DeviceStatus {
  name: string;
  online: boolean;
  batteryLevel: number | null;
  lastChargedAt?: string | null;
  // Chronological readings of the current discharge or charge.
  trend?: BatteryReading[] | null;
  // Offline periods overlapping the trend.
  offline?: OfflinePeriod[] | null;
  // When connection changes began to be recorded; absent if never.
  connectionHistoryStart?: string | null;
}

export interface DeviceState {
  devices: DeviceEntry[];
  loadingDevices: boolean;
  deviceError?: string;
}

export interface InitData extends DeviceState {
  version: string;
  autoCheck: boolean;
  deviceStatuses: DeviceStatus[];
}

export interface UpdateState {
  status: string;
  busy: boolean;
  installing: boolean;
  canInstall: boolean;
  automatic: boolean;
  // Whole percent of the update download received so far.
  downloadPercent?: number | null;
  currentVersion?: string;
  remoteVersion?: string;
}

export type HostMessage =
  | ({ type: "init" } & InitData)
  | ({ type: "devices" } & DeviceState)
  | { type: "deviceStatus"; deviceStatuses: DeviceStatus[] }
  | ({ type: "update" } & UpdateState);

declare global {
  interface Window {
    chrome?: {
      webview?: {
        addEventListener: (type: "message", cb: (event: MessageEvent<HostMessage>) => void) => void;
        removeEventListener: (type: "message", cb: (event: MessageEvent<HostMessage>) => void) => void;
        postMessage: (s: string) => void;
      };
    };
  }
}

export function onHostMessage(callback: (message: HostMessage) => void) {
  const receive = (event: MessageEvent<HostMessage>) => callback(event.data);
  window.chrome?.webview?.addEventListener("message", receive);
  return () => window.chrome?.webview?.removeEventListener("message", receive);
}

export function postMessage(msg: Record<string, unknown>) {
  window.chrome?.webview?.postMessage(JSON.stringify(msg));
}

export function getInit() {
  postMessage({ action: "getInit" });
}

export function saveDevices(devices: string[]) {
  postMessage({ action: "saveDevices", devices });
}

export function closeDialog() {
  postMessage({ action: "close" });
}

export function reportHeight(height: number) {
  postMessage({ action: "resize", height });
}
