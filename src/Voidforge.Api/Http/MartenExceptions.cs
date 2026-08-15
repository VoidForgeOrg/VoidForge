using Npgsql;

namespace Voidforge.Api.Http;

/// <summary>Detects the Postgres unique-constraint violation (23505) that Marten surfaces
/// when a duplicate hits a document unique index or primary key.</summary>
internal static class MartenExceptions
{
    public static bool IsUniqueViolation(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is PostgresException pg
                && string.Equals(pg.SqlState, PostgresErrorCodes.UniqueViolation, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
