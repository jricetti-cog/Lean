import { useCallback, useEffect, useRef, useState } from 'react';

export interface Loadable<T> {
  data?: T;
  error?: Error;
  loading: boolean;
  reload: () => void;
}

/** Runs `load` on mount / dependency change and every `intervalMs` while `poll(data)` is true. */
export function usePolling<T>(load: () => Promise<T>, deps: unknown[], intervalMs = 3000, poll: (data?: T) => boolean = () => true): Loadable<T> {
  const [state, setState] = useState<{ data?: T; error?: Error; loading: boolean }>({ loading: true });
  const [tick, setTick] = useState(0);
  const loadRef = useRef(load);
  loadRef.current = load;
  const pollRef = useRef(poll);
  pollRef.current = poll;

  useEffect(() => {
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const run = async () => {
      let data: T | undefined;
      try {
        data = await loadRef.current();
        if (!cancelled) setState({ data, loading: false });
      } catch (e) {
        if (!cancelled) setState((s) => ({ ...s, error: e as Error, loading: false }));
      }
      if (!cancelled && intervalMs > 0 && pollRef.current(data)) timer = setTimeout(run, intervalMs);
    };
    run();
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, tick, intervalMs]);

  const reload = useCallback(() => setTick((t) => t + 1), []);
  return { ...state, reload };
}
