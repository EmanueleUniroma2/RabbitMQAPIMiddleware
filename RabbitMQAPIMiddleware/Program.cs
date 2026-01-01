using Microsoft.Extensions.Caching.Memory;
using Microsoft.OpenApi.Models;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Dapper;
using MassTransit;
using System.Text;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Collections.Generic;
using RabbitMQAPIMiddleware.Models;
var builder = WebApplication.CreateBuilder(args);

// --- 1. VAULT INITIALIZATION ---
var vaultRequests = new[]
{
    new VaultKey("RabbitMQAPIMiddleware", "ConnectionString", "DefaultConnection"),
    new VaultKey("RabbitMQAPIMiddleware", "Username", "AdminUser"),
    new VaultKey("RabbitMQAPIMiddleware", "Password", "AdminPass"),
    new VaultKey("RabbitMQAPIMiddleware", "AutoLoginSecret", "SecretKey"),

    // Specific for rabbitMQ container instance
    new VaultKey("RabbitMQAPIMiddleware", "RabbitHost", "RabbitHost"),
    new VaultKey("RabbitMQAPIMiddleware", "RabbitUser", "RabbitUser"),
    new VaultKey("RabbitMQAPIMiddleware", "RabbitPass", "RabbitPass"),
    new VaultKey("RabbitMQAPIMiddleware", "MgmtApi", "MgmtApi")
};

try
{
    await builder.Configuration.LoadVaultSecrets(vaultRequests);
}
catch (Exception ex)
{
    Console.WriteLine($"FATAL: RabbitMQAPIMiddleware could not load secrets. {ex.Message}");
    return;
}

// --- 2. CONFIGURATION & SERVICES ---
builder.WebHost.UseUrls("http://127.0.0.1:7714");
builder.Services.AddMemoryCache();
builder.Services.AddCors(options => options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { In = ParameterLocation.Header, Description = "Enter token", Name = "Authorization", Type = SecuritySchemeType.ApiKey });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement { { new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }, Array.Empty<string>() } });
});


// Configuration extraction
string connectionString = builder.Configuration["DefaultConnection"]!;
string expectedUser = builder.Configuration["AdminUser"]!;
string expectedPass = builder.Configuration["AdminPass"]!;
string expectedSecret = builder.Configuration["SecretKey"]!;

string rabbitMqHost = builder.Configuration["RabbitHost"]!;
string rabbitMqUser = builder.Configuration["RabbitUser"]!;
string rabbitMqPass = builder.Configuration["RabbitPass"]!;
string rabbitMqMgmtApi = builder.Configuration["MgmtApi"]!;



// 1. DATA PLANE: MassTransit
builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(rabbitMqHost, "/", h =>
        {
            h.Username(rabbitMqUser);
            h.Password(rabbitMqPass);
        });
        cfg.UseRawJsonSerializer();
        cfg.ConfigureEndpoints(context);
    });
});

// 2. CONTROL PLANE: HttpClient per Management API
builder.Services.AddHttpClient("RabbitMgmt", client =>
{
    client.BaseAddress = new Uri(rabbitMqMgmtApi);
    var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{rabbitMqUser}:{rabbitMqPass}"));
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
});



var app = builder.Build();



// --- 3. DATABASE INITIALIZATION (SmallTerminal Style) ---
DbInitializer.Init(connectionString, "RabbitMQAPIMiddleware");

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();


static async ValueTask<object?> AuthFilter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
{
    // 1. Ottieni il servizio di cache dal DI container
    var cache = context.HttpContext.RequestServices.GetRequiredService<IMemoryCache>();

    // 2. Leggi l'header Authorization
    string? authHeader = context.HttpContext.Request.Headers["Authorization"];

    // 3. Verifica se l'header è presente e nel formato corretto "Bearer <token>"
    if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Unauthorized();
    }

    // 4. Estrai il token pulito
    var token = authHeader.Substring("Bearer ".Length).Trim();

    // 5. Verifica la presenza del token nella MemoryCache
    // Usiamo lo stesso prefisso "session_" impostato nei tuoi endpoint di login
    if (!cache.TryGetValue($"session_{token}", out var username))
    {
        return Results.Unauthorized();
    }

    // (Opzionale) Possiamo iniettare l'utente nel contesto per usarlo negli endpoint se serve
    context.HttpContext.Items["User"] = username;

    // 6. Se tutto è OK, procedi con l'esecuzione dell'API
    return await next(context);
}


// --- AUTH ENDPOINTS ---
app.MapPost("/api/login/secret", async (SecretLoginRequest request, IMemoryCache cache) =>
{
    if (request.Secret == expectedSecret)
    {
        var token = Guid.NewGuid().ToString();
        cache.Set($"session_{token}", "secret_user", TimeSpan.FromHours(8));
        return Results.Ok(new { token });
    }
    await Task.Delay(2000);
    return Results.Unauthorized();
});

app.MapPost("/api/login", async (LoginRequest request, IMemoryCache cache) =>
{
    if (request.Username == expectedUser && request.Password == expectedPass)
    {
        var token = Guid.NewGuid().ToString();
        cache.Set($"session_{token}", request.Username, TimeSpan.FromHours(8));
        return Results.Ok(new { token });
    }
    await Task.Delay(2000);
    return Results.Unauthorized();
});


// Add here API who need to be protected
var protectedApi = app.MapGroup("").AddEndpointFilter(AuthFilter);

protectedApi.MapPost("/api/messages/{queue}", async (string queue, [FromBody] JsonElement jsonMessage, ISendEndpointProvider sendProvider) =>
{
    try
    {
        // 1. Deserializziamo nel nostro tipo custom che eredita da Dictionary
        var messagePayload = JsonSerializer.Deserialize<DynamicMessage>(jsonMessage.GetRawText());

        if (messagePayload == null) return Results.BadRequest("Payload non valido");

        // 2. Otteniamo l'endpoint
        var endpoint = await sendProvider.GetSendEndpoint(new Uri($"queue:{queue}"));

        // 3. Inviamo il wrapper
        await endpoint.Send(messagePayload);

        return Results.Ok(new { status = "Inviato", target = queue });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, title: "Errore MassTransit Namespace", statusCode: 500);
    }
});

// Lista code (Management)
protectedApi.MapGet("/api/mgmt/queues", async (IHttpClientFactory factory) =>
{
    var client = factory.CreateClient("RabbitMgmt");
    try
    {
        var response = await client.GetFromJsonAsync<List<object>>("queues");
        return Results.Ok(response);
    }
    catch
    {
        return Results.Problem("Impossibile connettersi a RabbitMQ Management. Verifica che il plugin sia attivo.");
    }
});

// Elimina coda
protectedApi.MapDelete("/api/mgmt/queues/{name}", async (string name, IHttpClientFactory factory) =>
{
    var client = factory.CreateClient("RabbitMgmt");
    var response = await client.DeleteAsync($"queues/%2f/{name}");
    return response.IsSuccessStatusCode ? Results.NoContent() : Results.Problem("Errore eliminazione");
});

// Svuota (Purge) coda
protectedApi.MapPost("/api/mgmt/queues/{name}/purge", async (string name, IHttpClientFactory factory) =>
{
    var client = factory.CreateClient("RabbitMgmt");
    var response = await client.DeleteAsync($"queues/%2f/{name}/contents");
    return response.IsSuccessStatusCode ? Results.Ok() : Results.Problem("Errore purge");
});

// --- ESTENSIONE API DI MESSAGGISTICA ---

// 1. GET (Destructive): Legge e rimuove il primo messaggio dalla coda
protectedApi.MapPost("/api/messages/{queue}/receive", async (string queue, IHttpClientFactory factory) =>
{
    var client = factory.CreateClient("RabbitMgmt");
    // ackmode: 'ack_requeue_false' rimuove il messaggio dopo la lettura
    var payload = new { count = 1, ackmode = "ack_requeue_false", encoding = "auto" };

    var response = await client.PostAsJsonAsync($"queues/%2f/{queue}/get", payload);
    var messages = await response.Content.ReadFromJsonAsync<List<object>>();

    return messages != null && messages.Any() ? Results.Ok(messages.First()) : Results.NotFound("Coda vuota");
});

// 2. PEEK (Non-destructive): Legge i primi 100 messaggi senza rimuoverli
protectedApi.MapGet("/api/mgmt/queues/{name}/peek", async (string name, IHttpClientFactory factory) =>
{
    var client = factory.CreateClient("RabbitMgmt");
    // ackmode: 'ack_requeue_true' rimette i messaggi in coda dopo averli letti
    var payload = new
    {
        count = 100,
        ackmode = "ack_requeue_true",
        encoding = "auto",
        truncate = 50000
    };

    var response = await client.PostAsJsonAsync($"queues/%2f/{name}/get", payload);

    if (!response.IsSuccessStatusCode) return Results.Problem("Errore nel recupero messaggi");

    var messages = await response.Content.ReadFromJsonAsync<List<object>>();
    return Results.Ok(messages);
});

// 3. POST (Management): Scrive sulla coda usando l'API di Management (alternativa a MassTransit)
protectedApi.MapPost("/api/mgmt/queues/{name}/publish", async (string name, [FromBody] dynamic message, IHttpClientFactory factory) =>
{
    var client = factory.CreateClient("RabbitMgmt");
    var payload = new
    {
        properties = new { },
        routing_key = name,
        payload = System.Text.Json.JsonSerializer.Serialize(message),
        payload_encoding = "string"
    };

    var response = await client.PostAsJsonAsync($"exchanges/%2f/amq.default/publish", payload);
    return response.IsSuccessStatusCode ? Results.Ok(new { status = "Pubblicato via Mgmt" }) : Results.BadRequest();
});

app.Run();

// --- DB INITIALIZER ---
public static class DbInitializer
{
    public static void Init(string masterConn, string dbName)
    {
        try
        {
            using var c = new SqlConnection(masterConn);
            c.Open();
            // 1. Ensure Database exists
            new SqlCommand($"IF NOT EXISTS (SELECT * FROM sys.databases WHERE name='{dbName}') CREATE DATABASE [{dbName}]", c).ExecuteNonQuery();

            // 2. Switch context to the new/existing DB
            c.ChangeDatabase(dbName);

            // 3. Ensure Table exists
            string tableSql = @"
                IF NOT EXISTS (SELECT * FROM sys.objects WHERE name='AppLogging') 
                CREATE TABLE AppLogging (
                    Id INT IDENTITY PRIMARY KEY, 
                    Timestamp DATETIME DEFAULT GETUTCDATE(), 
                    Level NVARCHAR(50), 
                    Message NVARCHAR(MAX), 
                    ConnectionId NVARCHAR(100)
                )";
            new SqlCommand(tableSql, c).ExecuteNonQuery();

            Console.WriteLine($"✅ Database [{dbName}] and tables initialized.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ DB Init Error: {ex.Message}");
        }
    }
}

// --- SHARED MODELS & EXTENSIONS ---
public record VaultKey(string AppName, string KeyName, string LocalConfigName);
public static class VaultExtensions
{
    public static async Task LoadVaultSecrets(this IConfiguration configuration, VaultKey[] requests)
    {
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1111") };
        while (true)
        {
            try
            {
                if ((await client.GetAsync("/ready")).IsSuccessStatusCode) break;
            }
            catch { /* Wait for Vault */ }
            await Task.Delay(1000);
        }
        foreach (var req in requests)
        {
            var res = await client.GetFromJsonAsync<VaultResponse>($"/get-secret?app={req.AppName}&key={req.KeyName}");
            if (res != null) configuration[req.LocalConfigName] = res.Value;
        }
    }
    private record VaultResponse(string Value);
}
public record LoginRequest(string Username, string Password);
public record SecretLoginRequest(string Secret);
public class LoginStatus { public int Attempts { get; set; } public bool IsLocked { get; set; } public DateTime? LockoutEnd { get; set; } }

namespace RabbitMQAPIMiddleware.Models
{
    // Questo trucco sposta il tipo fuori dal namespace System
    public class DynamicMessage : Dictionary<string, object> { }
}