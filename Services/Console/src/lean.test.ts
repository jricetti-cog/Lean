import { describe, expect, it } from 'vitest';
import { chartSeries, orderRow, positionsFromOrders, seriesPoints } from './lean';

describe('lean result helpers', () => {
  it('reads [t,v], candle and {x,y} series values, sorted and de-duplicated', () => {
    expect(seriesPoints({ values: [[20, 2], [10, 1], [20, 3]] })).toEqual([{ time: 10, value: 1 }, { time: 20, value: 3 }]);
    expect(seriesPoints({ Values: [[10, 1, 5, 0, 4]] })).toEqual([{ time: 10, value: 4 }]);
    expect(seriesPoints({ values: [{ x: 5, y: 7 }] })).toEqual([{ time: 5, value: 7 }]);
  });

  it('lists non-empty chart series', () => {
    const chart = { name: 'Strategy Equity', series: { Equity: { name: 'Equity', values: [[1, 100000, 100010, 99990, 100005]] }, Empty: { values: [] } } };
    expect(chartSeries(chart).map((s) => s.name)).toEqual(['Equity']);
  });

  it('maps LEAN order enums and derives net positions from fills', () => {
    const orders = [
      { id: 1, symbol: { value: 'SPY' }, type: 0, status: 3, quantity: 10, price: 100 },
      { id: 2, symbol: { value: 'SPY' }, type: 1, status: 3, quantity: 10, price: 110 },
      { id: 3, symbol: { value: 'AAPL' }, type: 0, status: 3, quantity: 5, price: 50 },
      { id: 4, symbol: { value: 'AAPL' }, type: 0, status: 3, quantity: -5, price: 55 },
      { id: 5, symbol: { value: 'IBM' }, type: 0, status: 5, quantity: 1, price: 1 },
    ].map(orderRow);
    expect(orders[1]).toMatchObject({ type: 'Limit', status: 'Filled', symbol: 'SPY' });
    expect(positionsFromOrders(orders)).toEqual([{ symbol: 'SPY', quantity: 20, averagePrice: 105, lastPrice: 110 }]);
  });
});
