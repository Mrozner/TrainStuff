using System.Security;

namespace ClaudeSepareted.Domain
{
    /// <summary>
    /// Centralized factory for generating Rocrail XML commands
    /// Eliminates hardcoded XML strings scattered across services
    /// </summary>
    public static class RocrailCommandFactory
    {
        /// <summary>
        /// Generate system power command (go/stop)
        /// </summary>
        /// <param name="on">True to power on, false to power off</param>
        /// <returns>XML command string</returns>
        public static string SystemPower(bool on) =>
            $"<sys cmd=\"{(on ? "go" : "stop")}\"/>";

        /// <summary>
        /// Generate switch/turnout command
        /// </summary>
        /// <param name="switchId">Switch identifier</param>
        /// <param name="position">Switch position (straight/turn, closed/thrown, etc.)</param>
        /// <returns>XML command string</returns>
        public static string Switch(string switchId, string position) =>
            $"<sw id=\"{SecurityElement.Escape(switchId)}\" cmd=\"{SecurityElement.Escape(position)}\"/>";

        /// <summary>
        /// Generate signal command
        /// </summary>
        /// <param name="signalId">Signal identifier</param>
        /// <param name="aspect">Signal aspect/state</param>
        /// <returns>XML command string</returns>
        public static string Signal(string signalId, string aspect) =>
            $"<sg id=\"{SecurityElement.Escape(signalId)}\" aspect=\"{SecurityElement.Escape(aspect)}\"/>";

        /// <summary>
        /// Generate train velocity command
        /// </summary>
        /// <param name="trainName">Train identifier</param>
        /// <param name="speed">Speed value (0-255, typically use Speed enum values)</param>
        /// <param name="forward">Direction - true for forward, false for reverse</param>
        /// <returns>XML command string</returns>
        public static string TrainVelocity(string trainName, int speed, bool forward) =>
            $"<lc id=\"{SecurityElement.Escape(trainName)}\" v=\"{speed}\" dir=\"{forward.ToString().ToLower()}\"/>";

        /// <summary>
        /// Generate train velocity command using Speed enum
        /// </summary>
        /// <param name="trainName">Train identifier</param>
        /// <param name="speed">Speed enum value</param>
        /// <param name="forward">Direction - true for forward, false for reverse</param>
        /// <returns>XML command string</returns>
        public static string TrainVelocity(string trainName, Speed speed, bool forward) =>
            TrainVelocity(trainName, (int)speed, forward);

        /// <summary>
        /// Generate train power command
        /// </summary>
        /// <param name="trainName">Train identifier</param>
        /// <param name="on">True to power on, false to power off</param>
        /// <returns>XML command string</returns>
        public static string TrainPower(string trainName, bool on) =>
            $"<lc id=\"{SecurityElement.Escape(trainName)}\" cmd=\"{(on ? "on" : "off")}\"/>";

        /// <summary>
        /// Generate train mode command (auto/manual)
        /// </summary>
        /// <param name="trainName">Train identifier</param>
        /// <param name="auto">True for automatic mode, false for manual</param>
        /// <returns>XML command string</returns>
        public static string TrainMode(string trainName, bool auto) =>
            $"<lc id=\"{SecurityElement.Escape(trainName)}\" cmd=\"{(auto ? "auto" : "manual")}\"/>";

        /// <summary>
        /// Generate generic locomotive command
        /// </summary>
        /// <param name="trainName">Train identifier</param>
        /// <param name="command">Command type</param>
        /// <returns>XML command string</returns>
        public static string TrainCommand(string trainName, string command) =>
            $"<lc id=\"{SecurityElement.Escape(trainName)}\" cmd=\"{SecurityElement.Escape(command)}\"/>";

        /// <summary>
        /// Generate clock command for time synchronization
        /// </summary>
        /// <param name="hour">Hour value (0-23)</param>
        /// <param name="minute">Minute value (0-59)</param>
        /// <param name="day">Day value (1-31)</param>
        /// <param name="month">Month value (1-12)</param>
        /// <param name="year">Year value</param>
        /// <returns>XML command string</returns>
        public static string Clock(int hour, int minute, int day, int month, int year) =>
            $"<clock hour=\"{hour}\" min=\"{minute}\" day=\"{day}\" month=\"{month}\" year=\"{year}\"/>";

        /// <summary>
        /// Generate schedule/timetable command
        /// </summary>
        /// <param name="command">Schedule command (start/stop, etc.)</param>
        /// <returns>XML command string</returns>
        public static string Schedule(string command) =>
            $"<schedule cmd=\"{SecurityElement.Escape(command)}\"/>";
    }
}