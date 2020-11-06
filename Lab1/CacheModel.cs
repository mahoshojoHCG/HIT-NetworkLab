using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Lab1
{
    public class CacheContext : DbContext
    {
        public DbSet<Cache> Caches { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            options.UseSqlite("Data Source=cache.db");
        }
    }

    public class Cache
    {
        [MaxLength(128)] public string CacheId { get; set; }

        public DateTime CachedTime { get; set; }
        public DateTime ExpireTime { get; set; }

        [MaxLength(40960)] public byte[] Content { get; set; }
    }
}