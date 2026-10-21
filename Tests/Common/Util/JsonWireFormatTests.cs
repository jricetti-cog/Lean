/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using QuantConnect.Algorithm.Framework.Alphas;
using QuantConnect.Notifications;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Orders.TimeInForces;
using QuantConnect.Packets;
using QuantConnect.Securities;
using QuantConnect.Statistics;

namespace QuantConnect.Tests.Common.Util
{
    /// <summary>
    /// Pins the JSON wire format of LEAN types. The expected output in TestData/json-wire-format/golden.txt
    /// was generated with the Newtonsoft.Json based serialization and must stay byte for byte identical.
    /// Set the environment variable LEAN_JSON_GOLDEN_WRITE to a file path to regenerate it.
    /// </summary>
    [TestFixture]
    public class JsonWireFormatTests
    {
        private const string GoldenPath = "TestData/json-wire-format/golden.txt";
        private const string Exception = "!EXCEPTION";

        private static readonly Assembly[] Assemblies =
        {
            typeof(Symbol).Assembly,
            typeof(QuantConnect.Api.Api).Assembly,
            typeof(QuantConnect.Lean.Engine.Engine).Assembly,
            typeof(QuantConnect.Brokerages.Brokerage).Assembly,
            typeof(QuantConnect.Optimizer.LeanOptimizer).Assembly,
            typeof(QuantConnect.Report.Report).Assembly,
            typeof(QuantConnect.Messaging.StreamingMessageHandler).Assembly,
            typeof(QuantConnect.Queues.JobQueue).Assembly,
        };

        [Test]
        public void SerializedOutputMatchesGoldenFile()
        {
            var generatePath = Environment.GetEnvironmentVariable("LEAN_JSON_GOLDEN_WRITE");
            if (!string.IsNullOrEmpty(generatePath))
            {
                File.WriteAllText(generatePath, Generate(GetCasesToGenerate()));
                Assert.Pass($"Golden file written to {generatePath}");
            }

            var expected = Parse(File.ReadAllText(GoldenPath));
            var failures = new List<string>();
            foreach (var (name, factory) in GetCases(expected.Keys))
            {
                var expectedLines = expected[name];
                var actualLines = Run(factory);
                for (var i = 0; i < expectedLines.Length; i++)
                {
                    if (expectedLines[i] == Exception)
                    {
                        continue;
                    }
                    if (actualLines[i] != expectedLines[i])
                    {
                        failures.Add($"{name} [{Modes[i]}]{Environment.NewLine}  expected: {expectedLines[i]}{Environment.NewLine}  actual:   {actualLines[i]}");
                    }
                }
            }
            Assert.IsEmpty(failures, $"{failures.Count} of {expected.Count} cases differ:{Environment.NewLine}" + string.Join(Environment.NewLine, failures));
        }

        private static readonly string[] Modes = { "default", "camelCase", "roundtrip" };

        private static string[] Run(Func<object> factory)
        {
            var result = new string[Modes.Length];
            object value;
            try
            {
                value = factory();
            }
            catch
            {
                return Enumerable.Repeat(Exception, Modes.Length).ToArray();
            }
            result[0] = Try(() => JsonWireFormat.Serialize(value, camelCase: false));
            result[1] = Try(() => JsonWireFormat.Serialize(value, camelCase: true));
            result[2] = result[0] == Exception ? Exception : Try(() => JsonWireFormat.Serialize(JsonWireFormat.Deserialize(result[0], value.GetType()), camelCase: false));
            return result;
        }

        private static string Try(Func<string> func)
        {
            try
            {
                var result = func();
                return result.Contains('\n') ? result.Replace("\r", "\\r").Replace("\n", "\\n") : result;
            }
            catch
            {
                return Exception;
            }
        }

        private static string Generate(IEnumerable<(string, Func<object>)> cases)
        {
            var builder = new StringBuilder();
            foreach (var (name, factory) in cases)
            {
                var lines = Run(factory);
                if (lines.All(x => x == Exception))
                {
                    continue;
                }
                builder.Append("### ").Append(name).Append('\n');
                foreach (var line in lines)
                {
                    builder.Append(line).Append('\n');
                }
            }
            return builder.ToString();
        }

        private static Dictionary<string, string[]> Parse(string content)
        {
            var result = new Dictionary<string, string[]>();
            var lines = content.Split('\n');
            for (var i = 0; i + Modes.Length < lines.Length; i += Modes.Length + 1)
            {
                result[lines[i].Substring(4)] = lines.Skip(i + 1).Take(Modes.Length).ToArray();
            }
            return result;
        }

        private static IEnumerable<(string, Func<object>)> GetCasesToGenerate()
        {
            foreach (var manual in GetManualCases())
            {
                yield return manual;
            }
            foreach (var type in Assemblies.SelectMany(x => x.GetTypes()).Where(IsJsonAnnotated).OrderBy(x => x.FullName, StringComparer.Ordinal))
            {
                yield return ("type:" + type.FullName, () => Populate(type, 0));
            }
        }

        private static IEnumerable<(string, Func<object>)> GetCases(IEnumerable<string> names)
        {
            var manual = GetManualCases().ToDictionary(x => x.Item1, x => x.Item2);
            var types = Assemblies.SelectMany(x => x.GetTypes()).Where(IsJsonAnnotated).ToDictionary(x => "type:" + x.FullName);
            foreach (var name in names)
            {
                if (manual.TryGetValue(name, out var factory))
                {
                    yield return (name, factory);
                }
                else if (types.TryGetValue(name, out var type))
                {
                    yield return (name, () => Populate(type, 0));
                }
                else
                {
                    yield return (name, () => throw new InvalidOperationException($"Unknown case {name}"));
                }
            }
        }

        private static bool IsJsonAnnotated(Type type)
        {
            if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters || type.FullName == null
                || type.IsSubclassOf(typeof(Delegate)) || type.IsEnum
                || type.Namespace == null || type.Namespace.StartsWith("QuantConnect.Data.Fundamental", StringComparison.Ordinal)
                || type.GetCustomAttributes(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false).Length > 0)
            {
                return false;
            }
            static bool HasJsonAttribute(MemberInfo member) => member.GetCustomAttributes(false)
                .Any(x => x.GetType().Namespace?.Contains("Json", StringComparison.Ordinal) == true);
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                if (HasJsonAttribute(current) || current.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Any(HasJsonAttribute))
                {
                    return true;
                }
            }
            return false;
        }

        private static object Populate(Type type, int depth)
        {
            var instance = CreateInstance(type);
            var index = 0;
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public).Where(x => !x.IsInitOnly))
            {
                try { field.SetValue(instance, Sample(field.FieldType, index++, depth)); } catch { }
            }
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(x => x.GetIndexParameters().Length == 0))
            {
                var setter = property.GetSetMethod(true) ?? property.DeclaringType?.GetProperty(property.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetSetMethod(true);
                if (setter == null)
                {
                    continue;
                }
                try { setter.Invoke(instance, new[] { Sample(property.PropertyType, index++, depth) }); } catch { }
            }
            return instance;
        }

        private static object CreateInstance(Type type)
        {
            var constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            return constructor != null ? constructor.Invoke(null) : System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
        }

        private static object Sample(Type type, int index, int depth)
        {
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null)
            {
                return index % 3 == 2 ? null : Sample(underlying, index, depth);
            }
            if (type == typeof(string)) return "str" + index;
            if (type == typeof(int)) return index + 1;
            if (type == typeof(long)) return 1000L + index;
            if (type == typeof(short)) return (short)(index + 1);
            if (type == typeof(byte)) return (byte)(index + 1);
            if (type == typeof(uint)) return (uint)(index + 1);
            if (type == typeof(ulong)) return (ulong)(index + 1);
            if (type == typeof(bool)) return index % 2 == 0;
            if (type == typeof(char)) return (char)('a' + index % 26);
            if (type == typeof(decimal)) return index % 2 == 0 ? index + 1m : index + 0.25m;
            if (type == typeof(double)) return index % 2 == 0 ? index + 1d : index + 0.125d;
            if (type == typeof(float)) return index % 2 == 0 ? index + 1f : index + 0.5f;
            if (type == typeof(DateTime)) return new DateTime(2020, 1, 2, 3, 4, 5).AddDays(index);
            if (type == typeof(DateTimeOffset)) return new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero).AddDays(index);
            if (type == typeof(TimeSpan)) return TimeSpan.FromMinutes(index + 1);
            if (type == typeof(Guid)) return new Guid(index + 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
            if (type == typeof(Color)) return Color.FromArgb(255, 10 + index, 20, 30);
            if (type == typeof(Symbol)) return Symbols.SPY;
            if (type == typeof(object)) return "obj" + index;
            if (type == typeof(TimeInForce)) return TimeInForce.GoodTilDate(new DateTime(2020, 1, 2));
            if (type.IsEnum)
            {
                var values = Enum.GetValues(type);
                return values.Length == 0 ? Activator.CreateInstance(type) : values.GetValue(index % values.Length);
            }
            if (depth >= 2)
            {
                return type.IsValueType ? Activator.CreateInstance(type) : null;
            }
            if (type.IsArray)
            {
                var array = Array.CreateInstance(type.GetElementType(), 1);
                array.SetValue(Sample(type.GetElementType(), index, depth + 1), 0);
                return array;
            }
            if (type.IsGenericType)
            {
                var definition = type.GetGenericTypeDefinition();
                var arguments = type.GetGenericArguments();
                if (arguments.Length == 2 && (definition == typeof(Dictionary<,>) || definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>)))
                {
                    var dictionary = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(arguments));
                    dictionary.Add(Sample(arguments[0], index, depth + 1), Sample(arguments[1], index + 1, depth + 1));
                    return dictionary;
                }
                if (arguments.Length == 1 && (definition == typeof(List<>) || definition == typeof(IList<>) || definition == typeof(IEnumerable<>)
                    || definition == typeof(ICollection<>) || definition == typeof(IReadOnlyList<>) || definition == typeof(IReadOnlyCollection<>)))
                {
                    var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(arguments));
                    list.Add(Sample(arguments[0], index, depth + 1));
                    return list;
                }
                if (arguments.Length == 1 && definition == typeof(HashSet<>))
                {
                    var set = Activator.CreateInstance(type);
                    type.GetMethod("Add").Invoke(set, new[] { Sample(arguments[0], index, depth + 1) });
                    return set;
                }
            }
            if ((type.IsClass || type.IsValueType) && !type.IsAbstract && !type.ContainsGenericParameters
                && type.Namespace?.StartsWith("QuantConnect", StringComparison.Ordinal) == true)
            {
                return Populate(type, depth + 1);
            }
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        private static IEnumerable<(string, Func<object>)> GetManualCases()
        {
            var time = new DateTime(2015, 11, 23, 17, 15, 37);
            var group = new GroupOrderManager(1, 2, 10, 1.5m);
            group.OrderIds.Add(12345);
            group.OrderIds.Add(12346);
            var properties = new OrderProperties { TimeInForce = TimeInForce.GoodTilDate(new DateTime(2020, 1, 2)) };
            Order WithCommon(Order order)
            {
                order.Id = 12345;
                order.Price = 209.03m;
                order.ContingentId = 123456;
                order.BrokerId = new List<string> { "727", "54970" };
                order.PriceCurrency = "USD";
                return order;
            }
            var orders = new (string, Func<Order>)[]
            {
                ("MarketOrder", () => new MarketOrder(Symbols.SPY, 100, time, "now")),
                ("MarketOrderForex", () => new MarketOrder(Symbols.EURUSD, -1000.5m, time, "tag\"with\\escapes\u00e9", properties)),
                ("LimitOrder", () => new LimitOrder(Symbols.SPY, 100, 210m, time, "")),
                ("StopMarketOrder", () => new StopMarketOrder(Symbols.SPY, 100, 210.10m, time)),
                ("StopLimitOrder", () => new StopLimitOrder(Symbols.SPY, 100, 210.10m, 211m, time) { StopTriggered = true }),
                ("TrailingStopOrder", () => new TrailingStopOrder(Symbols.SPY, 100, 210.10m, 0.1m, true, time)),
                ("LimitIfTouchedOrder", () => new LimitIfTouchedOrder(Symbols.SPY, 100, 205m, 210m, time)),
                ("MarketOnOpenOrder", () => new MarketOnOpenOrder(Symbols.SPY, 100, time)),
                ("MarketOnCloseOrder", () => new MarketOnCloseOrder(Symbols.SPY, 100, time)),
                ("OptionExerciseOrder", () => new OptionExerciseOrder(Symbols.SPY_C_192_Feb19_2016, 1, time)),
                ("ComboMarketOrder", () => new ComboMarketOrder(Symbols.SPY, 100, time, group)),
                ("ComboLimitOrder", () => new ComboLimitOrder(Symbols.SPY, 100, 210m, time, group)),
                ("ComboLegLimitOrder", () => new ComboLegLimitOrder(Symbols.SPY, 100, 210m, time, group)),
            };
            foreach (var (name, factory) in orders)
            {
                yield return ("order:" + name, () => WithCommon(factory()));
            }
            yield return ("orders:dictionary", () => orders.Select((x, i) => (i, WithCommon(x.Item2()))).ToDictionary(x => x.i, x => x.Item2));
            yield return ("orders:list", () => orders.Select(x => WithCommon(x.Item2())).ToList());
            yield return ("orderEvent", () => new OrderEvent(1, Symbols.SPY, time, OrderStatus.PartiallyFilled, OrderDirection.Buy, 10.5m, 50, new OrderFee(new CashAmount(1.5m, "USD")), "message")
            {
                LimitPrice = 11, StopPrice = 12.5m, TriggerPrice = 3, TrailingAmount = 0.5m, TrailingAsPercentage = true, IsAssignment = true, Ticket = null, Id = 7
            });
            yield return ("orderEvent:empty-message", () => new OrderEvent(1, Symbols.SPY_C_192_Feb19_2016, time, OrderStatus.Filled, OrderDirection.Sell, 1, -5, OrderFee.Zero));
            yield return ("orderEvents:list", () => new List<OrderEvent> { new OrderEvent(1, Symbols.SPY, time, OrderStatus.Submitted, OrderDirection.Buy, 0, 10, OrderFee.Zero, "") });
            yield return ("insight", () => WithIds(new Insight(Symbols.SPY, TimeSpan.FromDays(1), InsightType.Price, InsightDirection.Up, 0.5, 0.75, "model", 0.25, "tag")
            {
                GeneratedTimeUtc = time, CloseTimeUtc = time.AddDays(1)
            }, Guid.Parse("8c60c4a4-0f7f-42e5-9c2f-9e2a6e9c5f0b")));
            yield return ("insights:list", () => new List<Insight> { WithIds(new Insight(Symbols.EURUSD, TimeSpan.FromHours(1), InsightType.Volatility, InsightDirection.Flat) { GeneratedTimeUtc = time, CloseTimeUtc = time }, null) });
            foreach (var symbol in new[] { Symbols.SPY, Symbols.EURUSD, Symbols.BTCUSD, Symbols.SPY_C_192_Feb19_2016, Symbols.Fut_SPY_Feb19_2016, Symbols.SPY_Option_Chain, Symbol.CreateCanonicalOption(Symbols.Fut_SPY_Feb19_2016) })
            {
                yield return ("symbol:" + symbol.ID, () => symbol);
            }
            yield return ("symbols:dictionary", () => new Dictionary<string, Symbol> { { "a", Symbols.SPY }, { "b", Symbols.SPY_C_192_Feb19_2016 } });
            yield return ("holding", () => new Holding { Symbol = Symbols.SPY, CurrencySymbol = "$", AveragePrice = 100, Quantity = 10.5m, MarketPrice = 101.25m, ConversionRate = 1, MarketValue = 1063.125m, UnrealizedPnL = 13.125m, UnrealizedPnLPercent = 1.25m });
            yield return ("holding:euro", () => new Holding { Symbol = Symbols.EURUSD, CurrencySymbol = "\u20ac", AveragePrice = 1.1m, Quantity = -1000, MarketPrice = 1.05m, ConversionRate = 1.2m, MarketValue = -1050, UnrealizedPnL = 50, UnrealizedPnLPercent = 4.5m });
            yield return ("holdings:dictionary", () => new Dictionary<string, Holding> { { "SPY", new Holding { Symbol = Symbols.SPY, Quantity = 1 } } });
            yield return ("timeInForce:gtc", () => TimeInForce.GoodTilCanceled);
            yield return ("timeInForce:day", () => TimeInForce.Day);
            yield return ("timeInForce:gtd", () => TimeInForce.GoodTilDate(time));
            yield return ("chart:all-series", () =>
            {
                var chart = new Chart("Strategy Equity");
                var line = new Series("Equity", SeriesType.Line, "$", Color.Red, ScatterMarkerSymbol.Circle) { Index = 1, ZIndex = 2, IndexName = "idx", Tooltip = "tip" };
                line.AddPoint(time, 100m);
                line.AddPoint(time.AddDays(1), 100.5m);
                line.Values.Add(new ChartPoint(time.AddDays(2), null));
                var scatter = new Series("Scatter", SeriesType.Scatter, 0);
                scatter.Values.Add(new ScatterChartPoint(time, 1.5m, "tooltip"));
                scatter.Values.Add(new ScatterChartPoint(time.AddDays(1), 2m));
                var candles = new CandlestickSeries("Candles", 2, "$");
                candles.AddPoint(time, 1, 2, 0.5m, 1.25m);
                var pie = new Series("Pie", SeriesType.Pie, 3);
                pie.AddPoint(time, 10);
                pie.AddPoint(time.AddDays(1), 20.5m);
                var bar = new Series("Bar", SeriesType.Bar, 0) { Color = Color.FromArgb(0x11, 0x22, 0x33) };
                bar.AddPoint(time, 3);
                chart.AddSeries(line);
                chart.AddSeries(scatter);
                chart.AddSeries(candles);
                chart.AddSeries(pie);
                chart.AddSeries(bar);
                return new Dictionary<string, Chart> { { chart.Name, chart } };
            });
            yield return ("notifications", () => new List<Notification>
            {
                new NotificationEmail("a@b.com", "subject", "message", "data", new Dictionary<string, string> { { "h", "v" } }),
                new NotificationSms("123", "msg"),
                new NotificationWeb("http://a.com", new { x = 1, y = "z" }, new Dictionary<string, string> { { "h", "v" } }),
                new NotificationTelegram("id", "msg", "token"),
            });
            yield return ("backtestResultPacket", () => BuildBacktestResultPacket(time));
            yield return ("liveResultPacket", () => new LiveResultPacket { ProjectId = 1, DeployId = "deploy",
                Results = new LiveResult { Holdings = new Dictionary<string, Holding> { { "SPY", new Holding { Symbol = Symbols.SPY, Quantity = 1, AveragePrice = 2 } } },
                    Cash = new Dictionary<string, Cash> { { "USD", new Cash("USD", 100, 1) } }, Orders = new Dictionary<int, Order> { { 1, WithCommon(new LimitOrder(Symbols.SPY, 1, 2, time)) } },
                    Statistics = new Dictionary<string, string> { { "a", "b" } }, RuntimeStatistics = new Dictionary<string, string> { { "Equity", "$100.00" } },
                    ServerStatistics = new Dictionary<string, string> { { "CPU", "1%" } }, State = new Dictionary<string, string> { { "Status", "Running" } } } });
            yield return ("algorithmStatusPacket", () => new AlgorithmStatusPacket("algo", 1, AlgorithmStatus.Running, "msg") { ChannelStatus = "x", OptimizationId = "opt" });
            yield return ("debugPacket", () => new DebugPacket(1, "algo", "compile", "message <html> & 'quotes' \u0001\u007f\ud83d\ude00", true));
            yield return ("logPacket", () => new LogPacket("algo", "message"));
            yield return ("runtimeErrorPacket", () => new RuntimeErrorPacket(1, "algo", "error", "stack"));
            yield return ("handledErrorPacket", () => new HandledErrorPacket("algo", "error", "stack"));
            yield return ("orderEventPacket", () => new OrderEventPacket("algo", new OrderEvent(1, Symbols.SPY, time, OrderStatus.Filled, OrderDirection.Buy, 1, 1, OrderFee.Zero, "m")));
            yield return ("securityTypesPacket", () => new SecurityTypesPacket { Types = new List<SecurityType> { SecurityType.Equity, SecurityType.Option } });
            yield return ("cashBook", () => { var book = new CashBook(); book.Add("EUR", 10.5m, 1.1m); return book; });
            yield return ("primitives", () => new Dictionary<string, object>
            {
                { "decimalWhole", 1m }, { "decimalScale", 1.50m }, { "decimalSmall", 0.0000001m }, { "decimalBig", 79228162514264337593543950335m },
                { "double", 1d }, { "doubleFraction", 0.1d }, { "doubleBig", 1e20 }, { "doubleNaN", double.NaN }, { "doubleInf", double.PositiveInfinity },
                { "float", 1.5f }, { "dateUnspecified", time }, { "dateUtc", DateTime.SpecifyKind(time, DateTimeKind.Utc).AddTicks(1234567) },
                { "timeSpan", TimeSpan.FromHours(25.5) }, { "string", "a<b>&'\"\\\n\t\u0001\u001f\u007f\u0085\u2028\u00e9\ud83d\ude00/" }, { "null", null },
                { "enum", SecurityType.Crypto }, { "char", 'c' }, { "long", long.MaxValue }, { "bool", true },
            });
        }

        private static Insight WithIds(Insight insight, Guid? groupId)
        {
            typeof(Insight).GetProperty(nameof(Insight.Id)).SetValue(insight, Guid.Parse("ec9e5d2b-c8e1-4e14-a6d9-1ef3dd6b0f47"));
            typeof(Insight).GetProperty(nameof(Insight.GroupId)).SetValue(insight, groupId);
            return insight;
        }

        private static BacktestResultPacket BuildBacktestResultPacket(DateTime time)
        {
            var result = new BacktestResult
            {
                Orders = new Dictionary<int, Order> { { 1, new MarketOrder(Symbols.SPY, 1, time) { Id = 1 } } },
                ProfitLoss = new Dictionary<DateTime, decimal> { { time, 1.5m }, { time.AddDays(1), -2m } },
                Statistics = new Dictionary<string, string> { { "Total Orders", "1" } },
                RuntimeStatistics = new Dictionary<string, string> { { "Equity", "$100" } },
                Charts = new Dictionary<string, Chart> { { "Benchmark", new Chart("Benchmark") } },
                AlgorithmConfiguration = new AlgorithmConfiguration { Name = "name", Tags = new HashSet<string> { "t" }, AccountCurrency = "USD", Parameters = new Dictionary<string, string> { { "p", "1" } } },
                State = new Dictionary<string, string> { { "Status", "Completed" } },
                OrderEvents = new List<OrderEvent> { new OrderEvent(1, Symbols.SPY, time, OrderStatus.Filled, OrderDirection.Buy, 1, 1, OrderFee.Zero) },
            };
            return new BacktestResultPacket { BacktestId = "id", Name = "name", CompileId = "compile", ProjectId = 1, Progress = 0.5m, Results = result,
                PeriodStart = time, PeriodFinish = time.AddDays(1), DateRequested = time, DateFinished = time, ProcessingTime = 2, TradeableDates = 1 };
        }
    }
}
