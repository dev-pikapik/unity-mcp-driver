using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Northwind.Trading.UI
{
    public class LineSeries : MaskableGraphic
    {
        public CandleChart chart;
        public List<float> values = new List<float>();
        public float thickness = 1.6f;

        public void Refresh() => SetVerticesDirty();

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (chart == null) return;
            for (int i = Mathf.Max(1, chart.FirstIndex + 1); i < values.Count; i++)
            {
                if (float.IsNaN(values[i - 1]) || float.IsNaN(values[i])) continue;
                var a = new Vector2(chart.X(i - 1), chart.Y(values[i - 1]));
                var b = new Vector2(chart.X(i), chart.Y(values[i]));
                var n = (b - a).normalized;
                var side = new Vector2(-n.y, n.x) * thickness * 0.5f;
                var ext = n * thickness * 0.5f;
                int v = vh.currentVertCount;
                vh.AddVert(a - side - ext, color, Vector2.zero);
                vh.AddVert(a + side - ext, color, Vector2.zero);
                vh.AddVert(b + side + ext, color, Vector2.zero);
                vh.AddVert(b - side + ext, color, Vector2.zero);
                vh.AddTriangle(v, v + 1, v + 2);
                vh.AddTriangle(v + 2, v + 3, v);
            }
        }
    }
}
