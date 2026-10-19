using System;
using System.Collections.Generic;

namespace Ro3Emu.ClientHooks
{
    /// <summary>One selectable unit on the ground plane (x, z in metres).</summary>
    internal struct TargetPoint
    {
        public long Id;
        public float X, Z;
        public TargetPoint(long id, float x, float z) { Id = id; X = x; Z = z; }
    }

    /// <summary>Pure selection math for <see cref="Targeting"/>: cone filter, candidate order, cycling. No Unity types, so
    /// the tests compile it.</summary>
    internal static class TargetMath
    {
        /// <summary>How many metres of distance one half-turn (180 degrees) of angle costs in <see cref="Cycle"/> order.</summary>
        internal const float AnglePenaltyMetres = 10f;

        /// <summary>Angle in degrees (0..180) between the facing (fx, fz) and the direction to (dx, dz); 0 when either is zero.</summary>
        internal static float AngleTo(float fx, float fz, float dx, float dz)
        {
            double fl = Math.Sqrt(fx * fx + fz * fz), dl = Math.Sqrt(dx * dx + dz * dz);
            if (fl < 1e-6 || dl < 1e-6) return 0f;
            double cos = (fx * dx + fz * dz) / (fl * dl);
            return (float)(Math.Acos(Math.Max(-1.0, Math.Min(1.0, cos))) * 180.0 / Math.PI);
        }

        /// <summary>Ids within <paramref name="range"/> metres and <paramref name="halfAngle"/> degrees of the facing, nearest
        /// first (ties: smaller angle). The facing (0, 0) means "no direction": every angle passes.</summary>
        internal static List<long> Cone(float px, float pz, float fx, float fz, IList<TargetPoint> points, float halfAngle, float range)
        {
            var hits = new List<(long id, float dist, float angle)>();
            foreach (var p in points)
            {
                float dx = p.X - px, dz = p.Z - pz, d = (float)Math.Sqrt(dx * dx + dz * dz);
                if (d > range) continue;
                float a = AngleTo(fx, fz, dx, dz);
                if (a > halfAngle) continue;
                hits.Add((p.Id, d, a));
            }
            hits.Sort((l, r) => l.dist != r.dist ? l.dist.CompareTo(r.dist) : l.angle != r.angle ? l.angle.CompareTo(r.angle) : l.id.CompareTo(r.id));
            return hits.ConvertAll(h => h.id);
        }

        /// <summary>Every id within range, ordered by distance plus an angle penalty (<see cref="AnglePenaltyMetres"/>), so the
        /// cycle starts with what is close and ahead and walks outwards.</summary>
        internal static List<long> Cycle(float px, float pz, float fx, float fz, IList<TargetPoint> points, float range)
        {
            var hits = new List<(long id, float score)>();
            foreach (var p in points)
            {
                float dx = p.X - px, dz = p.Z - pz, d = (float)Math.Sqrt(dx * dx + dz * dz);
                if (d > range) continue;
                hits.Add((p.Id, d + AngleTo(fx, fz, dx, dz) / 180f * AnglePenaltyMetres));
            }
            hits.Sort((l, r) => l.score != r.score ? l.score.CompareTo(r.score) : l.id.CompareTo(r.id));
            return hits.ConvertAll(h => h.id);
        }

        /// <summary>The id after <paramref name="current"/> in <paramref name="order"/>, wrapping; the first when current is
        /// null or not in the list; null for an empty list.</summary>
        internal static long? Next(IList<long> order, long? current)
        {
            if (order.Count == 0) return null;
            int at = current.HasValue ? order.IndexOf(current.Value) : -1;
            return order[(at + 1) % order.Count];
        }
    }
}
