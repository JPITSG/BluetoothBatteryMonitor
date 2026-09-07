import { useEffect, useRef, useState } from "react";
import { onInit, getInit, reportHeight, type InitData } from "./lib/bridge";
import ConfigView from "./ConfigView";

export default function App() {
  const containerRef = useRef<HTMLDivElement>(null);
  const [initData, setInitData] = useState<InitData | null>(null);

  useEffect(() => {
    onInit((data) => setInitData(data));
    getInit();
  }, []);

  useEffect(() => {
    const el = containerRef.current;
    if (!el || !initData) return;

    let lastHeight = 0;
    const report = () => {
      // Measure content, not the viewport: the native dialog must be able to
      // grow and shrink. Allow for fractional pixels at laptop DPI scales.
      const height = Math.ceil(Math.max(el.scrollHeight, el.getBoundingClientRect().height)) + 2;
      if (height !== lastHeight) {
        lastHeight = height;
        reportHeight(height);
      }
    };

    requestAnimationFrame(report);
    const settlingReports = [
      window.setTimeout(report, 50),
      window.setTimeout(report, 250),
    ];

    const observer = new ResizeObserver(report);
    observer.observe(el);
    if (document.body) {
      observer.observe(document.body);
    }

    return () => {
      observer.disconnect();
      settlingReports.forEach((timerId) => window.clearTimeout(timerId));
    };
  }, [initData]);

  if (!initData) return null;

  return (
    <div ref={containerRef}>
      <ConfigView devices={initData.devices} version={initData.version} autoCheck={initData.autoCheck} />
    </div>
  );
}
