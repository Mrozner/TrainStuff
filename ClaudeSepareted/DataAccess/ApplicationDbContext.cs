using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics;
using System.Reflection.Emit;
using ClaudeSepareted.Domain;

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
        public DbSet<TrackConnection> TrackConnections { get; set; }
        public DbSet<VLookupSectionNextSection> VLookupSectionNextSection { get; set; }
        public DbSet<VPlatformEntry> VPlatformEntry { get; set; }
        public DbSet<LookupSectionNextSection> LookupSectionNextSection { get; set; }
        public DbSet<LookupSectionNextSectionSwitches> LookupSectionNextSectionSwitches { get; set; }
        public DbSet<LookupSectionNextSectionDestinations> LookupSectionNextSectionDestinations { get; set; }
        public DbSet<LookupSectionsSubSections> LookupSectionsSubSections { get; set; }
        public DbSet<LookupSectionNextSectionNextSection> LookupSectionNextSectionNextSection { get; set; }

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

            // Configure TimetableEntries
            modelBuilder.Entity<TimetableEntries>(entity =>
            {
                // 1. Trigger bypass to prevent OUTPUT clause crashes
                entity.ToTable(tb => tb.HasTrigger("PreventOutputClauseTrigger"));

                // 2. Enum Conversions (Fixes the 'Upcoming' to int crash)
                entity.Property(e => e.EntryState).HasConversion<string>();
                entity.Property(e => e.RouteState).HasConversion<string>();

                // 3. Explicit Foreign Keys (Fixes the Invalid Column Name crash)
                // Forces EF Core to look for the FKs on TimetableEntries, not on the Platforms table
                entity.HasOne(e => e.SourcePlatform)
                      .WithMany()
                      .HasForeignKey(e => e.SourcePlatform_DB_ID)
                      .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(e => e.DestinationPlatform)
                      .WithMany()
                      .HasForeignKey(e => e.DestinationPlatform_DB_ID)
                      .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(e => e.Train)
                      .WithMany()
                      .HasForeignKey(e => e.Train_DB_ID)
                      .OnDelete(DeleteBehavior.Restrict);
            });

            // Configure Platforms relationships
            modelBuilder.Entity<Platforms>()
                .HasOne(p => p.SubSection)
                .WithMany()
                .HasForeignKey(p => p.SubSection_DB_ID);

            modelBuilder.Entity<Platforms>()
                .HasOne(p => p.Station)
                .WithMany()
                .HasForeignKey(p => p.Station_DB_ID);

            // Configure VLookupSectionNextSection view mappings
            modelBuilder.Entity<VLookupSectionNextSection>(entity =>
            {
                entity.ToTable("V_Lookup_Section_NextSection");
                entity.HasKey(e => e.DB_ID);
            });
        }
    }
}