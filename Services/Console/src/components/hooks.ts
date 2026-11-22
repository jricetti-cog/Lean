import { useCallback, useEffect, useRef, useState } from 'react';
import { eventsUrl, JobEventStream, type SseEvent, type StreamState } from '../api/events';

export interface Loadable<T> {
  data?: T;
  error?: Error;
  loading: boolean;
  reload: () => void;
  /** Replaces the data locally (live events) until the next load. */
  mutate: (update: (data?: T) => T | undefined) => void;
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
  const mutate = useCallback((update: (data?: T) => T | undefined) => setState((s) => ({ ...s, data: update(s.data) })), []);
  return { ...state, reload, mutate };
}

/**
 * Ticks when a job seen active reaches a terminal status, and twice more shortly after: the results service
 * may store the final packet after the terminal event, so the final statistics, charts and orders are refetched.
 */
export function useFinalRefresh(status?: string, terminal?: boolean): number {
  const [tick, setTick] = useState(0);
  const active = useRef(false);
  useEffect(() => {
    if (status && !terminal) active.current = true;
    if (!terminal || !active.current) return;
    setTick((t) => t + 1);
    const timers = [2000, 6000].map((ms) => setTimeout(() => setTick((t) => t + 1), ms));
    return () => timers.forEach(clearTimeout);
  }, [status, terminal]);
  return tick;
}

/** Subscribes to a job's live events while `enabled`; `state` is 'live' only while frames can arrive. */
export function useJobEvents(id: string, enabled: boolean): JobEvents {
  const [stream, setStream] = useState<JobEventStream>();
  const [state, setState] = useState<StreamState>(enabled ? 'connecting' : 'closed');
  useEffect(() => {
    if (!enabled || typeof EventSource === 'undefined') {
      setState('closed');
      return;
    }
    const s = new JobEventStream(eventsUrl(id));
    const off = s.onState(setState);
    setStream(s);
    setState('connecting');
    s.start();
    return () => {
      off();
      s.stop();
      setStream(undefined);
    };
  }, [id, enabled]);
  const subscribe = useCallback((fn: (e: SseEvent) => void) => stream?.subscribe(fn) ?? (() => {}), [stream]);
  return { state, subscribe };
}

export interface JobEvents {
  state: StreamState;
  subscribe: (fn: (e: SseEvent) => void) => () => void;
}

/** Runs `fn` for the stream's events of the given names. */
export function useJobEvent(events: JobEvents | undefined, names: string[], fn: (e: SseEvent) => void) {
  const fnRef = useRef(fn);
  fnRef.current = fn;
  const key = names.join(',');
  useEffect(() => {
    if (!events) return;
    const wanted = new Set(key.split(','));
    return events.subscribe((e) => wanted.has(e.event) && fnRef.current(e));
  }, [events?.subscribe, key]);
}
