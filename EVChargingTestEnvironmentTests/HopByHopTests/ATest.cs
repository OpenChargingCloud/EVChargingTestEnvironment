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

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Collections.Concurrent;

#endregion

namespace cloud.charging.open.TestEnvironment.Tests.Connections.HopByHop
{

    public abstract class ATest
    {

        #region Data

        public const String  authPassword             = "password";
        public const String  authTOTP                 = "totp";
        public const String  authTOTPChannelBinding   = "totpChannelBinding";
        public const String  authPublicKey            = "publicKey";
        public const String  authMTLS                 = "mTLS";

        public const String  formatJSON               = "json";
        public const String  formatCBOR               = "cbor";

        /// <summary>
        /// How long a test waits for what is to happen on the network.
        /// </summary>
        protected static readonly TimeSpan  Waiting  = TimeSpan.FromSeconds(30);

        protected readonly ConcurrentQueue<String> nodeDirectories = new();

        protected X509Certificate2?  testCA;
        protected ECDsa?             testCAKey;

        #endregion

    }

}
