// Live job events over Server-Sent Events (ADR 0002): GET /api/v1/jobs/{id}/events. EventSource reconnects by itself
// and sends Last-Event-ID; while it is (re)connecting or after the gateway refused the stream, pages poll the REST
// endpoints instead, and a fresh stream is retried with backoff.
import type { Schemas } from './client';

export type SseEvent = Schemas['SseEvent'];
export type SseEventName = SseEvent['event'];
export type StreamState = 'connecting' | 'live' | 'polling' | 'closed';

export const EVENT_NAMES: SseEventName[] = ['snapshot', 'status', 'progress', 'result', 'log', 'order', 'error'];
export const TERMINAL_TYPES = new Set(['events.v1.JobCompleted', 'events.v1.JobFailed', 'events.v1.JobCancelled']);

export interface EventSourceLike {
  readonly readyState: number;
  onopen: ((this: EventSourceLike, ev: Event) => unknown) | null;
  onerror: ((this: EventSourceLike, ev: Event) => unknown) | null;
  addEventListener(type: string, listener: (ev: MessageEvent) => void): void;
  close(): void;
}

export type EventSourceFactory = (url: string) => EventSourceLike;

export const eventsUrl = (id: string) => `/api/v1/jobs/${encodeURIComponent(id)}/events`;

export function parseSseEvent(data: unknown): SseEvent | undefined {
  if (typeof data !== 'string') return undefined;
  try {
    const parsed = JSON.parse(data) as SseEvent;
    return parsed && typeof parsed === 'object' && typeof parsed.event === 'string' ? parsed : undefined;
  } catch {
    return undefined;
  }
}

/** One job's event stream: fan-out to subscribers, state for the polling fallback, retry with backoff. */
export class JobEventStream {
  private source?: EventSourceLike;
  private listeners = new Set<(e: SseEvent) => void>();
  private stateListeners = new Set<(s: StreamState) => void>();
  private retryTimer?: ReturnType<typeof setTimeout>;
  private retries = 0;
  private stopped = false;
  state: StreamState = 'connecting';
  lastEventId?: string;

  constructor(
    private readonly url: string,
    private readonly factory: EventSourceFactory = (u) => new EventSource(u) as unknown as EventSourceLike,
    private readonly retryMs = (attempt: number) => Math.min(30_000, 2_000 * 2 ** attempt),
  ) {}

  start() {
    this.stopped = false;
    this.open();
    return this;
  }

  stop(state: StreamState = 'closed') {
    this.stopped = true;
    clearTimeout(this.retryTimer);
    this.source?.close();
    this.source = undefined;
    this.setState(state);
  }

  subscribe(fn: (e: SseEvent) => void) {
    this.listeners.add(fn);
    return () => void this.listeners.delete(fn);
  }

  onState(fn: (s: StreamState) => void) {
    this.stateListeners.add(fn);
    return () => void this.stateListeners.delete(fn);
  }

  private open() {
    const source = this.factory(this.url);
    this.source = source;
    source.onopen = () => {
      this.retries = 0;
      this.setState('live');
    };
    source.onerror = () => {
      if (this.source !== source || this.stopped) return;
      // CONNECTING (0): the browser retries with Last-Event-ID; CLOSED (2): it gave up (e.g. 401/404/503).
      this.setState('polling');
      if (source.readyState === 2) {
        source.close();
        this.retryTimer = setTimeout(() => !this.stopped && this.open(), this.retryMs(this.retries++));
      }
    };
    for (const name of EVENT_NAMES) {
      source.addEventListener(name, (ev) => {
        const event = parseSseEvent(ev.data);
        if (!event) return;
        if (ev.lastEventId) this.lastEventId = ev.lastEventId;
        if (this.state !== 'live') this.setState('live');
        this.listeners.forEach((l) => l(event));
        // The gateway ends the stream after the terminal status: stop EventSource from reconnecting.
        if (name === 'status' && TERMINAL_TYPES.has(event.type)) this.stop();
      });
    }
  }

  private setState(state: StreamState) {
    if (this.state === state) return;
    this.state = state;
    this.stateListeners.forEach((l) => l(state));
  }
}
