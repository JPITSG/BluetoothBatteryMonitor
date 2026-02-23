export interface DeviceEntry {
  name: string;
  isConfigured: boolean;
}

export interface InitData {
  devices: DeviceEntry[];
}

type InitCallback = (data: InitData) => void;

let initCallback: InitCallback | null = null;

declare global {
  interface Window {
    onInit: (data: InitData) => void;
    chrome?: {
      webview?: {
        postMessage: (s: string) => void;
      };
    };
  }
}

window.onInit = (data: InitData) => {
  initCallback?.(data);
};

export function onInit(cb: InitCallback) {
  initCallback = cb;
}

function postMessage(msg: Record<string, unknown>) {
  try {
    window.chrome?.webview?.postMessage(JSON.stringify(msg));
  } catch {
    console.log("postMessage (no WebView2):", msg);
  }
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
