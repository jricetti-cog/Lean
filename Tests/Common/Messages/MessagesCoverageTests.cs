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
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace QuantConnect.Tests.Common
{
    [TestFixture]
    public class MessagesCoverageTests
    {
        private static readonly Type[] SupportedTypes =
        {
            typeof(string), typeof(int), typeof(long), typeof(decimal), typeof(double), typeof(bool),
            typeof(DateTime), typeof(TimeSpan), typeof(Symbol)
        };

        private static bool IsSupported(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            return SupportedTypes.Contains(underlying) || underlying.IsEnum;
        }

        private static IEnumerable<MethodInfo> GetMessageMethods()
        {
            return typeof(global::QuantConnect.Messages).GetNestedTypes(BindingFlags.Public)
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Where(method => method.ReturnType == typeof(string) && !method.IsGenericMethodDefinition && !method.IsSpecialName)
                .Where(method => method.GetParameters().All(p => IsSupported(p.ParameterType)))
                .OrderBy(method => method.DeclaringType.Name).ThenBy(method => method.ToString());
        }

        private static IEnumerable<TestCaseData> MessageMethods()
        {
            return GetMessageMethods().Select(method =>
                new TestCaseData(method).SetName($"MessagesContainArguments({method.DeclaringType.Name}.{method.Name}({method.GetParameters().Length}))"));
        }

        private static object CreateArgument(Type type, int index, out string expectedText)
        {
            expectedText = null;
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            if (underlying == typeof(string))
            {
                expectedText = $"Arg{index}Text";
                return expectedText;
            }
            if (underlying == typeof(int))
            {
                var value = 7310 + index;
                expectedText = value.ToString(CultureInfo.InvariantCulture);
                return value;
            }
            if (underlying == typeof(long))
            {
                var value = 8420L + index;
                expectedText = value.ToString(CultureInfo.InvariantCulture);
                return value;
            }
            if (underlying == typeof(decimal))
            {
                return 12.5m + index;
            }
            if (underlying == typeof(double))
            {
                return 3.5d + index;
            }
            if (underlying == typeof(bool))
            {
                return index % 2 == 0;
            }
            if (underlying == typeof(DateTime))
            {
                return new DateTime(2021, 3, 4, 5, 6, 7).AddDays(index);
            }
            if (underlying == typeof(TimeSpan))
            {
                return TimeSpan.FromMinutes(5 + index);
            }
            if (underlying == typeof(Symbol))
            {
                expectedText = "SPY";
                return Symbols.SPY;
            }
            var values = Enum.GetValues(underlying);
            return values.GetValue(Math.Min(1, values.Length - 1));
        }

        private static string Normalize(string text)
        {
            return text.Replace("_", string.Empty, StringComparison.InvariantCulture).ToLowerInvariant();
        }

        [TestCaseSource(nameof(MessageMethods))]
        public void MessagesContainArguments(MethodInfo method)
        {
            var parameters = method.GetParameters();
            var expectedTexts = new List<string>();
            var arguments = new object[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                arguments[i] = CreateArgument(parameters[i].ParameterType, i, out var expectedText);
                if (expectedText != null)
                {
                    expectedTexts.Add(expectedText);
                }
            }

            var message = (string)method.Invoke(null, arguments);

            Assert.IsFalse(string.IsNullOrWhiteSpace(message));
            var normalizedMessage = Normalize(message);
            foreach (var expectedText in expectedTexts)
            {
                StringAssert.Contains(Normalize(expectedText), normalizedMessage);
            }
        }
    }
}
