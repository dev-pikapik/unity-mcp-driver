using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace Northwind.Trading.UI
{
    public class CandleChart : MaskableGraphic
    {
        public List<Candle> candles = new List<Candle>();
        public int visibleCandles = 90;
        public float volumeShare = 0.16f;
        public Color upColor = new Color32(38, 166, 154, 255);
        public Color downColor = new Color32(239, 83, 80, 255);
        public Color gridColor = new Color32(255, 255, 255, 14);
        public Color lastPriceColor = new Color32(240, 185, 11, 160);
        public Text[] priceLabels = new Text[0];
        public Text[] timeLabels = new Text[0];
        public RectTransform lastPriceMarker;
        public Text lastPriceTag;

        public double PriceMin { get; private set; }
        public double PriceMax { get; private set; }
        public int FirstIndex { get; private set; }

        float SlotWidth => rectTransform.rect.width / Mathf.Max(1, visibleCandles);
        float PriceBottom => rectTransform.rect.yMin + rectTransform.rect.height * volumeShare;

        public float X(int index) => rectTransform.rect.xMin + (index - FirstIndex + 0.5f) * SlotWidth;

        public float Y(double price)
        {
            var r = rectTransform.rect;
            double t = PriceMax > PriceMin ? (price - PriceMin) / (PriceMax - PriceMin) : 0.5;
            return PriceBottom + (float)t * (r.yMax - PriceBottom);
        }

        public void Refresh()
        {
            FirstIndex = Math.Max(0, candles.Count - visibleCandles);
            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = FirstIndex; i < candles.Count; i++)
            {
                lo = Math.Min(lo, candles[i].low);
                hi = Math.Max(hi, candles[i].high);
            }
            if (lo > hi) { lo = 0; hi = 1; }
            double pad = Math.Max((hi - lo) * 0.08, 0.05);
            PriceMin = lo - pad;
            PriceMax = hi + pad;
            UpdateLabels();
            SetVerticesDirty();
        }

        void UpdateLabels()
        {
            for (int i = 0; i < priceLabels.Length; i++)
            {
                double price = PriceMax - (PriceMax - PriceMin) * (i + 0.5) / priceLabels.Length;
                priceLabels[i].text = price.ToString("F2", CultureInfo.InvariantCulture);
                SetY(priceLabels[i].rectTransform, Y(price));
            }
            for (int i = 0; i < timeLabels.Length; i++)
            {
                int index = FirstIndex + (int)((i + 0.5f) * visibleCandles / timeLabels.Length);
                timeLabels[i].text = index < candles.Count
                    ? DateTimeOffset.FromUnixTimeSeconds(candles[index].time).ToLocalTime().ToString("HH:mm:ss")
                    : "";
            }
            if (lastPriceMarker != null && candles.Count > 0)
            {
                double last = candles[candles.Count - 1].close;
                lastPriceTag.text = last.ToString("F2", CultureInfo.InvariantCulture);
                SetY(lastPriceMarker, Y(last));
            }
        }

        void SetY(RectTransform label, float chartY)
        {
            var p = label.anchoredPosition;
            p.y = chartY - rectTransform.rect.yMax;
            label.anchoredPosition = p;
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            var r = rectTransform.rect;
            for (int i = 0; i < priceLabels.Length; i++)
            {
                float y = Y(PriceMax - (PriceMax - PriceMin) * (i + 0.5) / priceLabels.Length);
                Quad(vh, r.xMin, y - 0.5f, r.xMax, y + 0.5f, gridColor);
            }
            Quad(vh, r.xMin, PriceBottom - 0.5f, r.xMax, PriceBottom + 0.5f, gridColor);
            if (candles.Count == 0) return;

            double maxVolume = 1;
            for (int i = FirstIndex; i < candles.Count; i++) maxVolume = Math.Max(maxVolume, candles[i].volume);

            float body = Mathf.Max(1f, SlotWidth * 0.7f);
            for (int i = FirstIndex; i < candles.Count; i++)
            {
                var c = candles[i];
                var col = c.close >= c.open ? upColor : downColor;
                float x = X(i);
                Quad(vh, x - 0.5f, Y(c.low), x + 0.5f, Y(c.high), col);
                float top = Y(Math.Max(c.open, c.close)), bottom = Y(Math.Min(c.open, c.close));
                Quad(vh, x - body / 2, bottom, x + body / 2, Mathf.Max(top, bottom + 1), col);
                float barHeight = (float)(c.volume / maxVolume) * (PriceBottom - r.yMin - 4);
                var vcol = col;
                vcol.a = 0.35f;
                Quad(vh, x - body / 2, r.yMin, x + body / 2, r.yMin + barHeight, vcol);
            }
            float lastY = Y(candles[candles.Count - 1].close);
            for (float x = r.xMin; x < r.xMax; x += 8) Quad(vh, x, lastY - 0.5f, Mathf.Min(x + 4, r.xMax), lastY + 0.5f, lastPriceColor);
        }

        static void Quad(VertexHelper vh, float x0, float y0, float x1, float y1, Color c)
        {
            int i = vh.currentVertCount;
            vh.AddVert(new Vector3(x0, y0), c, Vector2.zero);
            vh.AddVert(new Vector3(x0, y1), c, Vector2.zero);
            vh.AddVert(new Vector3(x1, y1), c, Vector2.zero);
            vh.AddVert(new Vector3(x1, y0), c, Vector2.zero);
            vh.AddTriangle(i, i + 1, i + 2);
            vh.AddTriangle(i + 2, i + 3, i);
        }
    }
}
