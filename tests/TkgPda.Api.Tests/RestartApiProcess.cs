using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Npgsql;
using TkgPda.Api;

namespace TkgPda.Api.Tests;

/// <summary>Real OS process restart on the phase-04 throwaway database only.</summary>
internal sealed class RestartApiProcess : IAsyncDisposable
{
    private readonly string connectionString;
    private readonly string apiDll;
    private readonly string contentRoot;
    private readonly string signingKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
    private Process? process;
    public string ApplicationName { get; } = "restart-proof-" + Guid.NewGuid().ToString("N");
    public Uri Address { get; }
    public int ProcessId => process?.Id ?? throw new InvalidOperationException("API is stopped.");

    public RestartApiProcess(string databaseConnection)
    {
        var connection = new NpgsqlConnectionStringBuilder(databaseConnection);
        if (connection.Database != "tkg_pda_phase04" || connection.Host != "127.0.0.1")
            throw new InvalidOperationException("Restart proof requires the isolated phase-04 database.");
        connection.ApplicationName = ApplicationName;
        connectionString = connection.ConnectionString;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TKG_PDA.slnx")))
        {
            // The repository may use .sln rather than .slnx.
            if (Directory.Exists(Path.Combine(root.FullName, "src", "TkgPda.Api"))) break;
            root = root.Parent;
        }
        if (root is null) throw new InvalidOperationException("API source root not found.");
        contentRoot = Path.Combine(root.FullName, "src", "TkgPda.Api");
        var configuration = typeof(RestartApiProcess).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        apiDll = Path.Combine(contentRoot, "bin", configuration, "net10.0", "TkgPda.Api.dll");
        if (!File.Exists(apiDll)) throw new InvalidOperationException("Built API DLL not found.");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        if (port is 5000 or 5001) throw new InvalidOperationException("Persistent API ports are forbidden.");
        Address = new Uri($"http://127.0.0.1:{port}");
    }

    public async Task StartAsync()
    {
        if (process is not null) throw new InvalidOperationException("API already running.");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = contentRoot,
        };
        start.ArgumentList.Add(apiDll);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add(Address.ToString());
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__TkgPda"] = connectionString;
        start.Environment["Authentication__Jwt__SigningKey"] = signingKey;
        start.Environment["Rds__Enabled"] = "false";
        start.Environment["Rds__DispatchEnabled"] = "false";
        start.Environment["RdsCoreExecution__Enabled"] = "false";
        process = Process.Start(start) ?? throw new InvalidOperationException("API could not start.");
        // Drain without printing environment values or authentication responses.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var client = new HttpClient { BaseAddress = Address, Timeout = TimeSpan.FromSeconds(1) };
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (process.HasExited) throw new InvalidOperationException($"Isolated API exited ({process.ExitCode}).");
            try
            {
                if ((await client.GetAsync("/health/ready")).IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException("Isolated API readiness timed out.");
    }

    public async Task KillAsync()
    {
        if (process is null) return;
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        process.Dispose();
        process = null;
    }

    public async Task RestartAsync()
    {
        var previous = ProcessId;
        await KillAsync();
        await StartAsync();
        if (ProcessId == previous) throw new InvalidOperationException("Restart did not replace the API process.");
    }

    public async Task<HttpClient> LoginAsync(TestIdentity identity)
    {
        var client = new HttpClient { BaseAddress = Address, Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.Add(ApiHeaders.DeviceCode, identity.DeviceCode);
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { employeeNumber = identity.EmployeeNo });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());
        return client;
    }

    public async ValueTask DisposeAsync() => await KillAsync();
}
