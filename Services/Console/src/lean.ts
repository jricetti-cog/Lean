// Helpers for LEAN result JSON (charts, orders) as served by the results service: camelCase, but keys are read
// case-insensitively so older PascalCase packets render too.
import type { LeanObject } from './api/client';

export function field<T = unknown>(obj: unknown, ...names: string[]): T | undefined {
  if (!obj || typeof obj !== 'object') return undefined;
  const record = obj as Record<string, unknown>;
  for (const name of names) {
    if (name in record) return record[name] as T;
    const key = Object.keys(record).find((k) => k.toLowerCase() === name.toLowerCase());
    if (key) return record[key] as T;
  }
  return undefined;
}

export interface Point {
  time: number;
  value: number;
}

/** LEAN series values are `[t, v]`, candles `[t, o, h, l, c]` (close is used) or legacy `{x, y}`; t is unix seconds. */
export function seriesPoints(series: unknown): Point[] {
  const values = field<unknown[]>(series, 'values') ?? [];
  const byTime = new Map<number, number>();
  for (const v of values) {
    let t: number | undefined;
    let y: number | undefined;
    if (Array.isArray(v)) {
      t = Number(v[0]);
      y = Number(v.length >= 5 ? v[4] : v[1]);
    } else if (v && typeof v === 'object') {
      t = Number(field(v, 'x', 'time'));
      y = Number(field(v, 'y', 'value', 'close', 'c'));
    }
    if (t !== undefined && y !== undefined && Number.isFinite(t) && Number.isFinite(y)) byTime.set(t, y);
  }
  return [...byTime.entries()].sort((a, b) => a[0] - b[0]).map(([time, value]) => ({ time, value }));
}

export function chartSeries(chart: unknown): { name: string; points: Point[] }[] {
  const series = field<Record<string, unknown>>(chart, 'series') ?? {};
  return Object.entries(series)
    .map(([name, s]) => ({ name: field<string>(s, 'name') ?? name, points: seriesPoints(s) }))
    .filter((s) => s.points.length > 0);
}

/**
 * Appends the points of a partial result packet's chart (LEAN sends only the new points, PascalCase) to the chart
 * loaded over REST; the result is camelCase like the results service and `seriesPoints` drops duplicate times.
 */
export function mergeChart(base: unknown, delta: unknown): LeanObject | undefined {
  if (!delta || typeof delta !== 'object') return base as LeanObject | undefined;
  const baseSeries = field<Record<string, unknown>>(base, 'series') ?? {};
  const series: Record<string, unknown> = { ...baseSeries };
  for (const [name, s] of Object.entries(field<Record<string, unknown>>(delta, 'series') ?? {})) {
    const key = Object.keys(baseSeries).find((k) => k.toLowerCase() === name.toLowerCase()) ?? name;
    const old = baseSeries[key];
    series[key] = {
      ...(old && typeof old === 'object' ? (old as LeanObject) : {}),
      name: field<string>(old, 'name') ?? field<string>(s, 'name') ?? name,
      values: [...(field<unknown[]>(old, 'values') ?? []), ...(field<unknown[]>(s, 'values') ?? [])],
    };
  }
  return { ...((base as LeanObject) ?? {}), name: field<string>(base, 'name') ?? field<string>(delta, 'name'), series };
}

/** The chart named `chart` inside an inline `BacktestResult`/`LiveResult` packet body (`Results.Charts`). */
export function packetChart(packet: unknown, chart: string): unknown {
  const charts = field<Record<string, unknown>>(field(packet, 'results', 'oResults'), 'charts');
  return charts ? field(charts, chart) : undefined;
}

/** `Progress`, `RuntimeStatistics` and whether an inline result packet carries new `Orders`. */
export function packetSummary(packet: unknown): { progress?: number; runtimeStatistics?: Record<string, string>; newOrders: boolean } {
  const results = field(packet, 'results', 'oResults');
  const progress = Number(field(packet, 'progress'));
  const orders = field<Record<string, unknown>>(results, 'orders');
  return {
    progress: Number.isFinite(progress) && field(packet, 'progress') !== undefined ? progress : undefined,
    runtimeStatistics: field<Record<string, string>>(results, 'runtimeStatistics'),
    newOrders: !!orders && Object.keys(orders).length > 0,
  };
}

const LOG_TYPES = ['Log', 'Debug', 'SystemDebug', 'RuntimeError', 'HandledError'] as const;

/** A `log`/`error` SSE event (`packets.v1.Log`, `Debug`, `RuntimeError`, ...) as a `/logs` line. */
export function packetLogLine(type: string, packet: unknown, now = new Date()) {
  const kind = type.slice(type.lastIndexOf('.') + 1) as (typeof LOG_TYPES)[number];
  const message = field<string>(packet, 'message');
  if (!LOG_TYPES.includes(kind) || message === undefined) return undefined;
  return { time: now.toISOString(), type: kind, message: String(message), stackTrace: field<string>(packet, 'stackTrace') ?? null };
}

const ORDER_TYPES = ['Market', 'Limit', 'StopMarket', 'StopLimit', 'MarketOnOpen', 'MarketOnClose', 'OptionExercise', 'LimitIfTouched', 'ComboMarket', 'ComboLimit', 'ComboLegLimit', 'TrailingStop'];
const ORDER_STATUS: Record<number, string> = { 0: 'New', 1: 'Submitted', 2: 'PartiallyFilled', 3: 'Filled', 5: 'Canceled', 6: 'None', 7: 'Invalid', 8: 'CancelPending', 9: 'UpdateSubmitted' };

export interface OrderRow {
  id: number;
  time?: string;
  symbol: string;
  type: string;
  status: string;
  quantity: number;
  price: number;
  value: number;
  tag?: string;
}

export function symbolOf(value: unknown): string {
  if (typeof value === 'string') return value;
  return String(field(value, 'value', 'permtick', 'id') ?? '?');
}

export function orderRow(o: LeanObject): OrderRow {
  const type = field<number | string>(o, 'type');
  const status = field<number | string>(o, 'status');
  const quantity = Number(field(o, 'quantity') ?? 0);
  const price = Number(field(o, 'price') ?? 0);
  return {
    id: Number(field(o, 'id') ?? 0),
    time: field<string>(o, 'lastFillTime') ?? field<string>(o, 'time'),
    symbol: symbolOf(field(o, 'symbol')),
    type: typeof type === 'number' ? ORDER_TYPES[type] ?? String(type) : String(type ?? ''),
    status: typeof status === 'number' ? ORDER_STATUS[status] ?? String(status) : String(status ?? ''),
    quantity,
    price,
    value: Number(field(o, 'value') ?? quantity * price),
    tag: field<string>(o, 'tag') || undefined,
  };
}

export interface Position {
  symbol: string;
  quantity: number;
  averagePrice: number;
  lastPrice: number;
}

/** Net positions from filled orders (the public API exposes orders, not a holdings endpoint). */
export function positionsFromOrders(orders: OrderRow[]): Position[] {
  const map = new Map<string, Position>();
  for (const o of [...orders].sort((a, b) => a.id - b.id)) {
    if (o.status !== 'Filled' && o.status !== 'PartiallyFilled') continue;
    const p = map.get(o.symbol) ?? { symbol: o.symbol, quantity: 0, averagePrice: 0, lastPrice: 0 };
    const next = p.quantity + o.quantity;
    if (next === 0) p.averagePrice = 0;
    else if (Math.sign(p.quantity) !== Math.sign(next) || p.quantity === 0) p.averagePrice = o.price;
    else if (Math.abs(next) > Math.abs(p.quantity)) p.averagePrice = (p.averagePrice * p.quantity + o.price * o.quantity) / next;
    p.quantity = next;
    p.lastPrice = o.price;
    map.set(o.symbol, p);
  }
  return [...map.values()].filter((p) => p.quantity !== 0);
}
