import { useEffect, useRef, useState } from "react";
import { onHostMessage, getInit, reportHeight, type InitData } from "./lib/bridge";
import ConfigView from "./ConfigView";

export default function App() {
  const containerRef = useRef<HTMLDivElement>(null);
  const [initData, setInitData] = useState<InitData | null>(null);

  useEffect(() => {
    const unsubscribe = onHostMessage((message) => {
      if (message.type === "init") setInitData(message);
      if (message.type === "devices") setInitData((current) => current && { ...current, ...message });
    });
    getInit();
    return unsubscribe;
  }, []);

  useEffect(() => {
    const el = containerRef.current;
    if (!el) return;
    let lastHeight = 0;
    let frame = 0;
    const scheduleReport = () => {
      if (frame) return;
      frame = requestAnimationFrame(() => {
        frame = 0;
        // Content, not viewport size; preserve fractional-DPI headroom without
        // feeding native window resizes back through a body observer.
        const height = Math.ceil(Math.max(el.scrollHeight, el.getBoundingClientRect().height)) + 2;
        if (height !== lastHeight) {
          lastHeight = height;
          reportHeight(height);
        }
      });
    };
    scheduleReport();
    const observer = new ResizeObserver(scheduleReport);
    observer.observe(el);
    return () => {
      observer.disconnect();
      cancelAnimationFrame(frame);
    };
  }, []);

  return (
    <div ref={containerRef}>
      {initData ? <ConfigView {...initData} /> : <p role="status" className="p-4 text-xs text-neutral-500">Loading configuration…</p>}
    </div>
  );
}
