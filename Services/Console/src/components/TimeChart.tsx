import { useEffect, useRef } from 'react';
import { AreaSeries, ColorType, createChart, LineSeries, type UTCTimestamp } from 'lightweight-charts';
import type { Point } from '../lean';

const PALETTE = ['#2563eb', '#0ea5e9', '#1e3a8a', '#38bdf8', '#6366f1'];

/** Lightweight Charts time series (area for the first series, lines for the rest). */
export function TimeChart({ series, height = 260, negative = false }: { series: { name: string; points: Point[] }[]; height?: number; negative?: boolean }) {
  const host = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const chart = createChart(host.current!, {
      height,
      autoSize: true,
      layout: { background: { type: ColorType.Solid, color: 'transparent' }, textColor: '#334155', fontFamily: 'inherit' },
      grid: { vertLines: { color: '#e2e8f0' }, horzLines: { color: '#e2e8f0' } },
      rightPriceScale: { borderColor: '#cbd5e1' },
      timeScale: { borderColor: '#cbd5e1', timeVisible: true },
    });
    series.forEach((s, i) => {
      const data = s.points.map((p) => ({ time: p.time as UTCTimestamp, value: p.value }));
      const line =
        i === 0
          ? chart.addSeries(AreaSeries, {
              lineColor: negative ? '#1d4ed8' : PALETTE[0],
              topColor: negative ? 'rgba(29,78,216,0.05)' : 'rgba(37,99,235,0.35)',
              bottomColor: negative ? 'rgba(29,78,216,0.35)' : 'rgba(37,99,235,0.02)',
              invertFilledArea: negative,
              lineWidth: 2,
              title: s.name,
            })
          : chart.addSeries(LineSeries, { color: PALETTE[i % PALETTE.length], lineWidth: 1, title: s.name });
      line.setData(data);
    });
    chart.timeScale().fitContent();
    return () => chart.remove();
  }, [series, height, negative]);
  return <div ref={host} className="chart" style={{ height }} />;
}
