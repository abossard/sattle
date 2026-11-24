import { useEffect, useState } from "react";

type CapturedQueries = {
  componentId: string;
  windowStart: string;
  windowEnd: string;
  queries: { routes: string; dependencies: string };
};

export function useCapturedKusto(componentId?: string, start?: string, end?: string) {
  const [capture, setCapture] = useState<CapturedQueries>();
  useEffect(() => {
    if (!componentId || !start || !end) return;
    const abort = new AbortController();
    async function load() {
      try {
        const response = await fetch("/example-evidence/captured-kusto", { cache: "no-store", signal: abort.signal });
        if (!response.ok) throw new Error("Local capture unavailable");
        const result: CapturedQueries = await response.json();
        if (!result.componentId || !result.queries?.routes || !result.queries.dependencies) throw new Error("Missing scoped queries");
        if (!abort.signal.aborted) setCapture(result);
      } catch { if (!abort.signal.aborted) setCapture(undefined); }
    }
    void load();
    return () => abort.abort();
  }, [componentId, start, end]);

  // An older revision or another engine can show the same app with a different capture.
  if (capture && capture.componentId.toLowerCase() === componentId?.toLowerCase()
    && capture.windowStart === start && capture.windowEnd === end) return capture.queries;
}
