using System;
using System.Collections.Generic;

namespace Northwind.Trading
{
    [Serializable]
    public class Candle
    {
        public long time;
        public double open;
        public double high;
        public double low;
        public double close;
        public double volume;
    }

    [Serializable]
    public class BookLevel
    {
        public double price;
        public double size;
    }

    [Serializable]
    public class OrderBook
    {
        public string symbol;
        public double time;
        public List<BookLevel> bids = new List<BookLevel>();
        public List<BookLevel> asks = new List<BookLevel>();
    }

    [Serializable]
    public class Tick
    {
        public string symbol;
        public double time;
        public double price;
        public long step;
        public Candle candle;
    }

    [Serializable]
    public class CandleHistory
    {
        public string symbol;
        public int interval;
        public List<Candle> candles = new List<Candle>();
    }
}
