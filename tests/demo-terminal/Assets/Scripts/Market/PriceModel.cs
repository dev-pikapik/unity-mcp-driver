using System;
using System.Collections.Generic;

namespace Northwind.Trading
{
    public sealed class PriceModel
    {
        public const string Symbol = "DEMO/USD";
        public const double StepSeconds = 0.5;
        public const int StepsPerCandle = 10;
        public const int CandleSeconds = 5;
        public const int HistoryCandles = 300;
        public const int BookLevels = 10;
        const uint Seed = 20261120;

        public readonly long origin;
        readonly List<double> walk = new List<double>();

        public PriceModel(double nowUnix)
        {
            origin = (long)Math.Floor(nowUnix / CandleSeconds) * CandleSeconds - HistoryCandles * CandleSeconds;
        }

        public static double Noise(long i)
        {
            unchecked
            {
                uint x = (uint)i * 0x9E3779B1u + Seed;
                x ^= x >> 16;
                x *= 0x85EBCA6Bu;
                x ^= x >> 13;
                x *= 0xC2B2AE35u;
                x ^= x >> 16;
                return x / 2147483648.0 - 1;
            }
        }

        public long StepAt(double nowUnix) => (long)Math.Floor((nowUnix - origin) / StepSeconds);

        long Cents(long i)
        {
            while (walk.Count <= i)
            {
                double prev = walk.Count == 0 ? 0 : walk[walk.Count - 1];
                walk.Add(prev * 0.995 + 0.25 * Noise(walk.Count));
            }
            double p = 248.5 + walk[(int)i] + 2.0 * Math.Sin(2 * Math.PI * i / 200.0);
            return (long)Math.Floor(p * 100 + 0.5);
        }

        public double Price(long i) => Cents(i) / 100.0;

        static double Volume(long i) => 1 + (long)Math.Floor((Noise(i + 1000003) + 1) * 20);

        public Candle CandleAt(long k, long lastStep)
        {
            long first = k * StepsPerCandle;
            long last = Math.Min(first + StepsPerCandle - 1, lastStep);
            var c = new Candle { time = origin + k * CandleSeconds, open = Price(first), close = Price(last) };
            c.high = c.low = c.open;
            for (long i = first; i <= last; i++)
            {
                double p = Price(i);
                c.high = Math.Max(c.high, p);
                c.low = Math.Min(c.low, p);
                c.volume += Volume(i);
            }
            return c;
        }

        public CandleHistory History(long step, int limit)
        {
            var h = new CandleHistory { symbol = Symbol, interval = CandleSeconds };
            long current = step / StepsPerCandle;
            for (long k = Math.Max(0, current - limit + 1); k <= current; k++) h.candles.Add(CandleAt(k, step));
            return h;
        }

        public Tick TickAt(long step) => new Tick
        {
            symbol = Symbol,
            time = origin + step * StepSeconds,
            price = Price(step),
            step = step,
            candle = CandleAt(step / StepsPerCandle, step)
        };

        public OrderBook BookAt(long step)
        {
            long c = Cents(step);
            var book = new OrderBook { symbol = Symbol, time = origin + step * StepSeconds };
            for (int j = 0; j < BookLevels; j++)
            {
                book.bids.Add(new BookLevel { price = (c - 1 - 5 * j) / 100.0, size = 5 + Math.Floor((Noise(step * 31 + j + 17) + 1) * 60) });
                book.asks.Add(new BookLevel { price = (c + 1 + 5 * j) / 100.0, size = 5 + Math.Floor((Noise(step * 31 + j + 517) + 1) * 60) });
            }
            return book;
        }
    }
}
