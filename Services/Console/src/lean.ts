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
