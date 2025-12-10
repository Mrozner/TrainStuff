using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ClaudeSepareted.Domain;

namespace ClaudeSepareted.DataAccess
{
    public class TimetableRepository : ITimetableRepository
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        public TimetableRepository(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        public List<TimetableEntries> GetTimetableEntries()
        {
            _semaphore.Wait();
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                return db.TimetableEntries
                    .Where(e => e.EntryState != EntryState.Arrived)
                    .OrderBy(e => e.StartDate)
                    .ThenBy(e => e.StartTime)
                    .ToList();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public bool AddEntry(TimetableEntries entry)
        {
            _semaphore.Wait();
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                db.TimetableEntries.Add(entry);
                db.SaveChanges();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error adding entry: {ex.Message}");
                return false;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public void UpdateEntry(TimetableEntries entry)
        {
            _semaphore.Wait();
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                db.TimetableEntries.Update(entry);
                db.SaveChanges();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public void DeleteEntry(TimetableEntries entry)
        {
            _semaphore.Wait();
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                db.TimetableEntries.Remove(entry);
                db.SaveChanges();
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }
}