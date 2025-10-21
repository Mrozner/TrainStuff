using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TrainControlSystem.Configuration;
using TrainControlSystem.Models;

namespace TrainControlSystem.Repositories
{
    /// <summary>
    /// Repository for managing timetable entries in the database
    /// </summary>
    public class TimetableRepository
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        public TimetableRepository(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        /// <summary>
        /// Gets all timetable entries that haven't arrived
        /// </summary>
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

        /// <summary>
        /// Gets all entries for a specific train
        /// </summary>
        public List<TimetableEntries> GetEntriesForTrain(int trainId)
        {
            _semaphore.Wait();
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                return db.TimetableEntries
                    .Where(e => e.Train_DB_ID == trainId)
                    .OrderBy(e => e.StartDate)
                    .ThenBy(e => e.StartTime)
                    .ToList();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Gets pending (non-arrived) entries for a specific train
        /// </summary>
        public List<TimetableEntries> GetPendingEntriesForTrain(int trainId)
        {
            _semaphore.Wait();
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                return db.TimetableEntries
                    .Where(e => e.Train_DB_ID == trainId && e.EntryState != EntryState.Arrived)
                    .OrderBy(e => e.StartDate)
                    .ThenBy(e => e.StartTime)
                    .ToList();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Gets the next scheduled entry for a specific train
        /// </summary>
        public TimetableEntries? GetNextScheduledEntryForTrain(int trainId)
        {
            _semaphore.Wait();
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                return db.TimetableEntries
                    .Where(e => e.Train_DB_ID == trainId &&
                               e.EntryState != EntryState.Arrived &&
                               e.EntryState != EntryState.InTransit)
                    .OrderBy(e => e.StartDate)
                    .ThenBy(e => e.StartTime)
                    .FirstOrDefault();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Gets active entries for a specific train
        /// </summary>
        public List<TimetableEntries> GetActiveEntriesForTrain(int trainId)
        {
            _semaphore.Wait();
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                return db.TimetableEntries
                    .Where(e => e.Train_DB_ID == trainId &&
                               (e.EntryState == EntryState.Scheduled ||
                                e.EntryState == EntryState.InTransit ||
                                e.EntryState == EntryState.AtStation))
                    .OrderBy(e => e.StartDate)
                    .ThenBy(e => e.StartTime)
                    .ToList();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Adds a new timetable entry
        /// </summary>
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

        /// <summary>
        /// Updates an existing timetable entry
        /// </summary>
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

        /// <summary>
        /// Deletes a timetable entry
        /// </summary>
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