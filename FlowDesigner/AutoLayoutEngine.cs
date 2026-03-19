using System.Collections.ObjectModel;

namespace FlowDesigner;

public static class AutoLayoutEngine
{
    private const double LayerGapX = 260;
    private const double NodeGapY = 110;
    private const double StartX = 80;
    private const double StartY = 80;

    public static void Layout(ObservableCollection<GraphNodeViewModel> nodes, ObservableCollection<GraphEdgeViewModel> edges)
    {
        if (nodes.Count == 0)
        {
            return;
        }

        var adj = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var inDeg = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var n in nodes)
        {
            adj[n.Id] = [];
            inDeg[n.Id] = 0;
        }

        foreach (var e in edges)
        {
            if (adj.ContainsKey(e.FromId) && inDeg.ContainsKey(e.ToId))
            {
                adj[e.FromId].Add(e.ToId);
                inDeg[e.ToId]++;
            }
        }

        var layers = new List<List<string>>();
        var remaining = new HashSet<string>(nodes.Select(n => n.Id), StringComparer.OrdinalIgnoreCase);

        while (remaining.Count > 0)
        {
            var layer = remaining.Where(id => inDeg[id] == 0).OrderBy(id => id).ToList();
            if (layer.Count == 0)
            {
                layer = [remaining.First()];
            }

            layers.Add(layer);
            foreach (var id in layer)
            {
                remaining.Remove(id);
                foreach (var child in adj[id])
                {
                    if (inDeg.ContainsKey(child))
                    {
                        inDeg[child]--;
                    }
                }
            }
        }

        var nodeMap = nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        for (var col = 0; col < layers.Count; col++)
        {
            var layerIds = layers[col];
            var totalHeight = layerIds.Count * NodeGapY;
            var offsetY = StartY + (nodes.Count > layerIds.Count ? (nodes.Count * NodeGapY - totalHeight) / 2 : 0);

            for (var row = 0; row < layerIds.Count; row++)
            {
                if (nodeMap.TryGetValue(layerIds[row], out var n))
                {
                    n.X = StartX + col * LayerGapX;
                    n.Y = offsetY + row * NodeGapY;
                }
            }
        }
    }
}
