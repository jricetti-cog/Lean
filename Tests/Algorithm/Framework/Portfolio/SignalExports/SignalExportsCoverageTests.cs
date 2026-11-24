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
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using QuantConnect.Algorithm;
using Python.Runtime;
using QuantConnect.Algorithm.Framework.Portfolio;
using QuantConnect.Algorithm.Framework.Portfolio.SignalExports;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Python;
using QuantConnect.Securities;
using QuantConnect.Tests.Engine.DataFeeds;

namespace QuantConnect.Tests.Algorithm.Framework.Portfolio.SignalExports
{
    [TestFixture]
    public class SignalExportsCoverageTests
    {
        private static readonly Symbol[] NumeraiSymbols =
        {
            Symbols.SGX, Symbols.AAPL, Symbols.MSFT, Symbols.ZNGA, Symbols.FXE,
            Symbols.LODE, Symbols.IBM, Symbols.GOOG, Symbols.NFLX, Symbols.CAT
        };

        private const string NumeraiAuthOk = @"{""data"":{""submissionUploadSignalsAuth"":{""filename"":""upload.csv"",""url"":""https://numerai.test/put""}}}";
        private const string NumeraiSubmissionOk = @"{""data"":{""createSignalsSubmission"":{""id"":""42"",""firstEffectiveDate"":""2023-03-03""}}}";

        #region Numerai

        [Test]
        public void NumeraiSendsAuthPutAndSubmissionRequests()
        {
            var algorithm = CreateAlgorithm(new DateTime(2023, 03, 03));
            using var export = new NumeraiSignalExport("public", "secret", "model", "file.csv");
            var handler = Inject(export,
                Respond(HttpStatusCode.OK, NumeraiAuthOk),
                Respond(HttpStatusCode.OK, ""),
                Respond(HttpStatusCode.OK, NumeraiSubmissionOk));

            var result = export.Send(CreateParameters(algorithm, NumeraiSymbols.Select(s => new PortfolioTarget(s, 0.05m))));

            Assert.IsTrue(result, string.Join(",", Errors(algorithm)));
            Assert.AreEqual(3, handler.Requests.Count);

            var auth = handler.Requests[0];
            Assert.AreEqual(HttpMethod.Post, auth.Method);
            Assert.AreEqual("https://api-tournament.numer.ai/", auth.Uri.ToString());
            Assert.AreEqual("Token public$secret", auth.Authorization);
            StringAssert.Contains("submissionUploadSignalsAuth", auth.Body);
            StringAssert.Contains(@"""filename"":""file.csv""", auth.Body);
            StringAssert.Contains(@"""modelId"":""model""", auth.Body);

            var put = handler.Requests[1];
            Assert.AreEqual(HttpMethod.Put, put.Method);
            Assert.AreEqual("https://numerai.test/put", put.Uri.ToString());
            StringAssert.StartsWith("numerai_ticker,signal\nSGX SP,0.05\nAAPL US,0.05\n", put.Body);

            var submission = handler.Requests[2];
            Assert.AreEqual(HttpMethod.Post, submission.Method);
            StringAssert.Contains("createSignalsSubmission", submission.Body);
            StringAssert.Contains(@"""filename"":""upload.csv""", submission.Body);
            Assert.IsEmpty(Errors(algorithm));
        }

        [Test]
        public void NumeraiReturnsFalseWhenAuthRequestFails()
        {
            var algorithm = CreateAlgorithm(new DateTime(2023, 03, 03));
            using var export = new NumeraiSignalExport("public", "secret", "model");
            var handler = Inject(export, Respond(HttpStatusCode.Unauthorized, "{}"));

            Assert.IsFalse(export.Send(CreateParameters(algorithm, NumeraiSymbols.Select(s => new PortfolioTarget(s, 0.1m)))));
            Assert.AreEqual(1, handler.Requests.Count);
            StringAssert.Contains("Numerai API returned HttpRequestException Unauthorized", Errors(algorithm).Single());
        }

        [Test]
        public void NumeraiReturnsFalseWhenAuthResponseHasNoUploadData()
        {
            var algorithm = CreateAlgorithm(new DateTime(2023, 03, 03));
            using var export = new NumeraiSignalExport("public", "secret", "model");
            var handler = Inject(export, Respond(HttpStatusCode.OK, @"{""data"":{""submissionUploadSignalsAuth"":{}},""errors"":[""bad model""]}"));

            Assert.IsFalse(export.Send(CreateParameters(algorithm, NumeraiSymbols.Select(s => new PortfolioTarget(s, 0.1m)))));
            Assert.AreEqual(1, handler.Requests.Count);
            StringAssert.Contains("Numerai API returned the following errors: bad model", Errors(algorithm).Single());
        }

        [Test]
        public void NumeraiReturnsFalseWhenSubmissionRequestFails()
        {
            var algorithm = CreateAlgorithm(new DateTime(2023, 03, 03));
            using var export = new NumeraiSignalExport("public", "secret", "model");
            var handler = Inject(export,
                Respond(HttpStatusCode.OK, NumeraiAuthOk),
                Respond(HttpStatusCode.OK, ""),
                Respond(HttpStatusCode.BadRequest, "submission rejected"));

            Assert.IsFalse(export.Send(CreateParameters(algorithm, NumeraiSymbols.Select(s => new PortfolioTarget(s, 0.1m)))));
            Assert.AreEqual(3, handler.Requests.Count);
            StringAssert.Contains("Numerai API returned HttpRequestException submission rejected", Errors(algorithm).Single());
        }

        [Test]
        public void NumeraiReturnsFalseWhenSubmissionResponseHasNoData()
        {
            var algorithm = CreateAlgorithm(new DateTime(2023, 03, 03));
            using var export = new NumeraiSignalExport("public", "secret", "model");
            Inject(export,
                Respond(HttpStatusCode.OK, NumeraiAuthOk),
                Respond(HttpStatusCode.OK, ""),
                Respond(HttpStatusCode.OK, @"{""data"":{""createSignalsSubmission"":{}},""errors"":[""late""]}"));

            Assert.IsFalse(export.Send(CreateParameters(algorithm, NumeraiSymbols.Select(s => new PortfolioTarget(s, 0.1m)))));
            StringAssert.Contains("Numerai API returned the following errors: late", Errors(algorithm).Single());
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(-0.5)]
        [TestCase(1.5)]
        public void NumeraiRejectsSignalsOutsideTheOpenUnitInterval(decimal invalidSignal)
        {
            var algorithm = CreateAlgorithm(new DateTime(2023, 03, 03));
            using var export = new NumeraiSignalExport("public", "secret", "model");
            var handler = Inject(export);

            var targets = NumeraiSymbols.Select(s => new PortfolioTarget(s, 0.1m)).ToList();
            targets[3] = new PortfolioTarget(targets[3].Symbol, invalidSignal);

            Assert.IsFalse(export.Send(CreateParameters(algorithm, targets)));
            Assert.IsEmpty(handler.Requests);
            StringAssert.Contains("All signals must be between 0 and 1 (exclusive)", Errors(algorithm).Single());
        }

        [Test]
        public void NumeraiReturnsFalseForEmptyTargets()
        {
            var algorithm = CreateAlgorithm(new DateTime(2023, 03, 03));
            using var export = new NumeraiSignalExport("public", "secret", "model");
            var handler = Inject(export);

            Assert.IsFalse(export.Send(CreateParameters(algorithm, Enumerable.Empty<PortfolioTarget>())));
            Assert.IsEmpty(handler.Requests);
            Assert.AreEqual("Portfolio target is empty", Debugs(algorithm).Single());
        }

        #endregion

        #region CrunchDAO

        [Test]
        public void CrunchDAOSendsPositionsWhenNoPreviousSubmissionExists()
        {
            var algorithm = CreateAlgorithm(new DateTime(2023, 03, 03), Symbols.SPY, Symbols.AAPL);
            using var export = new CrunchDAOSignalExport("key", "model", "label", "comment");
            var handler = Inject(export,
                Respond(HttpStatusCode.OK, @"{""id"":12}"),
                Respond(HttpStatusCode.OK, "[]"),
                Respond(HttpStatusCode.OK, "{}"));

            var result = export.Send(CreateParameters(algorithm, new PortfolioTarget(Symbols.SPY, 0.25m), new PortfolioTarget(Symbols.AAPL, 0.75m)));

            Assert.IsTrue(result, string.Join(",", Errors(algorithm)));
            Assert.AreEqual(3, handler.Requests.Count);
            Assert.AreEqual(HttpMethod.Get, handler.Requests[0].Method);
            Assert.AreEqual("https://api.tournament.crunchdao.com/v2/rounds/@current", handler.Requests[0].Uri.ToString());
            Assert.AreEqual(HttpMethod.Get, handler.Requests[1].Method);
            StringAssert.Contains("roundId=12", handler.Requests[1].Uri.ToString());
            StringAssert.Contains("apiKey=key", handler.Requests[1].Uri.ToString());

            var post = handler.Requests[2];
            Assert.AreEqual(HttpMethod.Post, post.Method);
            Assert.AreEqual("https://api.tournament.crunchdao.com/v3/alpha-submissions?apiKey=key", post.Uri.ToString());
            StringAssert.Contains("ticker,date,signal\nSPY,2023-03-03,0.25\nAAPL,2023-03-03,0.75\n", post.Body);
            StringAssert.Contains("submission.csv", post.Body);
            StringAssert.Contains("label", post.Body);
            StringAssert.Contains("comment", post.Body);
            Assert.IsEmpty(Errors(algorithm));
        }

        [Test]
        public void CrunchDAODeletesPreviousSubmissionBeforeSending()
        {
            var algorithm = CreateAlgorithm(new DateTime(2023, 03, 03), Symbols.SPY);
            using var export = new CrunchDAOSignalExport("key", "model");
            var handler = Inject(export,
                Respond(HttpStatusCode.OK, @"{""id"":3}"),
                Respond(HttpStatusCode.OK, @"[{""id"":77},{""id"":76}]"),
                Respond(HttpStatusCode.OK, "{}"),
                Respond(HttpStatusCode.OK, "{}"));

            Assert.IsTrue(export.Send(CreateParameters(algorithm, new PortfolioTarget(Symbols.SPY, 1m))));
            Assert.AreEqual(4, handler.Requests.Count);
            Assert.AreEqual(HttpMethod.Delete, handler.Requests[2].Method);
            StringAssert.Contains("alpha-submissions/77", handler.Requests[2].Uri.ToString());
            var debug = Debugs(algorithm).ToList();
            Assert.That(debug, Has.Some.Contains("You have already submitted a signal for round 3"));
            Assert.That(debug, Has.Some.EqualTo("Last submission has been deleted"));
        }

        [Test]
        public void CrunchDAOReturnsFalseWhenPreviousSubmissionCannotBeDeleted()
        {
            var algorithm = CreateAlgorithm(new DateTime(2023, 03, 03), Symbols.SPY);
            using var export = new CrunchDAOSignalExport("key", "model");
            var handler = Inject(export,
                Respond(HttpStatusCode.OK, @"{""id"":3}"),
                Respond(HttpStatusCode.OK, @"[{""id"":77}]"),
                Respond(HttpStatusCode.BadRequest, @"{""code"":""LOCKED_ROUND"",""message"":""round closed""}"));

            Assert.IsFalse(export.Send(CreateParameters(algorithm, new PortfolioTarget(Symbols.SPY, 0.5m))));
            Assert.AreEqual(3, handler.Requests.Count);
            StringAssert.Contains("code: LOCKED_ROUND message:round closed. Last submission could not be deleted", Errors(algorithm).Single());
        }

        [Test]
        public void CrunchDAOReturnsFalseWhenCurrentRoundIsNotFound()
        {
            var algorithm = CreateAlgorithm(new DateTime(2023, 03, 03), Symbols.SPY);
            using var export = new CrunchDAOSignalExport("key", "model");
            var handler = Inject(export, Respond(HttpStatusCode.NotFound, @"{""code"":""ROUND_NOT_FOUND"",""message"":""no round""}"));

            Assert.IsFalse(export.Send(CreateParameters(algorithm, new PortfolioTarget(Symbols.SPY, 0.5m))));
            Assert.AreEqual(1, handler.Requests.Count);
            StringAssert.Contains("CrunchDAO API returned code: ROUND_NOT_FOUND message:no round", Errors(algorithm).Single());
        }

        [Test]
        public void CrunchDAOReturnsFalseWhenCurrentRoundRequestFails()
        {
            var algorithm = CreateAlgorithm(new DateTime(2023, 03, 03), Symbols.SPY);
            using var export = new CrunchDAOSignalExport("key", "model");
            Inject(export, Respond(HttpStatusCode.InternalServerError, ""));

            Assert.IsFalse(export.Send(CreateParameters(algorithm, new PortfolioTarget(Symbols.SPY, 0.5m))));
            StringAssert.Contains("CrunchDAO API returned HttpRequestException InternalServerError", Errors(algorithm).Single());
        }

        [Test]
        public void CrunchDAOStillSendsWhenLastSubmissionLookupFails()
        {
            var algorithm = CreateAlgorithm(new DateTime(2023, 03, 03), Symbols.SPY);
            using var export = new CrunchDAOSignalExport("key", "model");
            var handler = Inject(export,
                Respond(HttpStatusCode.OK, @"{""id"":3}"),
                Respond(HttpStatusCode.ServiceUnavailable, ""),
                Respond(HttpStatusCode.OK, "{}"));

            Assert.IsTrue(export.Send(CreateParameters(algorithm, new PortfolioTarget(Symbols.SPY, 0.5m))));
            Assert.AreEqual(3, handler.Requests.Count);
            Assert.AreEqual(HttpMethod.Post, handler.Requests[2].Method);
            StringAssert.Contains("CrunchDAO API returned the following Error Code: ServiceUnavailable", Errors(algorithm).Single());
        }

        [TestCase(HttpStatusCode.Locked)]
        [TestCase(HttpStatusCode.Forbidden)]
        public void CrunchDAOReturnsFalseWhenSubmissionIsRejectedWithErrorPayload(HttpStatusCode statusCode)
        {
            var algorithm = CreateAlgorithm(new DateTime(2023, 03, 03), Symbols.SPY);
            using var export = new CrunchDAOSignalExport("key", "model");
            Inject(export,
                Respond(HttpStatusCode.OK, @"{""id"":3}"),
                Respond(HttpStatusCode.OK, "[]"),
                Respond(statusCode, @"{""code"":""ROUND_LOCKED"",""message"":""too late""}"));

            Assert.IsFalse(export.Send(CreateParameters(algorithm, new PortfolioTarget(Symbols.SPY, 0.5m))));
            StringAssert.Contains("CrunchDAO API returned code: ROUND_LOCKED message:too late", Errors(algorithm).Single());
        }

        [Test]
        public void CrunchDAOReturnsFalseWhenSubmissionFails()
        {
            var algorithm = CreateAlgorithm(new DateTime(2023, 03, 03), Symbols.SPY);
            using var export = new CrunchDAOSignalExport("key", "model");
            Inject(export,
                Respond(HttpStatusCode.OK, @"{""id"":3}"),
                Respond(HttpStatusCode.OK, "[]"),
                Respond(HttpStatusCode.BadGateway, ""));

            Assert.IsFalse(export.Send(CreateParameters(algorithm, new PortfolioTarget(Symbols.SPY, 0.5m))));
            StringAssert.Contains("CrunchDAO API returned HttpRequestException BadGateway", Errors(algorithm).Single());
        }

        [TestCase(-0.1)]
        [TestCase(1.1)]
        public void CrunchDAORejectsSignalsOutsideTheUnitInterval(decimal invalidSignal)
        {
            var algorithm = CreateAlgorithm(new DateTime(2023, 03, 03), Symbols.SPY);
            using var export = new CrunchDAOSignalExport("key", "model");
            var handler = Inject(export);

            Assert.IsFalse(export.Send(CreateParameters(algorithm, new PortfolioTarget(Symbols.SPY, invalidSignal))));
            Assert.IsEmpty(handler.Requests);
            StringAssert.Contains("All signals must be between 0 and 1", Errors(algorithm).Single());
        }

        #endregion

        #region vBase

        [TestCase(null, "collection")]
        [TestCase("", "collection")]
        [TestCase("   ", "collection")]
        [TestCase("key", null)]
        [TestCase("key", "")]
        [TestCase("key", " ")]
        public void VBaseConstructorValidatesArguments(string apiKey, string collection)
        {
            var exception = Assert.Throws<ArgumentException>(() => new VBaseSignalExport(apiKey, collection));
            StringAssert.StartsWith("vBaseSignalExport:", exception.Message);
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        public void VBaseStampsCsvWithBearerToken(bool storeStampedFile, bool idempotent)
        {
            var algorithm = CreateAlgorithm(new DateTime(2023, 03, 03));
            using var export = new VBaseSignalExport("vbase-key", "my-collection", storeStampedFile, idempotent);
            var handler = Inject(export, Respond(HttpStatusCode.OK, @"{""ok"":true}"));

            var result = export.Send(CreateParameters(algorithm, new PortfolioTarget(Symbols.SPY, 0.5m), new PortfolioTarget(Symbols.AAPL, -0.25m)));

            Assert.IsTrue(result, string.Join(",", Errors(algorithm)));
            var request = handler.Requests.Single();
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("https://app.vbase.com/api/v1/stamp/", request.Uri.ToString());
            Assert.AreEqual("Bearer vbase-key", request.Authorization);

            var form = ParseForm(request.Body);
            Assert.AreEqual("sym,wt\nSPY,0.5\nAAPL,-0.25\n", form["data"]);
            Assert.AreEqual(storeStampedFile.ToString(), form["storeStampedFile"]);
            Assert.AreEqual(idempotent.ToString(), form["idempotent"]);
            Assert.That(form["collectionCid"], Does.Match("^0x[0-9a-f]{64}$"));
        }

        [Test]
        public void VBaseCollectionCidIsDeterministicPerCollectionName()
        {
            string GetCid(string collection)
            {
                var algorithm = CreateAlgorithm(new DateTime(2023, 03, 03));
                using var export = new VBaseSignalExport("key", collection);
                var handler = Inject(export, Respond(HttpStatusCode.OK, "{}"));
                export.Send(CreateParameters(algorithm, new PortfolioTarget(Symbols.SPY, 1m)));
                return ParseForm(handler.Requests.Single().Body)["collectionCid"];
            }

            Assert.AreEqual(GetCid("alpha"), GetCid("alpha"));
            Assert.AreNotEqual(GetCid("alpha"), GetCid("beta"));
        }

        [Test]
        public void VBaseReturnsFalseAndLogsBodyOnErrorResponse()
        {
            var algorithm = CreateAlgorithm(new DateTime(2023, 03, 03));
            using var export = new VBaseSignalExport("key", "collection");
            Inject(export, Respond(HttpStatusCode.BadRequest, "invalid collection"));

            Assert.IsFalse(export.Send(CreateParameters(algorithm, new PortfolioTarget(Symbols.SPY, 1m))));
            Assert.AreEqual("vBase API returned BadRequest. Body: invalid collection", Errors(algorithm).Single());
        }

        [Test]
        public void VBaseReturnsFalseWhenRequestThrows()
        {
            var algorithm = CreateAlgorithm(new DateTime(2023, 03, 03));
            using var export = new VBaseSignalExport("key", "collection");
            Inject(export, _ => throw new HttpRequestException("connection refused"));

            Assert.IsFalse(export.Send(CreateParameters(algorithm, new PortfolioTarget(Symbols.SPY, 1m))));
            var error = Errors(algorithm).Single();
            StringAssert.StartsWith("vBase signal export failed:", error);
        }

        [Test]
        public void VBaseRejectsUnsupportedSecurityTypesAndEmptyTargets()
        {
            var algorithm = CreateAlgorithm(new DateTime(2023, 03, 03));
            using var export = new VBaseSignalExport("key", "collection");
            var handler = Inject(export);

            Assert.IsFalse(export.Send(CreateParameters(algorithm, Enumerable.Empty<PortfolioTarget>())));
            Assert.IsFalse(export.Send(CreateParameters(algorithm, new PortfolioTarget(Symbols.SPX, 1m))));
            Assert.IsEmpty(handler.Requests);
            var debug = Debugs(algorithm).ToList();
            Assert.AreEqual("Portfolio target is empty", debug[0]);
            StringAssert.StartsWith("Index security type is not supported by vBase", debug[1]);
        }

        [Test]
        public void VBaseUsesOverriddenCsvBuilder()
        {
            var algorithm = CreateAlgorithm(new DateTime(2023, 03, 03));
            using var export = new CustomCsvVBaseSignalExport();
            var handler = Inject(export, Respond(HttpStatusCode.OK, "{}"));

            Assert.IsTrue(export.Send(CreateParameters(algorithm, new PortfolioTarget(Symbols.SPY, 1m))));
            Assert.AreEqual("custom,1", ParseForm(handler.Requests.Single().Body)["data"]);
        }

        #endregion

        #region Collective2

        [Test]
        public void Collective2SendsDesiredPositionsAndLogsNewSignals()
        {
            var algorithm = CreateCollective2Algorithm();
            using var export = new Collective2SignalExport("c2-key", 1234);
            var handler = Inject(export, Respond(HttpStatusCode.OK,
                @"{""Results"":[{""NewSignals"":[11,12],""CanceledSignals"":[13]}],""ResponseStatus"":{""ErrorCode"":""200"",""Message"":""OK"",""Errors"":[]}}"));

            Assert.IsTrue(export.Send(CreateParameters(algorithm, new PortfolioTarget(Symbols.SPY, 0.2m))));
            var request = handler.Requests.Single();
            Assert.AreEqual("https://api4-general.collective2.com/Strategies/SetDesiredPositions", request.Uri.ToString());
            Assert.AreEqual("Bearer c2-key", request.Authorization);
            StringAssert.Contains(@"""StrategyId"":1234", request.Body);
            StringAssert.Contains(@"""symbol"":""SPY""", request.Body);
            StringAssert.StartsWith("Collective2: NewSignals=11,12 | CanceledSignals=13", Debugs(algorithm).Last());
        }

        [Test]
        public void Collective2UsesWhiteLabelDestinationAndAcceptsEmptyResults()
        {
            var algorithm = CreateCollective2Algorithm();
            using var export = new Collective2SignalExport("c2-key", 1, useWhiteLabelApi: true);
            var handler = Inject(export, Respond(HttpStatusCode.OK, @"{""Results"":[]}"));

            Assert.IsTrue(export.Send(CreateParameters(algorithm, new PortfolioTarget(Symbols.SPY, 0.2m))));
            Assert.AreEqual("https://api4-wl.collective2.com/Strategies/SetDesiredPositions", handler.Requests.Single().Uri.ToString());
            Assert.IsFalse(Debugs(algorithm).Any(x => x.StartsWith("Collective2: NewSignals", StringComparison.InvariantCulture)));
        }

        [Test]
        public void Collective2ReturnsFalseAndLogsErrorsOnFailure()
        {
            var algorithm = CreateCollective2Algorithm();
            using var export = new Collective2SignalExport("c2-key", 1);
            Inject(export, Respond(HttpStatusCode.BadRequest,
                @"{""Results"":[],""ResponseStatus"":{""ErrorCode"":""400"",""Message"":""Bad"",""Errors"":[{""ErrorCode"":""E1"",""FieldName"":""Quantity"",""Message"":""too big""}]}}"));

            Assert.IsFalse(export.Send(CreateParameters(algorithm, new PortfolioTarget(Symbols.SPY, 0.2m))));
            StringAssert.Contains("Collective2 API returned the following errors: (E1) Quantity: too big", Errors(algorithm).Single());
        }

        [Test]
        public void Collective2ReturnsNullErrorsMarkerWhenErrorListIsEmpty()
        {
            var algorithm = CreateCollective2Algorithm();
            using var export = new Collective2SignalExport("c2-key", 1);
            Inject(export, Respond(HttpStatusCode.Unauthorized, @"{""ResponseStatus"":{""ErrorCode"":""401"",""Errors"":[]}}"));

            Assert.IsFalse(export.Send(CreateParameters(algorithm, new PortfolioTarget(Symbols.SPY, 0.2m))));
            StringAssert.Contains("Collective2 API returned the following errors: NULL", Errors(algorithm).Single());
        }

        [Test]
        [Ignore("Possible bug: BaseSignalExport.VerifyTargets dereferences a null PortfolioTarget, so Collective2SignalExport.Send throws NullReferenceException instead of logging 'One portfolio target was null'")]
        public void Collective2ReturnsFalseWhenATargetIsNull()
        {
            var algorithm = CreateCollective2Algorithm();
            using var export = new Collective2SignalExport("c2-key", 1);
            var handler = Inject(export);

            Assert.IsFalse(export.Send(CreateParameters(algorithm, new PortfolioTarget(Symbols.SPY, 0.2m), null)));
            Assert.IsEmpty(handler.Requests);
            Assert.AreEqual("One portfolio target was null", Errors(algorithm).Single());
        }

        #endregion

        #region SignalExportManager

        [Test]
        public void ManagerDoesNotSendOutsideLiveModeAndLogsOnlyOnce()
        {
            var algorithm = new AlgorithmStub();
            var manager = new SignalExportManager(algorithm);
            var provider = new RecordingSignalExport(true);
            manager.AddSignalExportProvider(provider);

            Assert.IsTrue(manager.SetTargetPortfolio(new PortfolioTarget(Symbols.SPY, 1m)));
            Assert.IsTrue(manager.SetTargetPortfolio(new PortfolioTarget(Symbols.SPY, 1m)));

            Assert.AreEqual(0, provider.Calls.Count);
            Assert.AreEqual(1, Debugs(algorithm).Count(x => x == "Portfolio targets are only sent in live mode"));
        }

        [Test]
        public void ManagerReturnsFalseInLiveModeWithoutProviders()
        {
            var algorithm = CreateLiveAlgorithm();
            var manager = new SignalExportManager(algorithm);

            Assert.IsFalse(manager.SetTargetPortfolio(new PortfolioTarget(Symbols.SPY, 1m)));
        }

        [Test]
        public void ManagerReturnsFalseWhenNoTargetsAreGiven()
        {
            var algorithm = CreateLiveAlgorithm();
            var manager = new SignalExportManager(algorithm);
            var provider = new RecordingSignalExport(true);
            manager.AddSignalExportProvider(provider);

            Assert.IsFalse(manager.SetTargetPortfolio());
            Assert.IsFalse(manager.SetTargetPortfolio(null));
            Assert.AreEqual(0, provider.Calls.Count);
            Assert.AreEqual(2, Debugs(algorithm).Count(x => x == "No portfolio target given"));
        }

        [TestCase(true, true, true)]
        [TestCase(true, false, false)]
        [TestCase(false, false, false)]
        public void ManagerSendsToEveryProviderAndCombinesResults(bool firstResult, bool secondResult, bool expected)
        {
            var algorithm = CreateLiveAlgorithm();
            var manager = new SignalExportManager(algorithm);
            var first = new RecordingSignalExport(firstResult);
            var second = new RecordingSignalExport(secondResult);
            manager.AddSignalExportProviders(first, second);

            var target = new PortfolioTarget(Symbols.SPY, 0.5m);
            Assert.AreEqual(expected, manager.SetTargetPortfolio(target));

            foreach (var provider in new[] { first, second })
            {
                var parameters = provider.Calls.Single();
                Assert.AreSame(algorithm, parameters.Algorithm);
                Assert.AreSame(target, parameters.Targets.Single());
            }
        }

        [Test]
        public void ManagerSetsTargetsFromPortfolioHoldings()
        {
            var algorithm = CreateLiveAlgorithm();
            var security = algorithm.AddSecurity(Symbols.SPY);
            security.SetMarketPrice(new Tick(new DateTime(2022, 01, 04), security.Symbol, 100m, 100m));
            security.Holdings.SetHoldings(100m, -100);

            var manager = new SignalExportManager(algorithm);
            var provider = new RecordingSignalExport(true);
            manager.AddSignalExportProvider(provider);

            Assert.IsTrue(manager.SetTargetPortfolioFromPortfolio());
            var target = provider.Calls.Single().Targets.Single();
            Assert.AreEqual(Symbols.SPY, target.Symbol);
            Assert.Less(target.Quantity, 0m);
        }

        [Test]
        public void ManagerSetTargetPortfolioFromPortfolioFailsWithNonPositivePortfolioValue()
        {
            var algorithm = CreateLiveAlgorithm();
            algorithm.SetCash(0);
            var manager = new SignalExportManager(algorithm);
            var provider = new RecordingSignalExport(true);
            manager.AddSignalExportProvider(provider);

            Assert.IsFalse(manager.SetTargetPortfolioFromPortfolio());
            Assert.AreEqual(0, provider.Calls.Count);
            Assert.AreEqual("Total portfolio value was less than or equal to 0", Errors(algorithm).Single());
        }

        [Test]
        public void ManagerFlushExportsOnlyAfterTheAutomaticExportTimeSpanHasElapsed()
        {
            var algorithm = CreateLiveAlgorithm();
            AddHolding(algorithm, Symbols.SPY, 10);
            var manager = new SignalExportManager(algorithm);
            var provider = new RecordingSignalExport(true);
            manager.AddSignalExportProvider(provider);
            var start = new DateTime(2022, 01, 04, 15, 0, 0);

            // no fill yet: nothing to export
            manager.Flush(start.AddHours(1));
            Assert.AreEqual(0, provider.Calls.Count);

            // a non-fill event does not start the timer
            manager.OnOrderEvent(CreateOrderEvent(start, OrderStatus.Submitted));
            manager.Flush(start.AddHours(1));
            Assert.AreEqual(0, provider.Calls.Count);

            manager.OnOrderEvent(CreateOrderEvent(start, OrderStatus.Filled));
            // a later fill does not move the initial time
            manager.OnOrderEvent(CreateOrderEvent(start.AddSeconds(3), OrderStatus.PartiallyFilled));

            manager.Flush(start.AddSeconds(4));
            Assert.AreEqual(0, provider.Calls.Count);

            manager.Flush(start.AddSeconds(5));
            Assert.AreEqual(1, provider.Calls.Count);

            // the timer was reset
            manager.Flush(start.AddSeconds(30));
            Assert.AreEqual(1, provider.Calls.Count);
        }

        [Test]
        public void ManagerFlushDoesNothingWhenAutomaticExportIsDisabled()
        {
            var algorithm = CreateLiveAlgorithm();
            var manager = new SignalExportManager(algorithm) { AutomaticExportTimeSpan = null };
            var provider = new RecordingSignalExport(true);
            manager.AddSignalExportProvider(provider);
            var start = new DateTime(2022, 01, 04, 15, 0, 0);

            manager.OnOrderEvent(CreateOrderEvent(start, OrderStatus.Filled));
            manager.Flush(start.AddDays(1));

            Assert.AreEqual(0, provider.Calls.Count);
        }

        [Test]
        public void ManagerFlushLogsProviderExceptions()
        {
            var algorithm = CreateLiveAlgorithm();
            AddHolding(algorithm, Symbols.SPY, 10);
            var manager = new SignalExportManager(algorithm) { AutomaticExportTimeSpan = TimeSpan.Zero };
            manager.AddSignalExportProvider(new RecordingSignalExport(true, new InvalidOperationException("provider down")));
            var start = new DateTime(2022, 01, 04, 15, 0, 0);

            manager.OnOrderEvent(CreateOrderEvent(start, OrderStatus.Filled));
            Assert.DoesNotThrow(() => manager.Flush(start));

            StringAssert.StartsWith("Failed to send portfolio target(s). Reason: provider down.", Errors(algorithm).Single());
        }

        [Test]
        public void ManagerAcceptsPythonProviders()
        {
            PythonInitializer.Initialize();
            var algorithm = CreateLiveAlgorithm();
            var manager = new SignalExportManager(algorithm);

            using (Py.GIL())
            {
                using var module = PyModule.FromString("SignalExportsCoverageTests", @"
class Provider:
    def __init__(self, result):
        self.calls = 0
        self.result = result
    def send(self, parameters):
        self.calls += 1
        return self.result
    def dispose(self):
        pass

single = Provider(True)
listed = [Provider(True), Provider(True)]
");
                manager.AddSignalExportProvider(module.GetAttr("single"));
                manager.AddSignalExportProviders(module.GetAttr("listed"));

                Assert.IsTrue(manager.SetTargetPortfolio(new PortfolioTarget(Symbols.SPY, 0.5m)));

                Assert.AreEqual(1, module.GetAttr("single").GetAttr("calls").As<int>());
                using var listed = module.GetAttr("listed");
                for (var i = 0; i < 2; i++)
                {
                    Assert.AreEqual(1, listed[i].GetAttr("calls").As<int>());
                }
            }
        }

        #endregion

        private static List<string> Errors(QCAlgorithm algorithm) => StripTimestamps(algorithm.ErrorMessages);

        private static List<string> Debugs(QCAlgorithm algorithm) => StripTimestamps(algorithm.DebugMessages);

        private static List<string> StripTimestamps(IEnumerable<string> messages)
        {
            // messages are prefixed with the algorithm time as "yyyy-MM-dd HH:mm:ss "
            return messages.Select(x => x.Length > 20 && x[4] == '-' && x[19] == ' ' ? x.Substring(20) : x).ToList();
        }

        private static void AddHolding(QCAlgorithm algorithm, Symbol symbol, decimal quantity)
        {
            var security = algorithm.Securities.ContainsKey(symbol) ? algorithm.Securities[symbol] : algorithm.AddSecurity(symbol);
            security.SetMarketPrice(new Tick(new DateTime(2022, 01, 04), symbol, 100m, 100m));
            security.Holdings.SetHoldings(100m, quantity);
        }

        private static AlgorithmStub CreateAlgorithm(DateTime time, params Symbol[] symbols)
        {
            var algorithm = new AlgorithmStub();
            algorithm.SetDateTime(time.ConvertToUtc(TimeZones.NewYork));
            foreach (var symbol in symbols)
            {
                var security = algorithm.AddSecurity(symbol);
                security.SetMarketPrice(new Tick { Value = 100 });
            }
            return algorithm;
        }

        private static AlgorithmStub CreateCollective2Algorithm()
        {
            var algorithm = new AlgorithmStub();
            algorithm.SetDateTime(new DateTime(2016, 02, 16, 11, 53, 30));
            var security = algorithm.AddSecurity(Symbols.SPY);
            security.FeeModel = new ConstantFeeModel(0);
            security.SetMarketPrice(new Tick { Value = 100 });
            algorithm.Portfolio.SetCash(1500000);
            return algorithm;
        }

        private static AlgorithmStub CreateLiveAlgorithm()
        {
            var algorithm = new AlgorithmStub();
            algorithm.SetLiveMode(true);
            algorithm.SetFinishedWarmingUp();
            algorithm.SetCash(100000);
            return algorithm;
        }

        private static OrderEvent CreateOrderEvent(DateTime utcTime, OrderStatus status)
        {
            return new OrderEvent(1, Symbols.SPY, utcTime, status, OrderDirection.Buy, 100, 1, new OrderFee(new CashAmount(1, Currencies.USD)));
        }

        private static SignalExportTargetParameters CreateParameters(IAlgorithm algorithm, IEnumerable<PortfolioTarget> targets)
        {
            return new SignalExportTargetParameters { Algorithm = algorithm, Targets = targets.ToList() };
        }

        private static SignalExportTargetParameters CreateParameters(IAlgorithm algorithm, params PortfolioTarget[] targets)
        {
            return CreateParameters(algorithm, (IEnumerable<PortfolioTarget>)targets);
        }

        private static Dictionary<string, string> ParseForm(string body)
        {
            return body.Split('&').Select(pair => pair.Split('=', 2))
                .ToDictionary(kv => WebUtility.UrlDecode(kv[0]), kv => WebUtility.UrlDecode(kv[1]));
        }

        private static Func<HttpRequestMessage, HttpResponseMessage> Respond(HttpStatusCode statusCode, string body)
        {
            return _ => new HttpResponseMessage(statusCode) { Content = new StringContent(body) };
        }

        private static FakeHttpMessageHandler Inject(BaseSignalExport export, params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        {
            var handler = new FakeHttpMessageHandler(responses);
            var field = typeof(BaseSignalExport).GetField("_lazyClient", BindingFlags.NonPublic | BindingFlags.Instance);
            field.SetValue(export, new Lazy<HttpClient>(() => new HttpClient(handler)));
            return handler;
        }

        private record RecordedRequest(HttpMethod Method, Uri Uri, string Authorization, string Body);

        private sealed class FakeHttpMessageHandler : HttpMessageHandler
        {
            private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;

            public List<RecordedRequest> Requests { get; } = new();

            public FakeHttpMessageHandler(IEnumerable<Func<HttpRequestMessage, HttpResponseMessage>> responses)
            {
                _responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responses);
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                Requests.Add(new RecordedRequest(request.Method, request.RequestUri, request.Headers.Authorization?.ToString()
                    ?? request.Headers.NonValidated.Where(x => x.Key == "Authorization").Select(x => x.Value.ToString()).FirstOrDefault(), body));
                if (_responses.Count == 0)
                {
                    throw new InvalidOperationException($"Unexpected request {request.Method} {request.RequestUri}");
                }
                return _responses.Dequeue()(request);
            }
        }

        private sealed class RecordingSignalExport : ISignalExportTarget
        {
            private readonly bool _result;
            private readonly Exception _exception;

            public List<SignalExportTargetParameters> Calls { get; } = new();

            public RecordingSignalExport(bool result, Exception exception = null)
            {
                _result = result;
                _exception = exception;
            }

            public bool Send(SignalExportTargetParameters parameters)
            {
                Calls.Add(parameters);
                if (_exception != null)
                {
                    throw _exception;
                }
                return _result;
            }

            public void Dispose()
            {
            }
        }

        private sealed class CustomCsvVBaseSignalExport : VBaseSignalExport
        {
            public CustomCsvVBaseSignalExport() : base("key", "custom")
            {
            }

            protected override string BuildCsv(SignalExportTargetParameters parameters)
            {
                return $"custom,{parameters.Targets.Count}";
            }
        }
    }
}
