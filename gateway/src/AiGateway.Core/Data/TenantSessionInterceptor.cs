using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AiGateway.Core.Data;

/// <summary>
/// Lapis kedua isolasi tenant (G4): memasang <c>SESSION_CONTEXT(N'tenant_id')</c> pada koneksi sebelum setiap
/// command EF dijalankan, sehingga security policy SQL Server (<c>gateway/sql/tenant-security.sql</c>) tetap
/// menegakkan batas tenant walau query filter EF dilewati (mis. <c>IgnoreQueryFilters()</c>) atau ada bug lain
/// di lapis aplikasi.
///
/// Koneksi di-pool: state dipelacak per objek <see cref="DbConnection"/> dan di-reset setiap kali koneksi
/// dibuka/ditutup, jadi context selalu dipasang ulang — baik untuk koneksi yang dipakai ulang dari pool,
/// maupun untuk koneksi yang sudah terbuka saat tenant berganti.
///
/// <see cref="GatewayDbContext.NoTenant"/> memasang NULL: predikat RLS tidak menemukan baris dan menolak semua
/// tulisan (fail closed), bukan membuka semua baris. Interceptor ini bukan bypass platform — hanya keanggotaan
/// database role <c>gateway_platform</c> yang diakui predikat; flag session apa pun diabaikan.
///
/// <para>
/// Integrasi: daftarkan sebagai scoped <c>services.AddScoped&lt;TenantSessionInterceptor&gt;()</c>, pasang lewat
/// <c>AddDbContext&lt;GatewayDbContext&gt;((sp, o) =&gt; o.UseGatewaySqlServer(cs).AddInterceptors(sp.GetRequiredService&lt;TenantSessionInterceptor&gt;()))</c>,
/// dan teruskan instance yang sama ke konstruktor <see cref="GatewayDbContext"/> supaya
/// <see cref="GatewayDbContext.CurrentTenantId"/> mengisi <see cref="TenantId"/>.
/// </para>
/// </summary>
public sealed class TenantSessionInterceptor : DbCommandInterceptor, IDbConnectionInterceptor
{
    /// <summary>Kunci SESSION_CONTEXT; harus sama dengan yang dibaca predikat di tenant-security.sql.</summary>
    public const string SessionContextKey = "tenant_id";

    private sealed class ConnectionState
    {
        public bool Applied;
        public long AppliedTenant = GatewayDbContext.NoTenant;
    }

    private readonly ConditionalWeakTable<DbConnection, ConnectionState> _states = new();

    /// <summary>Tenant untuk sesi ini. Diisi dari <see cref="GatewayDbContext.CurrentTenantId"/>, bukan dari klien.</summary>
    public long TenantId { get; set; } = GatewayDbContext.NoTenant;

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        EnsureSessionContext(command.Connection, command.Transaction);
        return result;
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        EnsureSessionContext(command.Connection, command.Transaction);
        return result;
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        EnsureSessionContext(command.Connection, command.Transaction);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        EnsureSessionContext(command.Connection, command.Transaction);
        return new(result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        EnsureSessionContext(command.Connection, command.Transaction);
        return new(result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        EnsureSessionContext(command.Connection, command.Transaction);
        return new(result);
    }

    // Koneksi dibuka/ditutup: state dianggap basi, command pertama setelahnya memasang ulang context.
    // Anggota IDbConnectionInterceptor (bukan virtual di DbCommandInterceptor), jadi implementasi biasa.
    public void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ResetState(connection);
        EnsureSessionContext(connection, transaction: null);
    }

    public Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        ConnectionOpened(connection, eventData);
        return Task.CompletedTask;
    }

    public void ConnectionClosed(DbConnection connection, ConnectionEndEventData eventData) => ResetState(connection);

    public Task ConnectionClosedAsync(DbConnection connection, ConnectionEndEventData eventData)
    {
        ResetState(connection);
        return Task.CompletedTask;
    }

    private void ResetState(DbConnection connection)
    {
        if (_states.TryGetValue(connection, out var state))
        {
            lock (state)
            {
                state.Applied = false;
            }
        }
    }

    /// <summary>
    /// Pasang ulang SESSION_CONTEXT bila koneksi tidak membawa tenant yang diminta. Command mentah dibuat
    /// langsung dari koneksi (bukan lewat EF) supaya tidak memicu interceptor ini lagi; transaksi command
    /// pemicu disalin agar command tetap sah saat ada transaksi lokal.
    /// </summary>
    private void EnsureSessionContext(DbConnection? connection, DbTransaction? transaction)
    {
        if (connection is null || connection.State != System.Data.ConnectionState.Open)
            return; // Koneksi tertutup: ConnectionOpened akan memasang context sebelum command dijalankan.

        var state = _states.GetOrCreateValue(connection);
        lock (state)
        {
            var tenant = TenantId;
            if (state.Applied && state.AppliedTenant == tenant)
            {
                return;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "EXEC sys.sp_set_session_context @key = @key, @value = @value;";
            AddParameter(command, "@key", DbType.String, SessionContextKey);
            AddParameter(command, "@value", DbType.Int64,
                tenant == GatewayDbContext.NoTenant ? DBNull.Value : tenant);
            try
            {
                command.ExecuteNonQuery();
            }
            // ponytail: database sedang di-drop/ditutup (drop database, restore, failover) tidak bisa memasang
            // context; buang koneksi fisik itu dari pool agar tidak mewarisi sesi "kill state". Error lain tetap dilempar.
            catch (SqlException ex) when (ex.Number is 596 or 233 or 4060 or 911 or 3701 or 3702)
            {
                if (connection is SqlConnection sql) SqlConnection.ClearPool(sql);
                return;
            }

            state.Applied = true;
            state.AppliedTenant = tenant;
        }
    }

    private static void AddParameter(DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
