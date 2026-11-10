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
using System.Linq;
using NUnit.Framework;
using QuantConnect.Logging;

namespace QuantConnect.Tests.Logging
{
    [TestFixture]
    public class LoggingCoverageTests
    {
        private ILogHandler _previousHandler;
        private bool _previousDebuggingEnabled;
        private int _previousDebuggingLevel;
        private RecordingHandler _recorder;

        [SetUp]
        public void SetUp()
        {
            _previousHandler = Log.LogHandler;
            _previousDebuggingEnabled = Log.DebuggingEnabled;
            _previousDebuggingLevel = Log.DebuggingLevel;
            _recorder = new RecordingHandler();
            Log.LogHandler = _recorder;
        }

        [TearDown]
        public void TearDown()
        {
            Log.LogHandler = _previousHandler;
            Log.DebuggingEnabled = _previousDebuggingEnabled;
            Log.DebuggingLevel = _previousDebuggingLevel;
        }

        [Test]
        public void TraceAndErrorSuppressRepeatedMessagesUnlessOverridden()
        {
            var text = "trace " + Guid.NewGuid();
            Log.Trace(text);
            Log.Trace(text);
            Log.Trace(text, true);
            Assert.AreEqual(2, _recorder.Traces.Count(t => t == text));

            var error = "error " + Guid.NewGuid();
            Log.Error(error);
            Log.Error(error);
            Log.Error(error, true);
            Assert.AreEqual(2, _recorder.Errors.Count(t => t == error));
        }

        [Test]
        public void FormatOverloads()
        {
            var id = Guid.NewGuid().ToString();
            Log.Trace("trace {0} {1}", id, 1);
            Log.Error("error {0} {1}", id, 2);
            Assert.Contains($"trace {id} 1", _recorder.Traces);
            Assert.Contains($"error {id} 2", _recorder.Errors);
        }

        [Test]
        public void ErrorWithExceptionIncludesMessageAndException()
        {
            var id = Guid.NewGuid().ToString();
            Log.Error(new InvalidOperationException("boom " + id), "context");
            var logged = _recorder.Errors.Last();
            StringAssert.Contains("context", logged);
            StringAssert.Contains("boom " + id, logged);
        }

        [Test]
        public void DebugRespectsEnabledFlagAndLevel()
        {
            Log.DebuggingEnabled = false;
            Log.Debug("hidden");
            Log.DebuggingEnabled = true;
            Log.DebuggingLevel = 2;
            Log.Debug("too low", 1);
            Log.Debug("shown", 2);
            CollectionAssert.AreEqual(new[] { "shown" }, _recorder.Debugs);
        }

        [Test]
        public void HandlerExceptionsAreSwallowed()
        {
            Log.LogHandler = new FunctionalLogHandler(_ => throw new Exception(), _ => throw new Exception(), _ => throw new Exception());
            Log.DebuggingEnabled = true;
            Log.DebuggingLevel = 1;
            Assert.DoesNotThrow(() =>
            {
                Log.Trace("x" + Guid.NewGuid());
                Log.Error("x" + Guid.NewGuid());
                Log.Debug("x");
            });
        }

        [Test]
        public void VarDumpDescribesProperties()
        {
            var dump = Log.VarDump(new Sample { Name = "abc", Values = new List<int> { 1, 2 }, Child = new Sample { Name = "child" } });
            StringAssert.Contains("Name = \"abc\"", dump);
            StringAssert.Contains("child", dump);
        }

        [Test]
        public void ExtensionMethodsFormatAndValidate()
        {
            ILogHandler handler = _recorder;
            handler.Trace("t {0}", 1);
            handler.Error("e {0}", 2);
            handler.Debug("d {0}", 3);
            Assert.Contains("t 1", _recorder.Traces);
            Assert.Contains("e 2", _recorder.Errors);
            Assert.Contains("d 3", _recorder.Debugs);

            ILogHandler nullHandler = null;
            Assert.Throws<ArgumentNullException>(() => nullHandler.Trace("t {0}", 1));
            Assert.Throws<ArgumentNullException>(() => nullHandler.Error("e {0}", 1));
            Assert.Throws<ArgumentNullException>(() => nullHandler.Debug("d {0}", 1));
        }

        [Test]
        public void CompositeLogHandlerForwardsToAllHandlers()
        {
            var first = new RecordingHandler();
            var second = new RecordingHandler();
            using (var composite = new CompositeLogHandler(first, second))
            {
                composite.Trace("t");
                composite.Debug("d");
                composite.Error("e");
            }
            foreach (var handler in new[] { first, second })
            {
                CollectionAssert.AreEqual(new[] { "t" }, handler.Traces);
                CollectionAssert.AreEqual(new[] { "d" }, handler.Debugs);
                CollectionAssert.AreEqual(new[] { "e" }, handler.Errors);
                Assert.IsTrue(handler.Disposed);
            }
            Assert.Throws<ArgumentNullException>(() => new CompositeLogHandler(null));
            Assert.Throws<ArgumentNullException>(() => new CompositeLogHandler(new ILogHandler[0]));
        }

        [Test]
        public void FunctionalLogHandlerInvokesDelegatesWithPrefix()
        {
            var output = new List<string>();
            using var handler = new FunctionalLogHandler(m => output.Add("D" + m), m => output.Add("T" + m), m => output.Add("E" + m), m => output.Add("R" + m));
            handler.Debug("debug");
            handler.Trace("trace");
            handler.Error("error");
            handler.Report("report");

            Assert.AreEqual(4, output.Count);
            Assert.IsTrue(output[0].StartsWith("D") && output[0].EndsWith("debug"));
            Assert.IsTrue(output[1].StartsWith("T") && output[1].EndsWith("trace"));
            Assert.IsTrue(output[2].StartsWith("E") && output[2].EndsWith("error"));
            Assert.IsTrue(output[3].StartsWith("R") && output[3].EndsWith("report"));

            using var empty = new FunctionalLogHandler();
            Assert.DoesNotThrow(() =>
            {
                empty.Debug("d");
                empty.Trace("t");
                empty.Error("e");
                empty.Report("r");
            });
        }

        [Test]
        public void QueueLogHandlerQueuesEntriesAndRaisesEvents()
        {
            using var handler = new QueueLogHandler();
            var raised = new List<LogEntry>();
            handler.LogEvent += raised.Add;

            handler.Error("e");
            handler.Debug("d");
            handler.Trace("t");

            Assert.AreEqual(3, raised.Count);
            Assert.AreEqual(LogType.Error, raised[0].MessageType);
            Assert.AreEqual(LogType.Debug, raised[1].MessageType);
            Assert.AreEqual(LogType.Trace, raised[2].MessageType);
            CollectionAssert.AreEqual(new[] { "e", "d", "t" }, handler.Logs.Select(l => l.Message));
        }

        [Test]
        public void LogEntryToStringContainsTypeAndMessage()
        {
            var entry = new LogEntry("message", new DateTime(2020, 1, 2, 3, 4, 5), LogType.Error);
            StringAssert.StartsWith("2020-01-02T03:04:05", entry.ToString());
            StringAssert.Contains("Error", entry.ToString());
            StringAssert.EndsWith("message", entry.ToString());

            var now = new LogEntry("now");
            Assert.AreEqual(LogType.Trace, now.MessageType);
            Assert.AreEqual("now", now.Message);
        }

        private class Sample
        {
            public string Name { get; set; }
            public List<int> Values { get; set; }
            public Sample Child { get; set; }
        }

        private class RecordingHandler : ILogHandler
        {
            public List<string> Traces { get; } = new();
            public List<string> Errors { get; } = new();
            public List<string> Debugs { get; } = new();
            public bool Disposed { get; private set; }

            public void Error(string text) => Errors.Add(text);
            public void Debug(string text) => Debugs.Add(text);
            public void Trace(string text) => Traces.Add(text);
            public void Report(string text) => Traces.Add("report:" + text);
            public void Dispose() => Disposed = true;
        }
    }
}
