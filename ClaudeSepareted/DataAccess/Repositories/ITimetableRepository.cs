using ClaudeSepareted.Domain;

namespace ClaudeSepareted.DataAccess
{
    public interface ITimetableRepository
    {
        List<TimetableEntries> GetTimetableEntries();
        bool AddEntry(TimetableEntries entry);
        void UpdateEntry(TimetableEntries entry);
        void DeleteEntry(TimetableEntries entry);
    }
}
