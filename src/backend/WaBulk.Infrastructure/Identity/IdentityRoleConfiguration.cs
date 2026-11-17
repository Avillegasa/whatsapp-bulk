using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WaBulk.Domain.Users;

namespace WaBulk.Infrastructure.Identity;

/// <summary>
/// Los tres roles van como datos de la migración (Id y ConcurrencyStamp fijos para que la migración sea estable):
/// la base queda lista sin un paso aparte de "sembrar roles".
/// </summary>
internal sealed class IdentityRoleConfiguration : IEntityTypeConfiguration<IdentityRole>
{
    public void Configure(EntityTypeBuilder<IdentityRole> builder)
    {
        builder.HasData(
            Role("6f1d6c0e-3b7a-4c1e-9a51-0c5e2b7d1a01", Roles.Operador),
            Role("6f1d6c0e-3b7a-4c1e-9a51-0c5e2b7d1a02", Roles.Aprobador),
            Role("6f1d6c0e-3b7a-4c1e-9a51-0c5e2b7d1a03", Roles.Admin));
    }

    private static IdentityRole Role(string id, string name) => new()
    {
        Id = id,
        Name = name,
        NormalizedName = name.ToUpperInvariant(),
        ConcurrencyStamp = id,
    };
}
