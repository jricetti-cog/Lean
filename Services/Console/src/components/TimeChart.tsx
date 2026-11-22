import { useEffect, useRef } from 'react';
import { AreaSeries, ColorType, createChart, LineSeries, type IChartApi, type ISeriesApi, type SeriesType, type UTCTimestamp } from 'lightweight-charts';
import type { Point } from '../lean';

const PALETTE = ['#2563eb', '#0ea5e9', '#1e3a8a', '#38bdf8', '#6366f1'];

/**
 * Lightweight Charts time series (area for the first series, lines for the rest). The chart is created once per
 * set of series names and live updates only replace the data, so streaming points do not rebuild it.
 */
export function TimeChart({ series, height = 260, negative = false }: { series: { name: string; points: Point[] }[]; height?: number; negative?: boolean }) {
  const host = useRef<HTMLDivElement>(null);
  const chart = useRef<{ api: IChartApi; lines: ISeriesApi<SeriesType>[] }>(undefined);
  const names = series.map((s) => s.name).join('\u0000');
  useEffect(() => {
    const api = createChart(host.current!, {
      height,
      autoSize: true,
      layout: { background: { type: ColorType.Solid, color: 'transparent' }, textColor: '#334155', fontFamily: 'inherit' },
      grid: { vertLines: { color: '#e2e8f0' }, horzLines: { color: '#e2e8f0' } },
      rightPriceScale: { borderColor: '#cbd5e1' },
      timeScale: { borderColor: '#cbd5e1', timeVisible: true },
    });
    const lines = names.split('\u0000').map((name, i) =>
      i === 0
        ? api.addSeries(AreaSeries, {
            lineColor: negative ? '#1d4ed8' : PALETTE[0],
            topColor: negative ? 'rgba(29,78,216,0.05)' : 'rgba(37,99,235,0.35)',
            bottomColor: negative ? 'rgba(29,78,216,0.35)' : 'rgba(37,99,235,0.02)',
            invertFilledArea: negative,
            lineWidth: 2,
            title: name,
          })
        : api.addSeries(LineSeries, { color: PALETTE[i % PALETTE.length], lineWidth: 1, title: name }),
    );
    chart.current = { api, lines };
    return () => {
      chart.current = undefined;
      api.remove();
    };
  }, [names, height, negative]);
  useEffect(() => {
    if (!chart.current) return;
    series.forEach((s, i) => chart.current!.lines[i]?.setData(s.points.map((p) => ({ time: p.time as UTCTimestamp, value: p.value }))));
    chart.current.api.timeScale().fitContent();
  }, [series, names, height, negative]);
  return <div ref={host} className="chart" style={{ height }} data-testid="chart" data-points={series[0]?.points.length ?? 0} />;
}
