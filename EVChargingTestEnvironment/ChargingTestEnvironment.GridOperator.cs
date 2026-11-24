/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of the EV Charging Test Environment
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

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

#endregion

namespace cloud.charging.open.TestEnvironment
{

    public sealed partial class ChargingTestEnvironment
    {

        #region Data

        /// <summary>
        /// The site's grid connection point, as the grid operator writes it
        /// down - the one the local controller is behind.
        /// </summary>
        public const String  GridConnectionPointId  = "GCP-1";

        /// <summary>
        /// The local controller as the grid operator knows it: the VEN it lets
        /// in, whose requests are taken as this one's.
        /// </summary>
        public const String  ControllerVENName      = "ven-lc001";

        /// <summary>
        /// The charging pool the local controller is, at the grid operator.
        /// </summary>
        public const String  ControllerPoolId       = "DE*GEF*P1";

        /// <summary>
        /// What the connection to the grid operator is called at the local
        /// controller - which is also how it is found again, and replaced, at
        /// the next start.
        /// </summary>
        private const String GridOperatorLine       = "The grid operator of this test environment";

        /// <summary>
        /// What the same connection is called at the grid operator.
        /// </summary>
        private const String ControllerLine         = "The local controller of this test environment";

        #endregion


        #region (private) ConnectToTheGridOperator()

        /// <summary>
        /// The line of the local controller over to the grid operator: its
        /// grid connection point and the local controller written down at the
        /// grid operator, where they are not yet, and the connection between
        /// the two at both ends - over the WebSocket port of OpenADR, in its
        /// JSON messages, signed in with a password made up here.
        /// </summary>
        /// <remarks>
        /// Replaced at every start, for the reason <see cref="ConnectUpwards"/>
        /// gives: a password is made up at every start, and a connection of
        /// the start before would sign in with the one of before. What the
        /// grid operator keeps of its own - its point, the local controller,
        /// the limits sent to it - is left as it is.
        /// </remarks>
        private void ConnectToTheGridOperator()
        {

            if (GridOperator is null || Controller is null)
                return;

            #region The grid connection point and the local controller, at the grid operator

            if (!GridOperator.TryGetGridConnectionPoint(GridConnectionPointId, out _) &&
                !GridOperator.TryAddGridConnectionPoint(
                     new JObject(
                         new JProperty("@id",                            GridConnectionPointId),
                         new JProperty("name",                           new JObject(new JProperty("en", "The grid connection of this test environment"))),
                         new JProperty("voltageLevel",                   "LowVoltage"),
                         new JProperty("connectionType",                 "AC_ThreePhases"),
                         new JProperty("nominalVoltage",                 400),
                         new JProperty("nominalFrequency",               50),
                         new JProperty("contractedImportPower",          110000),
                         new JProperty("contractedImportApparentPower",  120000)
                     ),
                     "test environment",
                     out _,
                     out var pointError,
                     out _))
            {
                throw new InvalidOperationException($"The grid operator could not be given the grid connection point '{GridConnectionPointId}': {pointError}");
            }

            if (GridOperator.OpenADR.Login(ControllerVENName) is null &&
               !GridOperator.TryAddLocalController(
                    new JObject(
                        new JProperty("venName",                ControllerVENName),
                        new JProperty("chargingPoolId",         ControllerPoolId),
                        new JProperty("gridConnectionPointId",  GridConnectionPointId),
                        new JProperty("description",            "The local controller of this test environment")
                    ),
                    "test environment",
                    out _,
                    out _,
                    out var venError,
                    out _))
            {
                throw new InvalidOperationException($"The grid operator could not be given the local controller '{ControllerVENName}': {venError}");
            }

            #endregion

            #region The connection of before, at both ends, goes

            foreach (var existing in GridOperator.Connections.Connections.
                                                  Where(one => one.Description.FirstText() == ControllerLine).
                                                  ToArray())
            {
                GridOperator.Connections.TryRemoveConnection(existing.Id, out _);
            }

            foreach (var existing in Controller.Connections.Connections.
                                                Where(one => one.Description.FirstText() == GridOperatorLine).
                                                ToArray())
            {
                Controller.Connections.TryRemoveConnection(existing.Id, out _);
            }

            foreach (var existing in Controller.Connections.Authentications.
                                                Where(one => one.Description.FirstText() == GridOperatorLine).
                                                ToArray())
            {
                Controller.Connections.TryRemoveAuthentication(existing.Id, out _);
            }

            #endregion

            var secret       = NewSecret();
            var description  = I18NString.Create(GridOperatorLine);
            var door         = URL.Parse($"ws://{options.Host}:{options.GridOperatorVENPort}/{ControllerLogin}");

            #region The grid operator: who signs in, and as which VEN

            AllowStation(GridOperator.WebSocketLogins, ControllerLogin, secret, "GO");

            if (!GridOperator.Connections.TryAddConnection(
                     I18NString.Create(ControllerLine),
                     door,
                     "VEN",
                     true,
                     null,
                     null,
                     out _,
                     out var inboundError,
                     Inbound:         true,
                     OpenADRBinding:  "messages",
                     VEN:             ControllerVENName))
            {
                throw new InvalidOperationException($"The grid operator could not be told of the local controller's connection: {inboundError}");
            }

            #endregion

            #region The local controller: where it connects to, and with what

            if (!Controller.Connections.TryAddPasswordAuthentication(
                     ControllerLogin,
                     secret,
                     description,
                     out var authenticationId,
                     out var authenticationError))
            {
                throw new InvalidOperationException($"The local controller's credentials for the grid operator could not be written down: {authenticationError}");
            }

            if (!Controller.Connections.TryAddConnection(
                     Description:       description,
                     URL:               door,
                     ConnectionType:    "GridOperator",
                     AutoConnect:       true,
                     AuthenticationId:  authenticationId,
                     CertificateId:     null,
                     Id:                out _,
                     Error:             out var connectionError,
                     OpenADRBinding:    "messages"))
            {
                throw new InvalidOperationException($"The local controller could not be told where its grid operator is: {connectionError}");
            }

            #endregion

        }

        #endregion

        #region (private) AskTheVTNThroughTheGridOperator()

        /// <summary>
        /// The local controller asks its VTN of OpenADR through its connection
        /// to the grid operator - and is told what the grid operator sends its
        /// grid connection point, as it is sent.
        /// </summary>
        private async Task AskTheVTNThroughTheGridOperator()
        {

            if (GridOperator is null || Controller is null)
                return;

            var (changed, error, _) = await Controller.TryUpdateOpenADRConfiguration(
                                                new JObject(
                                                    new JProperty("enabled",  true),
                                                    new JProperty("vtnURL",   $"ws://{options.Host}:{options.GridOperatorVENPort}/{ControllerLogin}")
                                                )
                                            );

            if (!changed && error is not null)
                throw new InvalidOperationException($"The local controller could not be told to ask the grid operator: {error}");

        }

        #endregion

    }

}
