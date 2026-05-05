using ClaudeSepareted.Domain;
using System.Threading.Tasks;

namespace ClaudeSepareted.DataAccess
{
    public interface ITimetableRepository
    {
        // Existing timetable methods
        List<TimetableEntries> GetTimetableEntries();
        bool AddEntry(TimetableEntries entry);
        void UpdateEntry(TimetableEntries entry);
        void DeleteEntry(TimetableEntries entry);

        // Admin panel support methods
        Task<List<Train>> GetActiveTrainsAsync();
    }
}
