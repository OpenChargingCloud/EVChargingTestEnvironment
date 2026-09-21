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

#endregion

namespace cloud.charging.open.TestEnvironment
{

    public sealed partial class ChargingTestEnvironment
    {

        #region Data

        /// <summary>
        /// The OCPI version the two are peered on.
        /// </summary>
        /// <remarks>
        /// One version rather than all of them, because a roaming partner is
        /// on exactly one: the one it was added under. 2.2.1 is the newest
        /// both sides offer by default, and the one a partner would pick for
        /// itself.
        /// </remarks>
        private const String OCPIVersion = "2.2.1";

        #endregion


        #region (private) PeerTheOperatorWithTheProvider()

        /// <summary>
        /// Introduce the CSMS and the EMSP to each other over OCPI, and let
        /// the operator start the peering.
        /// </summary>
        /// <remarks>
        /// <para>
        /// After both have started, and it has to be exactly there: a
        /// registration is HTTP in both directions - the operator fetches the
        /// provider's versions, POSTs its credentials, and the provider calls
        /// back to the URL in them before it answers - so both ports have to
        /// be open before either of them walks.
        /// </para>
        /// <para>
        /// Which way round is a coin toss that OCPI lets either side win, and
        /// the operator is the one that walks here for the same reason it is
        /// the operator that hands out logins everywhere else in this file:
        /// somebody has to, and this environment is the somebody.
        /// </para>
        /// <para>
        /// Idempotent. The OCPI library keeps its roaming partners in files
        /// below each component's directory and reads them back at every
        /// start, so a second start finds the two already peered and leaves
        /// them alone - a registration is not something to repeat for the fun
        /// of it, and repeating it would replace two tokens that are working.
        /// </para>
        /// </remarks>
        private async Task PeerTheOperatorWithTheProvider()
        {

            if (EMSP is null)
                return;

            #region Already peered? Then nothing to do.

            var operatorSees  = CSMS.OCPIVersions.SelectMany(version => version.RemoteParties).
                                     FirstOrDefault(party => party.PartyId == EMSP.PartyId.PartyId);

            var providerSees  = EMSP.OCPIVersions.SelectMany(version => version.RemoteParties).
                                     FirstOrDefault(party => party.PartyId == CSMS.PartyId.PartyId);

            if (operatorSees is not null && providerSees is not null)
            {

                Console.Say(
                    "env",
                    operatorSees.Registered
                        ? $"OCPI: {CSMS.PartyIdText} and {EMSP.PartyIdText} are peered from an earlier start."
                        : $"OCPI: {CSMS.PartyIdText} and {EMSP.PartyIdText} know each other from an earlier start, " +
                          $"and the registration has not gone through. Start it again under Roaming partners."
                );

                return;

            }

            #endregion

            #region One of the two knows the other and the other does not

            // Half a peering, from a start that was interrupted between the
            // two lines below. Nothing here can mend it without deciding
            // which of two tokens to throw away, so it says so and stops.
            if (operatorSees is not null || providerSees is not null)
            {

                Console.Say(
                    "env",
                    $"OCPI: only {(operatorSees is not null ? CSMS.PartyIdText : EMSP.PartyIdText)} knows the other, " +
                    $"which is half a peering from an earlier start. Remove that partner under Roaming partners and start again."
                );

                return;

            }

            #endregion

            #region The provider hands out a token, and expects to be called

            var atTheProvider = await EMSP.AddRemotePartyAsync(
                                          new JObject(
                                              new JProperty("version",      OCPIVersion),
                                              new JProperty("countryCode",  CSMS.PartyId.CountryCode.ToString()),
                                              new JProperty("partyId",      CSMS.PartyId.PartyId.    ToString()),
                                              new JProperty("role",         "CPO"),
                                              new JProperty("name",         CSMS.BusinessDetails.Name)
                                          )
                                      );

            if (!atTheProvider.Success)
            {
                Console.Say("env", $"OCPI: the EMSP would not take the CSMS as a roaming partner: {atTheProvider.Message}");
                return;
            }

            #endregion

            #region The operator is told that token and where to present it

            var atTheOperator = await CSMS.AddRemotePartyAsync(
                                          new JObject(
                                              new JProperty("version",      OCPIVersion),
                                              new JProperty("countryCode",  EMSP.PartyId.CountryCode.ToString()),
                                              new JProperty("partyId",      EMSP.PartyId.PartyId.    ToString()),
                                              new JProperty("role",         "EMSP"),
                                              new JProperty("name",         EMSP.BusinessDetails.Name),
                                              new JProperty("theirToken",   atTheProvider.Data!.Value<String>("ourToken")),
                                              new JProperty("versionsURL",  EMSP.OCPIVersionsURL.ToString())
                                          )
                                      );

            if (!atTheOperator.Success)
            {
                Console.Say("env", $"OCPI: the CSMS would not take the EMSP as a roaming partner: {atTheOperator.Message}");
                return;
            }

            #endregion

            #region And walks over

            var registered = await CSMS.RegisterRemotePartyAsync(
                                       OCPIVersion,
                                       atTheOperator.Data!.Value<String>("id")
                                   );

            Console.Say(
                "env",
                registered.Success
                    ? $"OCPI: {CSMS.PartyIdText} and {EMSP.PartyIdText} are peered on OCPI {OCPIVersion}; " +
                      $"each end holds a token of the other's. {CSMS.PartyIdText} publishes no location yet."
                    : $"OCPI: the registration between {CSMS.PartyIdText} and {EMSP.PartyIdText} did not go through: {registered.Message}"
            );

            #endregion

        }

        #endregion

    }

}
