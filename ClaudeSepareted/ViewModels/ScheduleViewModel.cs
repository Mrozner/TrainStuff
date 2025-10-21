using System.Collections.ObjectModel;
using System.Text.Json;

namespace ClaudeSepareted
{
    public class ScheduleViewModel
    {
        public ObservableCollection<ScheduleItem> ScheduleItems { get; set; }

        private readonly string _filePath;

        public ScheduleViewModel()
        {
            _filePath = Path.Combine(FileSystem.AppDataDirectory, "menetrend.json");

            // Ha létezik mentett fájl → töltsük be
            if (File.Exists(_filePath))
            {
                try
                {
                    string json = File.ReadAllText(_filePath);
                    var items = JsonSerializer.Deserialize<ObservableCollection<ScheduleItem>>(json);

                    ScheduleItems = items ?? new ObservableCollection<ScheduleItem>();
                }
                catch
                {
                    // Ha hiba van a betöltésben, kezdjük alap adatokkal
                    ScheduleItems = GetDefaultSchedule();
                }
            }
            else
            {
                // Nincs még fájl → alap adatok
                ScheduleItems = GetDefaultSchedule();
            }
        }

        private ObservableCollection<ScheduleItem> GetDefaultSchedule()
        {
            return new ObservableCollection<ScheduleItem>
            {
                new ScheduleItem { TrainName="InterCity", Start = new TimeSpan(8,0,0), End = new TimeSpan(10,0,0), From="Budapest", To="Szeged"},
                new ScheduleItem { TrainName="Személyvonat", Start = new TimeSpan(9,30,0), End = new TimeSpan(12,0,0), From="Debrecen", To="Miskolc"},
                new ScheduleItem { TrainName="Gyorsvonat", Start = new TimeSpan(14,15,0), End = new TimeSpan(16,30,0), From="Pécs", To="Győr"},
            };
        }

        public void Add(ScheduleItem item)
        {
            ScheduleItems.Add(item);
        }
    }
}
