using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace WaBulk.Infrastructure.Identity;

internal sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        // La migración AppUserActivo pone DEFAULT 1 en la base para que los usuarios existentes queden activos.
        builder.Property(x => x.IsActive).HasColumnName("Activo");
    }
}
