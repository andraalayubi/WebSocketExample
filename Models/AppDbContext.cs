using Microsoft.EntityFrameworkCore;

namespace WebSocketExample.Models
{
    public class AppDbContext : DbContext
    {
        public DbSet<Document> Documents { get; set; }

        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }
    }
}
