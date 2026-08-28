using Microsoft.EntityFrameworkCore;
using TimescaleApi.Models;

namespace TimescaleApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<FileRecord> Files => Set<FileRecord>();
    public DbSet<ValueRecord> Values => Set<ValueRecord>();
    public DbSet<ResultRecord> Results => Set<ResultRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FileRecord>()
            .HasIndex(f => f.FileName)
            .IsUnique();

        modelBuilder.Entity<ValueRecord>()
            .HasOne(v => v.File)
            .WithMany(f => f.Values)
            .HasForeignKey(v => v.FileId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ResultRecord>()
            .HasOne(r => r.File)
            .WithOne(f => f.Result)
            .HasForeignKey<ResultRecord>(r => r.FileId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}