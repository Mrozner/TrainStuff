namespace ClaudeSepareted.Common
{
    /// <summary>
    /// Unique key for routing table lookups based on source platform, destination platform, and direction
    /// </summary>
    public readonly struct RouteKey : IEquatable<RouteKey>
    {
        public int SourcePlatformId { get; }
        public int DestPlatformId { get; }
        public bool Direction { get; }

        public RouteKey(int sourcePlatformId, int destPlatformId, bool direction)
        {
            SourcePlatformId = sourcePlatformId;
            DestPlatformId = destPlatformId;
            Direction = direction;
        }

        public bool Equals(RouteKey other)
        {
            return SourcePlatformId == other.SourcePlatformId &&
                   DestPlatformId == other.DestPlatformId &&
                   Direction == other.Direction;
        }

        public override bool Equals(object obj)
        {
            return obj is RouteKey key && Equals(key);
        }

        public override int GetHashCode()
        {
            // Combine hash codes for all three fields
            unchecked
            {
                var hashCode = 17;
                hashCode = hashCode * 31 + SourcePlatformId.GetHashCode();
                hashCode = hashCode * 31 + DestPlatformId.GetHashCode();
                hashCode = hashCode * 31 + Direction.GetHashCode();
                return hashCode;
            }
        }

        public override string ToString()
        {
            return $"RouteKey(Source={SourcePlatformId}, Dest={DestPlatformId}, Dir={Direction})";
        }

        public static bool operator ==(RouteKey left, RouteKey right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(RouteKey left, RouteKey right)
        {
            return !(left == right);
        }
    }
}
