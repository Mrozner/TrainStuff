using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics;
using System.Reflection.Emit;

namespace ClaudeSepareted
{
    public class ApplicationDbContext : DbContext
    {
        public DbSet<Train> Trains { get; set; }
        public DbSet<Stations> Stations { get; set; }
        public DbSet<Platforms> Platforms { get; set; }
        public DbSet<Sections> Sections { get; set; }
        public DbSet<SubSections> SubSections { get; set; }
        public DbSet<Switches> Switches { get; set; }
        public DbSet<Objects> Objects { get; set; }
        public DbSet<TimetableEntries> TimetableEntries { get; set; }

        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Train>()
                .Property(e => e.MaxSpeed)
                .HasConversion<string>();

            modelBuilder.Entity<SubSections>()
                .Property(e => e.AllowedSpeed)
                .HasConversion<string>();

            modelBuilder.Entity<Objects>()
                .Property(e => e.ObjectType)
                .HasConversion<string>();

            modelBuilder.Entity<TimetableEntries>()
                .Property(e => e.EntryState)
                .HasConversion<string>();

            modelBuilder.Entity<TimetableEntries>()
                .Property(e => e.RouteState)
                .HasConversion<string>();
            modelBuilder.Entity<TimetableEntries>()
                .HasOne(e => e.Train) 
                .WithMany()           
                .HasForeignKey(e => e.Train_DB_ID); 

            modelBuilder.Entity<TimetableEntries>()
                .HasOne(e => e.SourceStation)
                .WithMany()
                .HasForeignKey(e => e.SourceStation_DB_ID);

            modelBuilder.Entity<TimetableEntries>()
                .HasOne(e => e.DestinationStation)
                .WithMany()
                .HasForeignKey(e => e.DestinationStation_DB_ID)
                .IsRequired(false);
        }
    }
}