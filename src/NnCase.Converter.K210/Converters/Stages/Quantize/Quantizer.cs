﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NnCase.Converter.Data;
using NnCase.Converter.Model;
using NnCase.Converter.Model.Layers;
using TensorFlow;

#if NET471
using System.Collections.Async;
#endif

namespace NnCase.Converter.K210.Converters.Stages.Quantize
{
    public static class Quantizer
    {
        public static async Task<QuantizationContext> QuantizeAsync(Dataset dataset, GraphPlanContext planContext)
        {
            using (var session = new TFSession(planContext.TFGraph))
            {
                var connectors = new List<OutputConnector>();
                var toFetches = new List<TFOutput>();

                foreach (var output in planContext.TFOutputs)
                {
                    connectors.Add(output.Key);
                    if (!(output.Key.Owner is InputLayer))
                        toFetches.Add(output.Value);
                }

                var quantizationContext = new QuantizationContext { Outputs = connectors, PlanContext = planContext };

#if NET471
                await dataset.GetBatchesAsync().ForEachAsync(async batch =>
#else
                await foreach (var batch in dataset.GetBatchesAsync())
#endif
                {
                    var input = batch.ToNHWC();
                    var runner = session.GetRunner();

                    runner.AddInput(planContext.Inputs.Values.First(), input);
                    foreach (var fetch in toFetches)
                        runner.Fetch(fetch);

                    var outputs = runner.Run();
                    RecordOutputs(new[] { input }.Concat(outputs).ToList(), quantizationContext);
                }
#if NET471
                );
#endif
                return quantizationContext;
            }
        }

        private static unsafe void RecordOutputs(IReadOnlyList<TFTensor> outputs, QuantizationContext context)
        {
            for (int i = 0; i < outputs.Count; i++)
            {
                var conn = context.Outputs[i];
                var span = new Span<float>(outputs[i].Data.ToPointer(), (int)outputs[i].TensorByteSize / 4);
                var newRange = GetRange(span);
                if (context.Distributions.TryGetValue(conn, out var range))
                    context.Distributions[conn] = range.EMA(0.01, newRange);
                else
                    context.Distributions.Add(conn, newRange);
            }
        }

        public static Range GetRange(Span<float> data)
        {
            double min = double.MaxValue, max = double.MinValue;
            for (int j = 0; j < data.Length; j++)
            {
                if (Math.Abs(data[j]) > 100) continue;
                min = Math.Min(min, data[j]);
                max = Math.Max(max, data[j]);
            }

            return new Range { Min = min, Max = max };
        }

        public static double Quantize(ReadOnlySpan<float> data, Span<ushort> dest, double scale, double bias, int weightsBits)
        {
            ushort max = (ushort)((1 << weightsBits) - 1);

            for (int i = 0; i < data.Length; i++)
                dest[i] = (ushort)
#if NET471
                    FxExtensions
#else
                    Math
#endif
                    .Clamp(Math.Round(data[i] * scale - bias), 0, max);

            var diff = new double[data.Length];
            for (int i = 0; i < data.Length; i++)
                diff[i] = Math.Abs(((dest[i] + bias) / scale) - data[i]);
            var avg = diff.Max();
            return avg;
        }

        public static ushort[] Quantize(ReadOnlySpan<float> data, double scale, double bias, int weightsBits)
        {
            var q = new ushort[data.Length];
            Quantize(data, q, scale, bias, weightsBits);
            return q;
        }

        public static (double value, int shift) ExtractValueAndShift(double value, int maxBits, int maxShift)
        {
            int shift = 0;
            double mul = 0;

            if (Math.Abs(value) > 1)
            {
                var mulShift = 0;
                mul = C.math.frexp(value, ref mulShift);
                shift = Math.Min(maxShift, maxBits - 1 - mulShift);
                mul = mul * Math.Pow(2, shift + mulShift);
            }
            else if (value == 0)
            {
                mul = shift = 0;
            }
            else
            {
                var mulShift = 0;
                mul = C.math.frexp(value, ref mulShift);
                shift = Math.Min(maxShift + mulShift, maxBits - 1);
                mul = mul * Math.Pow(2, shift);
                shift -= mulShift;
            }

            Debug.Assert(Math.Abs(mul) < Math.Pow(2, maxBits - 1));
            Debug.Assert(shift <= maxShift);
            Debug.Assert(Math.Abs(value - mul * Math.Pow(2, -shift)) <= double.Epsilon);
            return (mul, shift);
        }
    }
}
