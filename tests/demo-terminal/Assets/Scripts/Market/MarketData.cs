using System;
using System.Collections.Generic;
using UnityEngine;

namespace Northwind.Trading
{
    public class MarketData : MonoBehaviour
    {
        public static MarketData Instance { get; private set; }

        public string symbol = PriceModel.Symbol;
        public int maxCandles = 240;
        public int maPeriod = 10;
        public List<Candle> candles = new List<Candle>();
        public List<float> movingAverage = new List<float>();
        public double lastPrice;
        public double sessionOpen;
        public double lastTickTime;
        public OrderBook orderBook = new OrderBook();

        public event Action PriceChanged;
        public event Action BookChanged;

        public double Change => lastPrice - sessionOpen;
        public double ChangePercent => sessionOpen > 0 ? Change / sessionOpen * 100 : 0;

        void Awake()
        {
            Instance = this;
        }

        public void ApplyHistory(CandleHistory history)
        {
            candles.Clear();
            candles.AddRange(history.candles);
            Trim();
            if (candles.Count == 0) return;
            sessionOpen = candles[0].open;
            lastPrice = candles[candles.Count - 1].close;
            RecalculateAverage();
            PriceChanged?.Invoke();
        }

        public void ApplyTick(Tick tick)
        {
            lastPrice = tick.price;
            lastTickTime = tick.time;
            var last = candles.Count > 0 ? candles[candles.Count - 1] : null;
            if (last != null && last.time == tick.candle.time) candles[candles.Count - 1] = tick.candle;
            else if (last == null || tick.candle.time > last.time) candles.Add(tick.candle);
            Trim();
            if (sessionOpen <= 0) sessionOpen = tick.candle.open;
            RecalculateAverage();
            PriceChanged?.Invoke();
        }

        public void ApplyBook(OrderBook book)
        {
            orderBook = book;
            BookChanged?.Invoke();
        }

        void Trim()
        {
            if (candles.Count > maxCandles) candles.RemoveRange(0, candles.Count - maxCandles);
        }

        void RecalculateAverage()
        {
            movingAverage.Clear();
            double sum = 0;
            for (int i = 0; i < candles.Count; i++)
            {
                sum += candles[i].close;
                if (i >= maPeriod) sum -= candles[i - maPeriod].close;
                movingAverage.Add(i >= maPeriod - 1 ? (float)(sum / maPeriod) : float.NaN);
            }
        }
    }
}
