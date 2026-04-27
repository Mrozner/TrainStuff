using System;

namespace ClaudeSepareted.Services
{
    /// <summary>
    /// Represents the direction of train movement.
    /// Eliminates boolean blindness by providing explicit direction semantics.
    /// </summary>
    public enum MovementDirection
    {
        /// <summary>
        /// Train is moving in the forward/normal direction
        /// </summary>
        Forward = 1,

        /// <summary>
        /// Train is moving in the reverse/opposite direction
        /// </summary>
        Reverse = 0
    }

    /// <summary>
    /// Event arguments containing information about a train's movement between subsections.
    /// Fired by TrackOccupancyService when a train transitions from one section to another.
    ///
    /// This is a modern C# record with nullable reference types enabled for better safety.
    /// Note: Uses Channel-based pub/sub instead of traditional C# events, so does not inherit from EventArgs.
    /// </summary>
    public record TrainMovedEventArgs
    {
        /// <summary>
        /// The unique identifier of the train that moved
        /// </summary>
        public string TrainId { get; init; }

        /// <summary>
        /// The subsection the train was previously occupying (null if this is the initial registration)
        /// </summary>
        public string? PreviousSubSection { get; init; }

        /// <summary>
        /// The subsection the train is now occupying
        /// </summary>
        public string CurrentSubSection { get; init; }

        /// <summary>
        /// The direction the train is moving (Forward or Reverse)
        /// </summary>
        public MovementDirection Direction { get; init; }

        /// <summary>
        /// Creates a new TrainMovedEventArgs instance with explicit validation
        /// Accepts bool for backward compatibility with existing code
        /// </summary>
        /// <param name="trainId">The unique identifier of the train that moved</param>
        /// <param name="previousSubSection">The subsection the train was previously occupying (null if initial registration)</param>
        /// <param name="currentSubSection">The subsection the train is now occupying</param>
        /// <param name="direction">The direction the train is moving (true = forward, false = reverse)</param>
        public TrainMovedEventArgs(
            string trainId,
            string? previousSubSection,
            string currentSubSection,
            bool direction)
        {
            TrainId = trainId ?? throw new ArgumentNullException(nameof(trainId));
            CurrentSubSection = currentSubSection ?? throw new ArgumentNullException(nameof(currentSubSection));
            PreviousSubSection = previousSubSection; // Can be null for initial registration
            Direction = direction ? MovementDirection.Forward : MovementDirection.Reverse;
        }

        /// <summary>
        /// Creates a new TrainMovedEventArgs instance with MovementDirection enum
        /// </summary>
        /// <param name="trainId">The unique identifier of the train that moved</param>
        /// <param name="previousSubSection">The subsection the train was previously occupying (null if initial registration)</param>
        /// <param name="currentSubSection">The subsection the train is now occupying</param>
        /// <param name="direction">The direction the train is moving</param>
        public TrainMovedEventArgs(
            string trainId,
            string? previousSubSection,
            string currentSubSection,
            MovementDirection direction)
        {
            TrainId = trainId ?? throw new ArgumentNullException(nameof(trainId));
            CurrentSubSection = currentSubSection ?? throw new ArgumentNullException(nameof(currentSubSection));
            PreviousSubSection = previousSubSection; // Can be null for initial registration
            Direction = direction;
        }

        /// <summary>
        /// Returns a human-readable description of the train movement
        /// </summary>
        public override string ToString()
        {
            var directionStr = Direction == MovementDirection.Forward ? "forward" : "reverse";

            if (string.IsNullOrWhiteSpace(PreviousSubSection))
            {
                return $"{TrainId} appeared in {CurrentSubSection} (moving {directionStr})";
            }

            return $"{TrainId} moved from {PreviousSubSection} to {CurrentSubSection} (moving {directionStr})";
        }
    }
}
