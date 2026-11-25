import { useEffect, useRef } from 'react'
import { CandlestickSeries, createChart, type IChartApi, type ISeriesApi, type UTCTimestamp } from 'lightweight-charts'
import type { Candle } from './api/contract'
import { formatPrice, formatTime } from './format'

export function CandleChart({ candles }: { candles: Candle[] }) {
  const node = useRef<HTMLDivElement>(null)
  const api = useRef<{ chart: IChartApi; bars: ISeriesApi<'Candlestick'> } | null>(null)
  useEffect(() => {
    if (!node.current) return
    const chart = createChart(node.current, { autoSize: true, height: 300,
      layout: { background: { color: '#101d32' }, textColor: '#c5d1e6', fontSize: 15, attributionLogo: true },
      localization: { priceFormatter: formatPrice, timeFormatter: (time: number) => formatTime(time * 1000) },
      grid: { vertLines: { color: '#25364f' }, horzLines: { color: '#25364f' } },
      timeScale: { timeVisible: true, secondsVisible: false, minBarSpacing: 12, tickMarkFormatter: (time: number) => formatTime(time * 1000).slice(0, 5) },
    })
    const bars = chart.addSeries(CandlestickSeries, { upColor: '#ff8a95', downColor: '#6bb4ff', borderVisible: false, wickUpColor: '#ff8a95', wickDownColor: '#6bb4ff', priceFormat: { type: 'price', precision: 0, minMove: 10 } })
    api.current = { chart, bars }
    return () => { chart.remove(); api.current = null }
  }, [])
  useEffect(() => {
    if (!api.current) return
    api.current.bars.setData(candles.map(({ startMs, open, high, low, close }) => ({ time: (startMs / 1000) as UTCTimestamp, open, high, low, close })))
    api.current.chart.timeScale().fitContent()
  }, [candles])
  return <div className="chart" ref={node} aria-label="1분봉 캔들 차트" />
}
