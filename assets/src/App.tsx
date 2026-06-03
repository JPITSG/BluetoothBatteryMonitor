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

    const getContentHeight = () =>
      Math.max(
        el.scrollHeight,
        document.documentElement.scrollHeight,
        document.body?.scrollHeight ?? 0
      );

    const report = () => {
      reportHeight(Math.ceil(getContentHeight()));
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
      <ConfigView devices={initData.devices} />
    </div>
  );
}
