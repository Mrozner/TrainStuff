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
        public DbSet<TimetableEntriesArrived> TimetableEntriesArrived { get; set; }
        public DbSet<TimetableEntriesUpcoming> TimetableEntriesUpcoming { get; set; }
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

            modelBuilder.Entity<TimetableEntries>()
                .Property(e => e.EntryState)
                .HasConversion<string>();

            modelBuilder.Entity<TimetableEntries>()
                .Property(e => e.RouteState)
                .HasConversion<string>();

            modelBuilder.Entity<TimetableEntriesArrived>()
                .Property(e => e.RouteState)
                .HasConversion<string>();

            modelBuilder.Entity<TimetableEntriesUpcoming>()
                .Property(e => e.EntryState)
                .HasConversion<string>();

            modelBuilder.Entity<TimetableEntriesUpcoming>()
                .Property(e => e.RouteState)
                .HasConversion<string>();

            // Configure TimetableEntries relationships
            modelBuilder.Entity<TimetableEntries>()
                .HasOne(e => e.Train)
                .WithMany()
                .HasForeignKey(e => e.Train_DB_ID);

            modelBuilder.Entity<TimetableEntries>()
                .Property(e => e.SourcePlatform_DB_ID)
                .HasColumnName("SourcePlatform_DB_ID");

            modelBuilder.Entity<TimetableEntries>()
                .Property(e => e.DestinationPlatform_DB_ID)
                .HasColumnName("DestinationPlatform_DB_ID");

            modelBuilder.Entity<TimetableEntries>()
                .HasOne(e => e.SourcePlatform)
                .WithMany()
                .HasForeignKey(e => e.SourcePlatform_DB_ID);

            modelBuilder.Entity<TimetableEntries>()
                .HasOne(e => e.DestinationPlatform)
                .WithMany()
                .HasForeignKey(e => e.DestinationPlatform_DB_ID);

            // Configure TimetableEntriesArrived relationships
            modelBuilder.Entity<TimetableEntriesArrived>()
                .HasOne(e => e.Train)
                .WithMany()
                .HasForeignKey(e => e.Train_DB_ID);

            modelBuilder.Entity<TimetableEntriesArrived>()
                .Property(e => e.SourcePlatform_DB_ID)
                .HasColumnName("SourcePlatform_DB_ID");

            modelBuilder.Entity<TimetableEntriesArrived>()
                .Property(e => e.DestinationPlatform_DB_ID)
                .HasColumnName("DestinationPlatform_DB_ID");

            modelBuilder.Entity<TimetableEntriesArrived>()
                .HasOne(e => e.SourcePlatform)
                .WithMany()
                .HasForeignKey(e => e.SourcePlatform_DB_ID);

            modelBuilder.Entity<TimetableEntriesArrived>()
                .HasOne(e => e.DestinationPlatform)
                .WithMany()
                .HasForeignKey(e => e.DestinationPlatform_DB_ID);

            // Configure TimetableEntriesUpcoming relationships
            modelBuilder.Entity<TimetableEntriesUpcoming>()
                .HasOne(e => e.Train)
                .WithMany()
                .HasForeignKey(e => e.Train_DB_ID);

            modelBuilder.Entity<TimetableEntriesUpcoming>()
                .Property(e => e.SourcePlatform_DB_ID)
                .HasColumnName("SourcePlatform_DB_ID");

            modelBuilder.Entity<TimetableEntriesUpcoming>()
                .Property(e => e.DestinationPlatform_DB_ID)
                .HasColumnName("DestinationPlatform_DB_ID");

            modelBuilder.Entity<TimetableEntriesUpcoming>()
                .HasOne(e => e.SourcePlatform)
                .WithMany()
                .HasForeignKey(e => e.SourcePlatform_DB_ID);

            modelBuilder.Entity<TimetableEntriesUpcoming>()
                .HasOne(e => e.DestinationPlatform)
                .WithMany()
                .HasForeignKey(e => e.DestinationPlatform_DB_ID);

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