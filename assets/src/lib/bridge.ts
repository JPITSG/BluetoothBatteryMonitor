export interface DeviceEntry {
  name: string;
  isConfigured: boolean;
}

export interface DeviceStatus {
  name: string;
  online: boolean;
  batteryLevel: number | null;
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
