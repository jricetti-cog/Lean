using System.Text.Json.Nodes;

namespace QuantConnect.Services.Results;

/// <summary>Chart series downsampling and merging on LEAN's serialized points (<c>[x, y]</c>, candles <c>[x, o, h, l, c]</c>, or <c>{x, y}</c>).</summary>
public static class Downsampler
{
    public static double? X(JsonNode? point) => point switch
    {
        JsonArray { Count: > 0 } array => LeanJson.AsDouble(array[0]),
        JsonObject obj => LeanJson.AsDouble(obj.Get("x")),
        _ => null,
    };

    public static double? Y(JsonNode? point) => point switch
    {
        JsonArray { Count: 2 } array => LeanJson.AsDouble(array[1]),
        JsonObject obj when obj.Count <= 2 => LeanJson.AsDouble(obj.Get("y")),
        _ => null,
    };

    /// <summary>
    /// At most <paramref name="maxPoints"/> points, always keeping the first and last. Line points use
    /// largest-triangle-three-buckets so peaks survive; other shapes (candles, points with nulls) use an even stride.
    /// </summary>
    public static JsonArray Downsample(JsonArray values, int maxPoints)
    {
        if (maxPoints < 3)
        {
            maxPoints = 3;
        }
        if (values.Count <= maxPoints)
        {
            return (JsonArray)values.DeepClone();
        }
        var xs = new double[values.Count];
        var ys = new double[values.Count];
        var line = true;
        for (var i = 0; i < values.Count && line; i++)
        {
            if (X(values[i]) is { } x && Y(values[i]) is { } y)
            {
                xs[i] = x;
                ys[i] = y;
            }
            else
            {
                line = false;
            }
        }
        IReadOnlyList<int> indices = line ? LargestTriangleThreeBuckets(xs, ys, maxPoints) : Stride(values.Count, maxPoints);
        return new JsonArray(indices.Select(i => values[i]?.DeepClone()).ToArray());
    }

    /// <summary>Downsamples every series of a normalized chart; returns the longest original series length.</summary>
    public static int DownsampleChart(JsonObject chart, int maxPoints)
    {
        var longest = 0;
        if (chart["series"] is JsonObject series)
        {
            foreach (var (_, value) in series)
            {
                if (value is JsonObject seriesObject && seriesObject["values"] is JsonArray points)
                {
                    longest = Math.Max(longest, points.Count);
                    seriesObject["values"] = Downsample(points, maxPoints);
                }
            }
        }
        return longest;
    }

    /// <summary>Appends <paramref name="update"/>'s points newer than the stored ones (partial packets carry chart deltas).</summary>
    public static JsonObject Merge(JsonObject? stored, JsonObject update)
    {
        if (stored == null)
        {
            return (JsonObject)update.DeepClone();
        }
        var merged = (JsonObject)stored.DeepClone();
        foreach (var (key, value) in update)
        {
            if (key != "series")
            {
                merged[key] = value?.DeepClone();
            }
        }
        var mergedSeries = merged["series"] as JsonObject ?? new JsonObject();
        merged["series"] = mergedSeries;
        if (update["series"] is JsonObject updateSeries)
        {
            foreach (var (name, value) in updateSeries)
            {
                if (value is not JsonObject incoming)
                {
                    continue;
                }
                if (mergedSeries[name] is not JsonObject existing || existing["values"] is not JsonArray existingPoints)
                {
                    mergedSeries[name] = incoming.DeepClone();
                    continue;
                }
                foreach (var (field, fieldValue) in incoming)
                {
                    if (field != "values")
                    {
                        existing[field] = fieldValue?.DeepClone();
                    }
                }
                var lastX = existingPoints.Count > 0 ? X(existingPoints[^1]) : null;
                foreach (var point in incoming["values"] as JsonArray ?? [])
                {
                    if (lastX == null || X(point) is not { } x || x > lastX)
                    {
                        existingPoints.Add(point?.DeepClone());
                        lastX = X(point) ?? lastX;
                    }
                }
            }
        }
        return merged;
    }

    private static int[] Stride(int count, int maxPoints)
    {
        var indices = new int[maxPoints];
        for (var i = 0; i < maxPoints; i++)
        {
            indices[i] = (int)Math.Round(i * (count - 1) / (double)(maxPoints - 1));
        }
        return indices;
    }

    private static List<int> LargestTriangleThreeBuckets(double[] xs, double[] ys, int threshold)
    {
        var count = xs.Length;
        var sampled = new List<int>(threshold) { 0 };
        var every = (count - 2) / (double)(threshold - 2);
        var a = 0;
        for (var i = 0; i < threshold - 2; i++)
        {
            var avgStart = (int)Math.Floor((i + 1) * every) + 1;
            var avgEnd = Math.Min((int)Math.Floor((i + 2) * every) + 1, count);
            double avgX = 0, avgY = 0;
            for (var j = avgStart; j < avgEnd; j++)
            {
                avgX += xs[j];
                avgY += ys[j];
            }
            var avgLength = Math.Max(avgEnd - avgStart, 1);
            avgX /= avgLength;
            avgY /= avgLength;

            var rangeStart = (int)Math.Floor(i * every) + 1;
            var rangeEnd = (int)Math.Floor((i + 1) * every) + 1;
            var maxArea = -1d;
            var next = rangeStart;
            for (var j = rangeStart; j < rangeEnd; j++)
            {
                var area = Math.Abs((xs[a] - avgX) * (ys[j] - ys[a]) - (xs[a] - xs[j]) * (avgY - ys[a]));
                if (area > maxArea)
                {
                    maxArea = area;
                    next = j;
                }
            }
            sampled.Add(next);
            a = next;
        }
        sampled.Add(count - 1);
        return sampled;
    }
}
