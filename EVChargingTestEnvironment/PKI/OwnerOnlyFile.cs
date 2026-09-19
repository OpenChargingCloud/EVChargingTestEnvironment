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

namespace cloud.charging.open.TestEnvironment.PKI
{

    /// <summary>
    /// A file only its owner may read.
    /// </summary>
    /// <remarks>
    /// The same twenty lines the charging station, the local controller and the
    /// CSMS each carry, and for the same reason: a private key or a password
    /// written with the default mode is world-readable on a shared machine, and
    /// tightening it afterwards leaves a window in which it was not.
    ///
    /// Windows has no equivalent of 0600 that is worth writing here - the
    /// directory decides there - so this is a plain write on Windows and says
    /// so rather than pretending otherwise.
    /// </remarks>
    public static class OwnerOnlyFile
    {

        #region Write(Path, Content)

        /// <summary>
        /// Write text readable and writable by its owner alone.
        /// </summary>
        public static void Write(String  Path,
                                 String  Content)
        {

            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(Path, Content);
                return;
            }

            using var stream = File.Open(
                                   Path,
                                   new FileStreamOptions {
                                       Mode            = FileMode.Create,
                                       Access          = FileAccess.Write,
                                       UnixCreateMode  = UnixFileMode.UserRead | UnixFileMode.UserWrite
                                   }
                               );

            using var writer = new StreamWriter(stream);

            writer.Write(Content);

        }

        #endregion

        #region WriteBytes(Path, Content)

        /// <summary>
        /// Write bytes readable and writable by its owner alone - a PKCS#12
        /// file, or a private key in DER.
        /// </summary>
        public static void WriteBytes(String  Path,
                                      Byte[]  Content)
        {

            if (OperatingSystem.IsWindows())
            {
                File.WriteAllBytes(Path, Content);
                return;
            }

            using var stream = File.Open(
                                   Path,
                                   new FileStreamOptions {
                                       Mode            = FileMode.Create,
                                       Access          = FileAccess.Write,
                                       UnixCreateMode  = UnixFileMode.UserRead | UnixFileMode.UserWrite
                                   }
                               );

            stream.Write(Content);

        }

        #endregion

    }

}
