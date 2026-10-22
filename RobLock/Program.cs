using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using QRCoder;

// ---------------------------------------------------------------
//  RobLock back end (C#)
//  - one admin account (password is set the first time you open /admin.html)
//  - admin generates a random 5-digit session id for a device, shown as a QR code
//  - whoever holds the session id gets a page with only Lock and Unlock
//  - data lives in data/db.json (swap the Db class for a real database later)
//  The front end is plain HTML in wwwroot/ and Private/.
// ---------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "roblock_admin";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        // API calls should get a 401, not a redirect to a login page
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<Limiter>();

var app = builder.Build();

app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/dashboard"))
        ctx.Response.Headers.CacheControl = "no-store";
    await next();
});

app.UseDefaultFiles();   // "/" serves wwwroot/index.html
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// ---- admin dashboard page (only served to a logged-in admin) ----

app.MapGet("/dashboard", (HttpContext ctx, IWebHostEnvironment env) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Redirect("/admin.html");
    return Results.File(Path.Combine(env.ContentRootPath, "Private", "dashboard.html"), "text/html; charset=utf-8");
});

app.MapGet("/admin", () => Results.Redirect("/admin.html"));

var api = app.MapGroup("/api");

// ---- admin account: setup, login, logout ----

api.MapGet("/admin/status", (Db db) => Results.Ok(new { configured = db.AdminConfigured() }));

api.MapPost("/admin/setup", async (PasswordRequest req, HttpContext ctx, Db db) =>
{
    var password = req.Password ?? "";
    if (password.Length < 8)
        return Results.Json(new { error = "Password needs to be at least 8 characters." }, statusCode: 400);
    if (!db.TrySetupAdmin(password))
        return Results.Json(new { error = "The admin account is already set up." }, statusCode: 409);
    await Auth.SignInAdmin(ctx);
    return Results.Ok(new { ok = true });
});

api.MapPost("/admin/login", async (PasswordRequest req, HttpContext ctx, Db db, Limiter limiter) =>
{
    var key = "login:" + Auth.Ip(ctx);
    if (limiter.Blocked(key)) return Auth.TooMany();
    if (!db.AdminConfigured())
        return Results.Json(new { error = "The admin account isn't set up yet." }, statusCode: 409);
    if (!db.VerifyAdmin(req.Password ?? ""))
    {
        limiter.Fail(key);
        return Results.Json(new { error = "Incorrect password." }, statusCode: 401);
    }
    limiter.Reset(key);
    await Auth.SignInAdmin(ctx);
    return Results.Ok(new { ok = true });
});

api.MapPost("/admin/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { ok = true });
});

api.MapGet("/admin/me", (HttpContext ctx) =>
    ctx.User.Identity?.IsAuthenticated == true
        ? Results.Ok(new { admin = true })
        : Results.Json(new { admin = false }, statusCode: 401));

// ---- admin-only API ----

var admin = api.MapGroup("/admin").RequireAuthorization();

admin.MapGet("/dashboard", (Db db) => Results.Ok(db.GetDashboard()));

admin.MapPost("/sessions", (CreateSessionRequest req, Db db) =>
{
    var (view, error, status) = db.CreateSession(req.DeviceId ?? "");
    return view is null
        ? Results.Json(new { error }, statusCode: status)
        : Results.Ok(view);
});

admin.MapPost("/sessions/{id}/end", (string id, Db db) =>
    db.EndSession(id)
        ? Results.Ok(new { ok = true })
        : Results.Json(new { error = "Session not found or already ended." }, statusCode: 404));

admin.MapGet("/sessions/{id}/qr.svg", (string id, HttpContext ctx, Db db) =>
{
    if (db.GetSessionView(id) is null) return Results.NotFound();
    var url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/control.html?s={id}";
    return Results.Content(Qr.Svg(url), "image/svg+xml");
});

admin.MapPost("/devices/{id}/{action}", (string id, string action, Db db) =>
{
    var view = db.DeviceAction(id, action);
    return view is null
        ? Results.Json(new { error = "Unknown device or action." }, statusCode: 404)
        : Results.Ok(view);
});

// ---- user API (anyone holding a valid 5-digit session id) ----

api.MapGet("/session/{id}", (string id, HttpContext ctx, Db db, Limiter limiter) =>
    Handlers.UserLookup(id, ctx, db, limiter));

api.MapPost("/session/{id}/join", (string id, HttpContext ctx, Db db, Limiter limiter) =>
    Handlers.UserJoin(id, ctx, db, limiter));

api.MapPost("/session/{id}/lock", (string id, HttpContext ctx, Db db, Limiter limiter) =>
    Handlers.UserCommand(id, "lock", ctx, db, limiter));

api.MapPost("/session/{id}/unlock", (string id, HttpContext ctx, Db db, Limiter limiter) =>
    Handlers.UserCommand(id, "unlock", ctx, db, limiter));

app.Run();

// ===============================================================
//  Request and response shapes
// ===============================================================

record PasswordRequest(string? Password);
record CreateSessionRequest(string? DeviceId);

record SessionView(
    string Id, string DeviceId, string DeviceName, bool Locked, bool Buzzer,
    string Status, DateTime CreatedAt, DateTime ExpiresAt,
    DateTime? TimeIn, DateTime? TimeOut, string? EndReason);

record DeviceView(string Id, string Name, bool Locked, bool Buzzer, SessionView? ActiveSession);

record EventView(DateTime Time, string Kind, string Title, string Detail, string DeviceName, string? SessionId);

record DashboardData(List<DeviceView> Devices, List<SessionView> Sessions, List<EventView> Events, int SessionMinutes);

// ===============================================================
//  Stored data
// ===============================================================

class Device
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Locked { get; set; } = true;
    public bool Buzzer { get; set; }
}

class LockSession
{
    public string Id { get; set; } = "";            // the 5-digit session id
    public string DeviceId { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ClaimedAt { get; set; }        // "time in": first time the user opened it
    public DateTime? EndedAt { get; set; }          // "time out"
    public string? EndReason { get; set; }
}

class LogEvent
{
    public DateTime Time { get; set; }
    public string Kind { get; set; } = "info";      // ok | warn | info
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string? SessionId { get; set; }
}

class AdminRecord
{
    public string Salt { get; set; } = "";
    public string Hash { get; set; } = "";
}

class Store
{
    public List<Device> Devices { get; set; } = new();
    public List<LockSession> Sessions { get; set; } = new();
    public List<LogEvent> Events { get; set; } = new();
    public AdminRecord? Admin { get; set; }

    [JsonIgnore]
    public bool Dirty { get; set; }
}

// ===============================================================
//  Local JSON "database"
// ===============================================================

class Db
{
    public const int SessionMinutes = 120;   // a session stops working after this long

    private readonly string _path;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions FileOpts = new() { WriteIndented = true };

    public Db(IWebHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "db.json");

        if (!File.Exists(_path))
        {
            var store = new Store();
            store.Devices.Add(new Device { Id = "rack-a1", Name = "Rack A1" });
            store.Devices.Add(new Device { Id = "rack-a2", Name = "Rack A2" });
            store.Devices.Add(new Device { Id = "rack-b1", Name = "Rack B1" });
            store.Devices.Add(new Device { Id = "rack-b2", Name = "Rack B2" });
            Save(store);
        }
    }

    private Store Load() =>
        JsonSerializer.Deserialize<Store>(File.ReadAllText(_path), FileOpts) ?? new Store();

    private void Save(Store st)
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(st, FileOpts));
        File.Move(tmp, _path, true);
    }

    // Load, expire old sessions, run the work, save if anything changed.
    private T Run<T>(Func<Store, T> work, bool mutates = false)
    {
        lock (_gate)
        {
            var st = Load();
            Sweep(st);
            var result = work(st);
            if (mutates || st.Dirty) Save(st);
            return result;
        }
    }

    private static void Sweep(Store st)
    {
        var now = DateTime.UtcNow;
        foreach (var s in st.Sessions.Where(x => x.EndedAt is null && now >= x.ExpiresAt).ToList())
        {
            s.EndedAt = s.ExpiresAt;
            s.EndReason = "expired";
            Log(st, "info", "Session expired", "Time ran out", DeviceOf(st, s), s.Id, s.ExpiresAt);
        }
    }

    private static Device? DeviceOf(Store st, LockSession s) =>
        st.Devices.FirstOrDefault(d => d.Id == s.DeviceId);

    private static void Log(Store st, string kind, string title, string detail, Device? dev, string? sessionId, DateTime? at = null)
    {
        st.Events.Add(new LogEvent
        {
            Time = at ?? DateTime.UtcNow,
            Kind = kind,
            Title = title,
            Detail = detail,
            DeviceId = dev?.Id ?? "",
            DeviceName = dev?.Name ?? "",
            SessionId = sessionId,
        });
        if (st.Events.Count > 300) st.Events.RemoveRange(0, st.Events.Count - 300);
        st.Dirty = true;
    }

    private static LockSession? Active(Store st, string id)
    {
        if (id.Length != 5 || !id.All(c => c >= '0' && c <= '9')) return null;
        return st.Sessions.FirstOrDefault(s => s.Id == id && s.EndedAt is null);
    }

    private static SessionView View(Store st, LockSession s)
    {
        var dev = DeviceOf(st, s);
        var status = s.EndedAt is not null ? "ended" : s.ClaimedAt is null ? "waiting" : "active";
        return new SessionView(
            s.Id, s.DeviceId, dev?.Name ?? "Unknown device", dev?.Locked ?? true, dev?.Buzzer ?? false,
            status, s.CreatedAt, s.ExpiresAt, s.ClaimedAt, s.EndedAt, s.EndReason);
    }

    private static DeviceView DeviceViewOf(Store st, Device d)
    {
        var active = st.Sessions.FirstOrDefault(s => s.DeviceId == d.Id && s.EndedAt is null);
        return new DeviceView(d.Id, d.Name, d.Locked, d.Buzzer, active is null ? null : View(st, active));
    }

    private static void TrimSessions(Store st)
    {
        if (st.Sessions.Count <= 500) return;
        var drop = st.Sessions
            .Where(s => s.EndedAt is not null)
            .OrderBy(s => s.CreatedAt)
            .Take(st.Sessions.Count - 500)
            .ToList();
        foreach (var s in drop) st.Sessions.Remove(s);
    }

    // ---- admin account ----

    public bool AdminConfigured() => Run(st => st.Admin is not null);

    public bool TrySetupAdmin(string password) => Run(st =>
    {
        if (st.Admin is not null) return false;
        var (salt, hash) = Auth.HashPassword(password);
        st.Admin = new AdminRecord { Salt = salt, Hash = hash };
        st.Dirty = true;
        return true;
    });

    public bool VerifyAdmin(string password) =>
        Run(st => st.Admin is not null && Auth.Verify(st.Admin, password));

    // ---- dashboard ----

    public DashboardData GetDashboard() => Run(st =>
    {
        var devices = st.Devices.Select(d => DeviceViewOf(st, d)).ToList();
        var sessions = st.Sessions
            .OrderByDescending(s => s.CreatedAt)
            .Take(50)
            .Select(s => View(st, s))
            .ToList();
        var events = st.Events
            .OrderByDescending(e => e.Time)
            .Take(60)
            .Select(e => new EventView(e.Time, e.Kind, e.Title, e.Detail, e.DeviceName, e.SessionId))
            .ToList();
        return new DashboardData(devices, sessions, events, SessionMinutes);
    });

    // ---- sessions (admin side) ----

    public (SessionView? View, string? Error, int Status) CreateSession(string deviceId) =>
        Run<(SessionView?, string?, int)>(st =>
        {
            var dev = st.Devices.FirstOrDefault(d => d.Id == deviceId);
            if (dev is null) return (null, "Pick a device first.", 404);
            if (st.Sessions.Any(s => s.DeviceId == dev.Id && s.EndedAt is null))
                return (null, dev.Name + " already has an active session. End it first.", 409);

            string id;
            do
            {
                id = RandomNumberGenerator.GetInt32(10000, 100000).ToString();
            }
            while (st.Sessions.Any(s => s.Id == id));

            var now = DateTime.UtcNow;
            var session = new LockSession
            {
                Id = id,
                DeviceId = dev.Id,
                CreatedAt = now,
                ExpiresAt = now.AddMinutes(SessionMinutes),
            };
            st.Sessions.Add(session);
            Log(st, "info", "Session created", "QR code generated", dev, id);
            TrimSessions(st);
            return (View(st, session), null, 200);
        }, true);

    public bool EndSession(string id) => Run(st =>
    {
        var s = Active(st, id);
        if (s is null) return false;
        s.EndedAt = DateTime.UtcNow;
        s.EndReason = "ended by admin";
        Log(st, "info", "Session ended", "Ended by admin", DeviceOf(st, s), s.Id);
        return true;
    });

    public SessionView? GetSessionView(string id) => Run<SessionView?>(st =>
    {
        var s = Active(st, id);
        return s is null ? null : View(st, s);
    });

    public DeviceView? DeviceAction(string id, string action) => Run<DeviceView?>(st =>
    {
        var dev = st.Devices.FirstOrDefault(d => d.Id == id);
        if (dev is null) return null;
        var active = st.Sessions.FirstOrDefault(s => s.DeviceId == dev.Id && s.EndedAt is null);

        switch (action)
        {
            case "lock":
                dev.Locked = true;
                Log(st, "ok", "Locked", "By admin", dev, active?.Id);
                break;
            case "unlock":
                dev.Locked = false;
                Log(st, "ok", "Unlocked", "By admin", dev, active?.Id);
                break;
            case "buzzer-on":
                dev.Buzzer = true;
                Log(st, "warn", "Buzzer on", "By admin", dev, active?.Id);
                break;
            case "buzzer-off":
                dev.Buzzer = false;
                Log(st, "info", "Buzzer off", "By admin", dev, active?.Id);
                break;
            default:
                return null;
        }
        return DeviceViewOf(st, dev);
    }, true);

    // ---- sessions (user side) ----

    public SessionView? JoinSession(string id) => Run<SessionView?>(st =>
    {
        var s = Active(st, id);
        if (s is null) return null;
        if (s.ClaimedAt is null)
        {
            s.ClaimedAt = DateTime.UtcNow;
            Log(st, "ok", "Session started", "User opened the QR code", DeviceOf(st, s), s.Id);
        }
        return View(st, s);
    });

    public SessionView? UserCommand(string id, string action) => Run<SessionView?>(st =>
    {
        if (action != "lock" && action != "unlock") return null;
        var s = Active(st, id);
        var dev = s is null ? null : DeviceOf(st, s);
        if (s is null || dev is null) return null;

        if (s.ClaimedAt is null)
        {
            s.ClaimedAt = DateTime.UtcNow;
            Log(st, "ok", "Session started", "User opened the QR code", dev, s.Id);
        }

        var wantLocked = action == "lock";
        if (dev.Locked != wantLocked)
        {
            dev.Locked = wantLocked;
            Log(st, "ok", wantLocked ? "Locked" : "Unlocked", "By user", dev, s.Id);
        }
        return View(st, s);
    });
}

// ===============================================================
//  Helpers
// ===============================================================

static class Handlers
{
    private static IResult NotFoundOrEnded() =>
        Results.Json(new { error = "Session not found or ended." }, statusCode: 404);

    public static IResult UserLookup(string id, HttpContext ctx, Db db, Limiter limiter)
    {
        var key = "session:" + Auth.Ip(ctx);
        if (limiter.Blocked(key)) return Auth.TooMany();
        var view = db.GetSessionView(id);
        if (view is null) { limiter.Fail(key); return NotFoundOrEnded(); }
        return Results.Ok(view);
    }

    public static IResult UserJoin(string id, HttpContext ctx, Db db, Limiter limiter)
    {
        var key = "session:" + Auth.Ip(ctx);
        if (limiter.Blocked(key)) return Auth.TooMany();
        var view = db.JoinSession(id);
        if (view is null) { limiter.Fail(key); return NotFoundOrEnded(); }
        return Results.Ok(view);
    }

    public static IResult UserCommand(string id, string action, HttpContext ctx, Db db, Limiter limiter)
    {
        var key = "session:" + Auth.Ip(ctx);
        if (limiter.Blocked(key)) return Auth.TooMany();
        var view = db.UserCommand(id, action);
        if (view is null) { limiter.Fail(key); return NotFoundOrEnded(); }
        return Results.Ok(view);
    }
}

static class Auth
{
    private const int Iterations = 100_000;

    public static (string Salt, string Hash) HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return (Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public static bool Verify(AdminRecord admin, string password)
    {
        var salt = Convert.FromBase64String(admin.Salt);
        var expected = Convert.FromBase64String(admin.Hash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public static Task SignInAdmin(HttpContext ctx)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "admin"),
            new Claim(ClaimTypes.Role, "admin"),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    }

    public static string Ip(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public static IResult TooMany() =>
        Results.Json(new { error = "Too many tries. Wait a minute and try again." }, statusCode: 429);
}

// Slows down guessing: 12 failed tries per minute per address.
class Limiter
{
    private const int MaxFails = 12;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly Dictionary<string, List<DateTime>> _fails = new();
    private readonly object _gate = new();

    public bool Blocked(string key)
    {
        lock (_gate) return Prune(key).Count >= MaxFails;
    }

    public void Fail(string key)
    {
        lock (_gate) Prune(key).Add(DateTime.UtcNow);
    }

    public void Reset(string key)
    {
        lock (_gate) _fails.Remove(key);
    }

    private List<DateTime> Prune(string key)
    {
        if (!_fails.TryGetValue(key, out var list))
        {
            list = new List<DateTime>();
            _fails[key] = list;
        }
        var now = DateTime.UtcNow;
        list.RemoveAll(t => now - t > Window);
        return list;
    }
}

static class Qr
{
    public static string Svg(string text)
    {
        var generator = new QRCodeGenerator();
        var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(data);
        return svg.GetGraphic(8);
    }
}
