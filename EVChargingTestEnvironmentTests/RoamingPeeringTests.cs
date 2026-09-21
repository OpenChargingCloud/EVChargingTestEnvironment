/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of the EV charging test environment
 * <https://github.com/OpenChargingCloud/EVChargingTestEnvironment>
 *
 * Licensed under the Affero GPL license, Version 3.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.gnu.org/licenses/agpl.html
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using Newtonsoft.Json.Linq;

using NUnit.Framework;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests
{

    /// <summary>
    /// An OCPI peering between a real charge point operator and a real
    /// e-mobility service provider, started from either end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The CPO is the CSMS of this environment and the EMSP is its EMSP -
    /// both the classes their own repositories ship, on ports of their own,
    /// talking to each other over HTTP. That is the whole point of this test
    /// project: the CSMS repository has no EMSP beside it and can only test
    /// against a stub, the EMSP repository has no CSMS, and a stub agrees
    /// with whatever it is sent.
    /// </para>
    /// <para>
    /// An OCPI registration is symmetric in what it achieves and asymmetric
    /// in who starts it, and both halves are here. Either way it ends in the
    /// same place: each side holds a token of the other's and the URL to
    /// present it at, and both call the peering registered. What differs is
    /// only who walks to whom.
    /// </para>
    /// <para>
    /// Driven through the two components' own methods rather than over their
    /// JSON APIs: signing a browser in to each of them would test the web
    /// interfaces, which their own repositories already do. What travels
    /// between the two - the versions, the version details, the credentials -
    /// goes over real sockets either way.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class RoamingPeeringTests
    {

        #region Data

        private CSMS.CSMS  cpo        = default!;
        private EMSP.EMSP  emsp       = default!;

        private String     cpoPath    = default!;
        private String     emspPath   = default!;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public async Task StartBothEnds()
        {

            cpoPath   = TestParties.TemporaryDirectory("cpo");
            emspPath  = TestParties.TemporaryDirectory("emsp");

            cpo       = TestParties.NewCPO (cpoPath);
            emsp      = TestParties.NewEMSP(emspPath);

            await cpo. Start();
            await emsp.Start();

        }

        [TearDown]
        public async Task StopBothEnds()
        {

            if (cpo  is not null)  await cpo. DisposeAsync();
            if (emsp is not null)  await emsp.DisposeAsync();

            TestParties.Remove(cpoPath);
            TestParties.Remove(emspPath);

        }

        #endregion


        #region (private) The two halves of adding a partner

        /// <summary>
        /// The EMSP learns about the CPO. With a token and nothing else it is
        /// the CPO that has to come; with the CPO's token and versions URL as
        /// well the EMSP could go there itself.
        /// </summary>
        private async Task<String> TheEMSPAdds(String?  CPOToken     = null,
                                               String?  VersionsURL  = null)
        {

            var body = new JObject(
                           new JProperty("version",      "2.2.1"),
                           new JProperty("countryCode",  cpo.PartyId.CountryCode.ToString()),
                           new JProperty("partyId",      cpo.PartyId.PartyId.    ToString()),
                           new JProperty("role",         "CPO"),
                           new JProperty("name",         cpo.BusinessDetails.Name)
                       );

            if (CPOToken    is not null)  body.Add("theirToken",  CPOToken);
            if (VersionsURL is not null)  body.Add("versionsURL", VersionsURL);

            var result = await emsp.AddRemotePartyAsync(body);

            Assert.That(result.Success, Is.True, $"The EMSP would not take the CPO as a partner: {result.Message}");

            return result.Data!.Value<String>("ourToken")!;

        }

        /// <summary>
        /// The CPO learns about the EMSP, the same two ways round.
        /// </summary>
        private async Task<String> TheCPOAdds(String?  EMSPToken    = null,
                                              String?  VersionsURL  = null)
        {

            var body = new JObject(
                           new JProperty("version",      "2.2.1"),
                           new JProperty("countryCode",  emsp.PartyId.CountryCode.ToString()),
                           new JProperty("partyId",      emsp.PartyId.PartyId.    ToString()),
                           new JProperty("role",         "EMSP"),
                           new JProperty("name",         emsp.BusinessDetails.Name)
                       );

            if (EMSPToken   is not null)  body.Add("theirToken",  EMSPToken);
            if (VersionsURL is not null)  body.Add("versionsURL", VersionsURL);

            var result = await cpo.AddRemotePartyAsync(body);

            Assert.That(result.Success, Is.True, $"The CPO would not take the EMSP as a partner: {result.Message}");

            return result.Data!.Value<String>("ourToken")!;

        }

        #endregion

        #region (private) Story()

        /// <summary>
        /// What both ends said while they were talking, oldest first.
        /// </summary>
        /// <remarks>
        /// Put into the message of every assertion that can only fail because
        /// something went wrong between the two: a peering that did not happen
        /// is a sequence of requests, and the sentence "it did not go through"
        /// on its own sends whoever reads it looking for a log neither
        /// component wrote to disk.
        /// </remarks>
        private String Story()

            => String.Join(
                   Environment.NewLine,
                   new[] { "", "What the two said:" }.
                       Concat(cpo. Log.Recent(200).Select(entry => $"  CPO  {entry.Timestamp:HH:mm:ss.fff} [{entry.LevelName}] {entry.Message}")).
                       Concat(emsp.Log.Recent(200).Select(entry => $"  EMSP {entry.Timestamp:HH:mm:ss.fff} [{entry.LevelName}] {entry.Message}"))
               );

        #endregion

        #region (private) What each end holds afterwards

        private CSMS.OCPI.RemotePartySummary CPOsViewOfTheEMSP()
        {

            var partner = cpo.OCPIVersions.
                              SelectMany(version => version.RemoteParties).
                              FirstOrDefault(party => party.PartyId == emsp.PartyId.PartyId);

            Assert.That(partner, Is.Not.Null, "The CPO does not know the EMSP at all.");

            return partner!;

        }

        private EMSP.OCPI.RemotePartySummary EMSPsViewOfTheCPO()
        {

            var partner = emsp.OCPIVersions.
                               SelectMany(version => version.RemoteParties).
                               FirstOrDefault(party => party.PartyId == cpo.PartyId.PartyId);

            Assert.That(partner, Is.Not.Null, "The EMSP does not know the CPO at all.");

            return partner!;

        }

        #endregion

        #region TheCPOStartsThePeering()

        /// <summary>
        /// The CPO walks to the EMSP: the EMSP hands out a token, the CPO is
        /// told that token and where the EMSP's versions are, and goes - GET
        /// versions, GET the version details, POST its credentials.
        /// </summary>
        [Test]
        public async Task TheCPOStartsThePeering()
        {

            // The EMSP has a token ready for the CPO and expects to be called.
            var emspToken = await TheEMSPAdds();

            // The CPO is told that token and where to present it.
            var cpoToken  = await TheCPOAdds(
                                      EMSPToken:    emspToken,
                                      VersionsURL:  emsp.OCPIVersionsURL.ToString()
                                  );

            Assert.Multiple(() => {
                Assert.That(CPOsViewOfTheEMSP().CanRegister, Is.True,  "The CPO holds a token and a URL and does not think it can go there.");
                Assert.That(CPOsViewOfTheEMSP().Registered,  Is.False, "Nothing has happened yet and the CPO calls the peering complete.");
                Assert.That(EMSPsViewOfTheCPO().Registered,  Is.False);
            });

            var result = await cpo.RegisterRemotePartyAsync("2.2.1", CPOsViewOfTheEMSP().Id.ToString());

            Assert.That(result.Success, Is.True, $"The CPO could not register with the EMSP: {result.Message}{Story()}");

            AssertBothEndsArePeered(TokenTheCPOCameInWith: cpoToken);

        }

        #endregion

        #region TheEMSPStartsThePeering()

        /// <summary>
        /// The same peering the other way round, which is the more common
        /// one: the CPO hands out a token, the EMSP is told that token and
        /// where the CPO's versions are, and goes.
        /// </summary>
        [Test]
        public async Task TheEMSPStartsThePeering()
        {

            // The CPO has a token ready for the EMSP and expects to be called.
            var cpoToken   = await TheCPOAdds();

            // The EMSP is told that token and where to present it.
            var emspToken  = await TheEMSPAdds(
                                       CPOToken:     cpoToken,
                                       VersionsURL:  cpo.OCPIVersionsURL.ToString()
                                   );

            Assert.Multiple(() => {
                Assert.That(EMSPsViewOfTheCPO().CanRegister, Is.True,  "The EMSP holds a token and a URL and does not think it can go there.");
                Assert.That(EMSPsViewOfTheCPO().Registered,  Is.False, "Nothing has happened yet and the EMSP calls the peering complete.");
                Assert.That(CPOsViewOfTheEMSP().Registered,  Is.False);
            });

            var result = await emsp.RegisterRemotePartyAsync("2.2.1", EMSPsViewOfTheCPO().Id.ToString());

            Assert.That(result.Success, Is.True, $"The EMSP could not register with the CPO: {result.Message}{Story()}");

            AssertBothEndsArePeered(TokenTheEMSPCameInWith: emspToken);

        }

        #endregion

        #region (private) AssertBothEndsArePeered(...)

        /// <summary>
        /// Where both directions end up, which is the same place: each side
        /// holds a token of the other's and a URL to present it at, each side
        /// calls the peering registered, and the two tokens match across the
        /// wire.
        /// </summary>
        /// <remarks>
        /// The token whoever started it came in with is spent: the credentials
        /// exchange replaces it with a fresh one, which is what OCPI asks for
        /// and what stops a token handed over by hand from being a key that
        /// keeps working.
        /// </remarks>
        private void AssertBothEndsArePeered(String? TokenTheCPOCameInWith    = null,
                                             String? TokenTheEMSPCameInWith   = null)
        {

            var atTheCPO   = CPOsViewOfTheEMSP();
            var atTheEMSP  = EMSPsViewOfTheCPO();

            Assert.Multiple(() => {

                Assert.That(atTheCPO. Registered,  Is.True, "The CPO does not call the peering complete.");
                Assert.That(atTheEMSP.Registered,  Is.True, "The EMSP does not call the peering complete.");

                // What one side hands out is what the other presents, in both
                // directions. This is the assertion a stub cannot make: it is
                // two real stores agreeing.
                Assert.That(atTheCPO. OurToken?.  ToString(),
                            Is.EqualTo(atTheEMSP.TheirToken?.ToString()),
                            "The token the CPO expects from the EMSP is not the one the EMSP would present.");

                Assert.That(atTheEMSP.OurToken?.  ToString(),
                            Is.EqualTo(atTheCPO. TheirToken?.ToString()),
                            "The token the EMSP expects from the CPO is not the one the CPO would present.");

                // And each side knows where to reach the other.
                Assert.That(atTheCPO. TheirVersionsURL?.ToString(), Is.EqualTo(emsp.OCPIVersionsURL.ToString()));
                Assert.That(atTheEMSP.TheirVersionsURL?.ToString(), Is.EqualTo(cpo. OCPIVersionsURL.ToString()));

                // The version the two settled on.
                Assert.That(atTheCPO. SelectedVersion?.ToString(), Is.EqualTo("2.2.1"));
                Assert.That(atTheEMSP.SelectedVersion?.ToString(), Is.EqualTo("2.2.1"));

                if (TokenTheCPOCameInWith is not null)
                    Assert.That(atTheCPO.OurToken?.ToString(), Is.Not.EqualTo(TokenTheCPOCameInWith),
                                "The token handed over by hand is still the CPO's after the registration replaced it.");

                if (TokenTheEMSPCameInWith is not null)
                    Assert.That(atTheEMSP.OurToken?.ToString(), Is.Not.EqualTo(TokenTheEMSPCameInWith),
                                "The token handed over by hand is still the EMSP's after the registration replaced it.");

            });

        }

        #endregion

    }

}
