using Microsoft.EntityFrameworkCore;
using NowFound.Domain.Entities;

namespace NowFound.Infrastructure.Data;

/// <summary>ope.db。書き込みが多いため main.db とは分離する。</summary>
public class OperationLogDbContext(DbContextOptions<OperationLogDbContext> options) : DbContext(options)
{
    public DbSet<OperationLog> OperationLogs => Set<OperationLog>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<OperationLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Operation).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.UserEmail).IsRequired().HasMaxLength(256);
            e.Property(x => x.Detail).IsRequired();
            e.Property(x => x.DetailKey).HasMaxLength(100);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => x.UserEmail);
            e.HasIndex(x => x.Operation);
        });
    }
}

public class OperationLogDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<OperationLogDbContext>
{
    public OperationLogDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<OperationLogDbContext>().UseSqlite("Data Source=ope.db").Options);
}
