import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { eventsUrl, JobEventStream, parseSseEvent, type EventSourceLike, type SseEvent } from './api/events';
import { chartSeries, mergeChart, packetChart, packetLogLine, packetSummary } from './lean';

class FakeSource implements EventSourceLike {
  static all: FakeSource[] = [];
  readyState = 0;
  onopen: EventSourceLike['onopen'] = null;
  onerror: EventSourceLike['onerror'] = null;
  closed = false;
  listeners = new Map<string, (ev: MessageEvent) => void>();
  constructor(readonly url: string) {
    FakeSource.all.push(this);
  }
  addEventListener(type: string, listener: (ev: MessageEvent) => void) {
    this.listeners.set(type, listener);
  }
  close() {
    this.closed = true;
    this.readyState = 2;
  }
  open() {
    this.readyState = 1;
    this.onopen?.call(this, new Event('open'));
  }
  fail(readyState: number) {
    this.readyState = readyState;
    this.onerror?.call(this, new Event('error'));
  }
  emit(event: Partial<SseEvent> & { event: SseEvent['event'] }, id = '1:1') {
    const frame = { type: 'events.v1.JobProgress', jobId: 'j', attempt: 1, body: {}, ...event };
    this.listeners.get(event.event)?.({ data: JSON.stringify(frame), lastEventId: id } as MessageEvent);
  }
}

describe('JobEventStream', () => {
  beforeEach(() => {
    FakeSource.all = [];
    vi.useFakeTimers();
  });
  afterEach(() => vi.useRealTimers());

  const start = () => {
    const states: string[] = [];
    const events: SseEvent[] = [];
    const stream = new JobEventStream(eventsUrl('j 1'), (u) => new FakeSource(u), () => 1000);
    stream.onState((s) => states.push(s));
    stream.subscribe((e) => events.push(e));
    stream.start();
    return { stream, states, events, source: () => FakeSource.all.at(-1)! };
  };

  it('relays frames while live and remembers the Last-Event-ID', () => {
    const { stream, states, events, source } = start();
    expect(source().url).toBe('/api/v1/jobs/j%201/events');
    source().open();
    source().emit({ event: 'snapshot', type: 'gateway.v1.Snapshot' }, '1:0');
    source().emit({ event: 'result', type: 'packets.v1.BacktestResult', sequence: 4 }, '1:4');
    expect(events.map((e) => e.event)).toEqual(['snapshot', 'result']);
    expect(stream.lastEventId).toBe('1:4');
    expect(states).toEqual(['live']);
  });

  it('falls back to polling while EventSource reconnects by itself, then goes live again', () => {
    const { states, source } = start();
    source().open();
    source().fail(0);
    expect(states).toEqual(['live', 'polling']);
    expect(FakeSource.all).toHaveLength(1);
    source().open();
    expect(states).toEqual(['live', 'polling', 'live']);
  });

  it('retries a refused stream with a new EventSource after the backoff', () => {
    const { states, source } = start();
    const first = source();
    first.fail(2);
    expect(states).toEqual(['polling']);
    expect(first.closed).toBe(true);
    vi.advanceTimersByTime(999);
    expect(FakeSource.all).toHaveLength(1);
    vi.advanceTimersByTime(1);
    expect(FakeSource.all).toHaveLength(2);
    source().open();
    expect(states).toEqual(['polling', 'live']);
  });

  it('closes after the terminal status so EventSource does not reconnect', () => {
    const { states, source } = start();
    source().open();
    source().emit({ event: 'status', type: 'events.v1.JobCompleted' });
    expect(source().closed).toBe(true);
    expect(states).toEqual(['live', 'closed']);
    source().fail(2);
    vi.advanceTimersByTime(60_000);
    expect(FakeSource.all).toHaveLength(1);
  });

  it('ignores malformed frames', () => {
    expect(parseSseEvent('not json')).toBeUndefined();
    expect(parseSseEvent(undefined)).toBeUndefined();
    expect(parseSseEvent('{"event":"log","type":"packets.v1.Log","jobId":"j","attempt":1,"body":{}}')?.event).toBe('log');
  });
});

describe('live result packets', () => {
  const packet = {
    Progress: 0.42,
    Results: {
      RuntimeStatistics: { Equity: '$101,000.00' },
      Charts: { 'Strategy Equity': { Name: 'Strategy Equity', Series: { Equity: { Name: 'Equity', Values: [[30, 101000], [20, 100500]] } } } },
    },
  };

  it('appends streamed equity points to the REST chart', () => {
    const rest = { name: 'Strategy Equity', series: { Equity: { name: 'Equity', values: [[10, 100000], [20, 100400]] } } };
    const merged = mergeChart(rest, packetChart(packet, 'Strategy Equity'));
    expect(chartSeries(merged)[0].points).toEqual([
      { time: 10, value: 100000 },
      { time: 20, value: 100500 },
      { time: 30, value: 101000 },
    ]);
    expect(chartSeries(mergeChart(undefined, packetChart(packet, 'Strategy Equity')))[0].points).toHaveLength(2);
    expect(packetChart(packet, 'Drawdown')).toBeUndefined();
  });

  it('reads progress, runtime statistics and log lines from packets', () => {
    expect(packetSummary(packet)).toEqual({ progress: 0.42, runtimeStatistics: { Equity: '$101,000.00' }, newOrders: false });
    expect(packetSummary({ Results: { Orders: { 1: { Id: 1 } } } }).newOrders).toBe(true);
    expect(packetSummary({})).toEqual({ progress: undefined, runtimeStatistics: undefined, newOrders: false });
    const now = new Date('2026-11-22T00:00:00Z');
    expect(packetLogLine('packets.v1.Debug', { Message: 'hi' }, now)).toEqual({ time: now.toISOString(), type: 'Debug', message: 'hi', stackTrace: null });
    expect(packetLogLine('packets.v1.RuntimeError', { Message: 'boom', StackTrace: 'at x' }, now)?.stackTrace).toBe('at x');
    expect(packetLogLine('packets.v1.OrderEvent', { Message: 'x' }, now)).toBeUndefined();
  });
});
