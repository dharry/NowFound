using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NowFound.Infrastructure.Data;

/// <summary>EF Core ツール（マイグレーション生成）用のファクトリ。</summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=main.db")
            .Options;
        return new AppDbContext(options);
    }
}
