using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeSepareted.Lights
{
    public class ObjectsLibrary
    {
        private static readonly List<(string section, bool direction, int mega, int id)> LightMap = new()
        {
            ("P21.1", false, 8, 1),
            ("P22", false, 8, 2),
            ("P23", false, 8, 2),
            ("P24.2", true, 8, 3),
            ("P23", true, 8, 4),
            ("P21.2", true, 9, 1),
            ("P22", true, 9, 1),
            ("P22", true, 9, 2),
            ("P26.2", false, 9, 4),
            ("P26.3", true, 9, 5)
        };

        public static List<(int mega, int id)> GetLightInfo(string section, bool dir)
        {
            return LightMap
                .Where(x => x.section == section && x.direction == dir)
                .Select(x => (x.mega, x.id))
                .ToList();
        }

        private static readonly Dictionary<string, (string, bool, int)> HallToSection = new Dictionary<string, (string, bool, int)>
        {
            { "HALL_01", ("P24.2", true, 2)},
            //{ "HALL_02", ("", false, 2)},
            //{ "HALL_03", ("", false, 2)}
        };

        public static (string, bool, int) GetSection(string hallName)
        {
            if (HallToSection.TryGetValue(hallName, out var result))
            {
                return result;
            }

            return ("", false, -1);
        }
    }
}