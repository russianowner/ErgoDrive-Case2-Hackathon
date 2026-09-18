using AutoFleet_Vision.Components.Models;
using Microsoft.EntityFrameworkCore;

namespace AutoFleet_Vision.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<VehicleResult> VehicleResults { get; set; }
}