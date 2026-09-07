export interface DeviceEntry {
  name: string;
  isConfigured: boolean;
}

export interface InitData {
  devices: DeviceEntry[];
  version: string;
  autoCheck: boolean;
}

type InitCallback = (data: InitData) => void;

let initCallback: InitCallback | null = null;

declare global {
  interface Window {
    onInit: (data: InitData) => void;
    chrome?: {
      webview?: {
        addEventListener: (type: "message", cb: (event: MessageEvent<UpdateState>) => void) => void;
        removeEventListener: (type: "message", cb: (event: MessageEvent<UpdateState>) => void) => void;
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

export function postMessage(msg: Record<string, unknown>) {
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

export interface UpdateState { status: string; busy: boolean; canInstall: boolean; automatic: boolean; currentVersion?: string; remoteVersion?: string; }
