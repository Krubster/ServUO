using Server.Items;
using Server.Regions;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.Services.BotWatch
{
    /// <summary>
    /// Spatial index of every teleporter, moongate and teleport region entry point in the
    /// world, found automatically and refreshed hourly.
    /// </summary>
    public static class TeleporterIndex
    {
        private const int CellSize = 32;

        private static Dictionary<Map, Dictionary<long, List<Point2D>>> m_Grid = new Dictionary<Map, Dictionary<long, List<Point2D>>>();

        public static int Count { get; private set; }

        public static void Initialize()
        {
            // Teleport regions are created on a delayed call after startup.
            Timer.DelayCall(TimeSpan.FromSeconds(10), TimeSpan.FromHours(1), () =>
            {
                try
                {
                    Rebuild();
                }
                catch (Exception e)
                {
                    BotWatch.LogError(e);
                }
            });
        }

        public static void Rebuild()
        {
            var grid = new Dictionary<Map, Dictionary<long, List<Point2D>>>();
            int count = 0;

            void Add(Map map, int x, int y)
            {
                if (map == null || map == Map.Internal)
                    return;

                if (!grid.TryGetValue(map, out var cells))
                    grid[map] = cells = new Dictionary<long, List<Point2D>>();

                long key = Key(x / CellSize, y / CellSize);

                if (!cells.TryGetValue(key, out var list))
                    cells[key] = list = new List<Point2D>();

                list.Add(new Point2D(x, y));
                count++;
            }

            foreach (Item item in World.Items.Values)
            {
                if (!item.Deleted && item.Parent == null && (item is Teleporter || item is Moongate || item is PublicMoongate))
                    Add(item.Map, item.X, item.Y);
            }

            foreach (TeleportRegion region in Region.Regions.OfType<TeleportRegion>())
            {
                if (region.TeleLocs == null)
                    continue;

                foreach (WorldLocation loc in region.TeleLocs.Keys)
                    Add(loc.Map, loc.Location.X, loc.Location.Y);
            }

            m_Grid = grid;
            Count = count;
        }

        public static bool IsNear(Map map, IPoint2D p, int range)
        {
            if (map == null || !m_Grid.TryGetValue(map, out var cells))
                return false;

            int minX = (p.X - range) / CellSize, maxX = (p.X + range) / CellSize;
            int minY = (p.Y - range) / CellSize, maxY = (p.Y + range) / CellSize;

            for (int cx = minX; cx <= maxX; cx++)
            {
                for (int cy = minY; cy <= maxY; cy++)
                {
                    if (!cells.TryGetValue(Key(cx, cy), out var list))
                        continue;

                    foreach (Point2D t in list)
                    {
                        if (Math.Abs(t.X - p.X) <= range && Math.Abs(t.Y - p.Y) <= range)
                            return true;
                    }
                }
            }

            return false;
        }

        private static long Key(int cx, int cy)
        {
            return ((long)cx << 32) | (uint)cy;
        }
    }
}
