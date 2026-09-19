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

using EVLog       = cloud.charging.open.EV.Logging;
using StationLog  = cloud.charging.open.ChargingStation.Logging;
using ControlLog  = cloud.charging.open.LocalController.Logging;
using CSMSLog     = cloud.charging.open.CSMS.Logging;

#endregion

namespace cloud.charging.open.TestEnvironment
{

    /// <summary>
    /// Four event logs on one console, each line saying which of the four it
    /// came from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The four components each carry an event log of their own, in a namespace
    /// of their own - four copies of the same forty lines, because each of them
    /// is a program that ships alone and none of them may depend on another.
    /// Four separate types, therefore, and no way to hand one log to all four.
    /// </para>
    /// <para>
    /// That is not worth changing for this: what each web interface shows under
    /// <em>Logs</em> is that component's own log, which is exactly what it
    /// shows when the component runs alone, and the one place the four have to
    /// be read together is the console. So they are brought together here, at
    /// the one place where it matters, and each line is prefixed with who said
    /// it - which four <c>ConsoleLog</c>s of their own would not do, and which
    /// is the whole difficulty of reading four programs at once.
    /// </para>
    /// <para>
    /// The four <c>Attach</c> methods below are the same method four times
    /// because their parameters are four unrelated types with the same shape.
    /// C# has no way to say that, and inventing an interface for it would mean
    /// changing four repositories to add something only this one uses.
    /// </para>
    /// </remarks>
    public sealed class ConsoleMux
    {

        #region Data

        private readonly Lock     padlock  = new();
        private readonly Boolean  colours;
        private readonly Int32    minimumLevel;

        /// <summary>
        /// The widest of the names below, so that the messages line up.
        /// </summary>
        private const Int32 NameWidth = 8;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// One console for four logs.
        /// </summary>
        /// <param name="MinimumLevel">Entries below this stay off the console. They are still in each component's own log, where there is room for them.</param>
        /// <param name="Colours">Whether the level and the component are coloured; off by itself when the output is redirected.</param>
        public ConsoleMux(Int32     MinimumLevel,
                          Boolean?  Colours   = null)
        {
            this.minimumLevel  = MinimumLevel;
            this.colours       = Colours ?? !Console.IsOutputRedirected;
        }

        #endregion


        #region Attach(Log, Name) - once per component, because the types are four

        /// <summary>The vehicle's log.</summary>
        public void Attach(EVLog.EventLog Log, String Name)

            => Log.OnLogged += entry => Write(
                                            Name,
                                            (Int32) entry.Level,
                                            entry.Timestamp,
                                            entry.LevelName,
                                            entry.Tags,
                                            entry.Message
                                        );

        /// <summary>The charging station's log.</summary>
        public void Attach(StationLog.EventLog Log, String Name)

            => Log.OnLogged += entry => Write(
                                            Name,
                                            (Int32) entry.Level,
                                            entry.Timestamp,
                                            entry.LevelName,
                                            entry.Tags,
                                            entry.Message
                                        );

        /// <summary>The local controller's log.</summary>
        public void Attach(ControlLog.EventLog Log, String Name)

            => Log.OnLogged += entry => Write(
                                            Name,
                                            (Int32) entry.Level,
                                            entry.Timestamp,
                                            entry.LevelName,
                                            entry.Tags,
                                            entry.Message
                                        );

        /// <summary>The CSMS's log.</summary>
        public void Attach(CSMSLog.EventLog Log, String Name)

            => Log.OnLogged += entry => Write(
                                            Name,
                                            (Int32) entry.Level,
                                            entry.Timestamp,
                                            entry.LevelName,
                                            entry.Tags,
                                            entry.Message
                                        );

        #endregion

        #region Say(Name, Message)

        /// <summary>
        /// A line from the environment itself, in the same shape as the rest,
        /// for the moments before any of the four exists to say it.
        /// </summary>
        public void Say(String Name, String Message)

            => Write(Name, minimumLevel, DateTimeOffset.UtcNow, "Notice", [], Message);

        #endregion


        #region (private) Write(...)

        private void Write(String                Component,
                           Int32                 Level,
                           DateTimeOffset        Timestamp,
                           String                LevelName,
                           IReadOnlyList<String> Tags,
                           String                Message)
        {

            if (Level < minimumLevel)
                return;

            // What the libraries below write with DebugX reaches exactly one of
            // the four logs, because DebugX is one static listener list for the
            // whole process and four bridges would put every line into four
            // logs. Which of the four it lands in says nothing about which of
            // them was running at the time, so it is not named as that one.
            if (Tags.Contains("trace"))
                Component = "trace";

            // The console is one device and four components write to it from
            // every thread each of them has; without this the colour of one
            // entry ends up on the text of another.
            lock (padlock)
            {

                var to = Level >= 4 ? Console.Error : Console.Out;

                if (!colours)
                {
                    to.WriteLine($"{Timestamp.ToLocalTime():HH:mm:ss.fff} {Component.PadRight(NameWidth)} " +
                                 $"{LevelName.PadRight(8)}{(Tags.Count > 0 ? String.Join(" ", Tags) + " " : "")}{Message}");
                    return;
                }

                var previous = Console.ForegroundColor;

                try
                {

                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write(Timestamp.ToLocalTime().ToString("HH:mm:ss.fff"));
                    Console.Write(' ');

                    // The component in a colour of its own, which is the one
                    // thing a console showing four programs at once has to make
                    // easy: the eye finds the colour before it reads the name.
                    Console.ForegroundColor = ColourOfComponent(Component);
                    Console.Write(Component.PadRight(NameWidth));
                    Console.Write(' ');

                    Console.ForegroundColor = ColourOfLevel(Level);
                    Console.Write(LevelName.PadRight(8));

                    if (Tags.Count > 0)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkCyan;
                        Console.Write(String.Join(" ", Tags));
                        Console.Write(' ');
                    }

                    Console.ForegroundColor = previous;
                    Console.WriteLine(Message);

                }
                finally
                {
                    Console.ForegroundColor = previous;
                }

            }

        }

        #endregion

        #region (private static) ColourOfLevel(Level) / ColourOfComponent(Name)

        // The levels of all four enums are the same members in the same order,
        // which is what makes one number out of four types work at all.
        private static ConsoleColor ColourOfLevel(Int32 Level)

            => Level switch {
                   >= 5  => ConsoleColor.Magenta,      // Critical
                   4     => ConsoleColor.Red,          // Error
                   3     => ConsoleColor.Yellow,       // Warning
                   2     => ConsoleColor.Green,        // Notice
                   1     => ConsoleColor.Gray,         // Info
                   _     => ConsoleColor.DarkGray      // Debug
               };

        private static ConsoleColor ColourOfComponent(String Name)

            => Name switch {
                   "EV"       => ConsoleColor.Cyan,
                   "station"  => ConsoleColor.Blue,
                   "LC"       => ConsoleColor.DarkYellow,
                   "CSMS"     => ConsoleColor.DarkGreen,
                   _          => ConsoleColor.White
               };

        #endregion

    }

}
