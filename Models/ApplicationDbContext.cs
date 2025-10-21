using Microsoft.EntityFrameworkCore;
using TrainControlSystem.Models;

namespace TrainControlSystem.Models
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
        {
        }

        // DbSets
        public DbSet<Stations> Stations { get; set; }
        public DbSet<Sections> Sections { get; set; }
        public DbSet<SubSections> SubSections { get; set; }
        public DbSet<Trains> Trains { get; set; }
        public DbSet<Signals> Signals { get; set; }
        public DbSet<TimetableEntries> TimetableEntries { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // Additional configuration can be added here if needed
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Additional model configuration can be added here
            // For example, relationships, indexes, constraints, etc.
        }
    }
}